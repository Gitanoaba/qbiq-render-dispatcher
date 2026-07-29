using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Automation;

namespace QbiqRenderDispatcher
{
    /// <summary>
    /// Holds state for an in-progress render that needs to continue across
    /// Idling events.  Set by ProcessNextCommand once the user clicks
    /// "Render Now"; read and updated by the coordinator's Idling handler.
    /// </summary>
    public class RenderAutomationContext
    {
        public AutomationElement EnscapeWindow;

        public string SelectedPreset;
        public string SelectedSkyboxPath;
        public int    SkyboxRotation;
        public string SelectedViewPathFile;
        public string OutputAbsolutePath;

        public string ManifestPath;
        public string TicketName;
        public string ProjectName;

        public DateTime StartedAt;
        public int      IdleCallCount;
        public int      IdleReconnectCount;   // how many times we re-fetched the Enscape window
        public int      LastVsClickIdle;
        public bool     VisualSettingsOpened;
        public DateTime VisualSettingsOpenedAt;

        // Status strings used in the final log line + Slack notification
        public string PresetStatus          = "(pending)";
        public string SkyboxStatus          = "(pending)";
        public string SkyboxRotationStatus  = "(pending)";
        public string ViewPathStatus        = "(pending)";
        public string ExportStatus    = "(pending)";
        // Phase 3: waiting for output file
        public bool     ExportTriggered;
        public DateTime ExportTriggeredAt;
        public DateTime LastFileCheckAt;
        public long     LastFileSizeBytes  = -1;
        public DateTime FileSizeStableAt;
    }

    /// <summary>
    /// Runs the per-render automation (preset, skybox, view path, export)
    /// across multiple UIApplication.Idling events instead of in one big
    /// IExternalCommand call.
    ///
    /// Why: Enscape needs the Revit main thread to be free in order to
    /// fetch the active document's geometry. While our IExternalCommand is
    /// running (blocking on Thread.Sleep retries), Revit cannot fire its
    /// Idling event, so Enscape never receives the cycles it needs to
    /// finish loading. Symptom observed by the user: Visual Settings panel
    /// fails to open until the failure dialog is dismissed (which lets the
    /// command return).
    ///
    /// Solution: ProcessNextCommand kicks off Enscape, shows the dialog,
    /// then immediately returns Result.Succeeded after handing off to this
    /// coordinator. Each Idling tick we either click 'GuiVisualSettings' or,
    /// once the panel is open, run preset/skybox/xml/export inline. Between
    /// our Idling ticks Revit can hand cycles to Enscape, so it can finish
    /// loading the model.
    /// </summary>
    public static class RenderCoordinator
    {
        private static volatile RenderAutomationContext _ctx;
        /// <summary>True while a render is in progress.</summary>
        public static bool IsBusy { get { return _ctx != null; } }

        // Time allowed for Phase 1+2 (open VS, preset, skybox, view path, trigger export).
        // After export is triggered we switch to ExportTimeoutSeconds.
        private const int MaxTotalSeconds    = 600;   // 10 min for setup phases
        // Time allowed for the .mp4 to finish writing once export is triggered.
        // Set to 3 hours â€” enough for the longest fly-through video.
        private const int ExportTimeoutSeconds = 10800; // 3 hours

        // Click GuiVisualSettings every Nth Idling tick (Revit fires Idling
        // many times per second under light load).
        private const int VsClickEveryNIdles = 12;

        // After Visual Settings opens for the first time, wait this long
        // before starting preset/skybox/xml/export. This lets Enscape finish
        // its initial render of the model so subsequent UI interactions are
        // not contending with geometry conversion.
        private const int PostVsOpenDelaySeconds = 45;

        public static void Start(UIApplication uiApp, RenderAutomationContext ctx)
        {
            if (uiApp == null || ctx == null) return;

            ctx.StartedAt     = DateTime.Now;
            ctx.IdleCallCount = 0;
            _ctx = ctx;

            Logger.Info("Coordinator",
                "Idling-driven automation started. Project='" + ctx.ProjectName +
                "' Ticket='" + ctx.TicketName + "'");

            uiApp.Idling += OnIdling;
        }

        private static void OnIdling(object sender, IdlingEventArgs e)
        {
            UIApplication uiApp = sender as UIApplication;
            RenderAutomationContext ctx = _ctx;

            if (ctx == null || uiApp == null)
            {
                if (uiApp != null) uiApp.Idling -= OnIdling;
                return;
            }

            try
            {
                ctx.IdleCallCount++;

                // Bail out on timeout â€” use a longer limit once export has been triggered
                double elapsed    = (DateTime.Now - ctx.StartedAt).TotalSeconds;
                int    timeLimit  = ctx.ExportTriggered ? ExportTimeoutSeconds : MaxTotalSeconds;
                if (elapsed > timeLimit)
                {
                    Logger.Error("Coordinator",
                        string.Format("TIMEOUT after {0}s (limit={1}s). Aborting render.", (int)elapsed, timeLimit));
                    Finalize(uiApp, false, string.Format("Timeout after {0}s", (int)elapsed));
                    return;
                }

                // Phase 3: export triggered â€” wait for output file to appear and stabilise.
                if (ctx.ExportTriggered)
                {
                    // Throttle checks to every 5 s
                    if ((DateTime.Now - ctx.LastFileCheckAt).TotalSeconds < 5)
                    { e.SetRaiseWithoutDelay(); return; }
                    ctx.LastFileCheckAt = DateTime.Now;

                    double waitSec = (DateTime.Now - ctx.ExportTriggeredAt).TotalSeconds;

                    if (string.IsNullOrEmpty(ctx.OutputAbsolutePath))
                    { Finalize(uiApp, false, "No output path â€” cannot verify export."); return; }

                    bool exists = File.Exists(ctx.OutputAbsolutePath);
                    long size   = exists ? new System.IO.FileInfo(ctx.OutputAbsolutePath).Length : 0;

                    if (!exists || size == 0)
                    {
                        if (ctx.IdleCallCount % 40 == 0)
                            Logger.Info("Coordinator", string.Format(
                                "Waiting for file ({0:N0}s elapsed): {1}", waitSec, ctx.OutputAbsolutePath));
                        e.SetRaiseWithoutDelay(); return;
                    }

                    if (size != ctx.LastFileSizeBytes)
                    {
                        ctx.LastFileSizeBytes = size;
                        ctx.FileSizeStableAt  = DateTime.Now;
                        Logger.Info("Coordinator", string.Format(
                            "File growing: {0:N0} bytes ({1:N0}s elapsed)", size, waitSec));
                        e.SetRaiseWithoutDelay(); return;
                    }

                    double stableSec = (DateTime.Now - ctx.FileSizeStableAt).TotalSeconds;
                    if (stableSec < 10) { e.SetRaiseWithoutDelay(); return; }

                    Logger.Info("Coordinator", string.Format(
                        "Output file stable {0:N0}s at {1:N0} bytes â€” render complete.", stableSec, size));
                    Finalize(uiApp, true, "");
                    return;
                }

                // Phase 1: try to open the Visual Settings panel.
                // We don't block here - if it doesn't open, just yield and
                // try again on the next idle tick. That gives Enscape time
                // to finish loading the model.
                if (!ctx.VisualSettingsOpened)
                {
                    if (TryOpenVisualSettings(ctx))
                    {
                        ctx.VisualSettingsOpened   = true;
                        ctx.VisualSettingsOpenedAt = DateTime.Now;
                        Logger.Info("Coordinator",
                            "Visual Settings is OPEN (idle #" + ctx.IdleCallCount +
                            ", " + (int)elapsed + "s elapsed). Waiting " +
                            PostVsOpenDelaySeconds + "s before starting automation so " +
                            "Enscape can finish first render.");
                    }

                    // Keep idling firing fast so we don't wait forever
                    e.SetRaiseWithoutDelay();
                    return;
                }

                // Phase 1b: post-VS-open settle window.
                // After Visual Settings opens, give Enscape a chance to
                // finish its initial geometry render before we start
                // poking the preset switcher.
                double sinceVsOpen = (DateTime.Now - ctx.VisualSettingsOpenedAt).TotalSeconds;
                if (sinceVsOpen < PostVsOpenDelaySeconds)
                {
                    // Log progress every ~5s so the operator sees we're alive
                    if (ctx.IdleCallCount % 80 == 0)
                    {
                        Logger.Info("Coordinator",
                            "Settling after VS open: " + (int)sinceVsOpen + "/" +
                            PostVsOpenDelaySeconds + "s elapsed (idle #" +
                            ctx.IdleCallCount + ")");
                    }
                    e.SetRaiseWithoutDelay();
                    return;
                }

                // Phase 2: Visual Settings is open, Enscape's UI is responsive.
                // Run the actual render automation inline. The internal
                // Thread.Sleeps in these methods are OK now - geometry is
                // already loaded so Enscape doesn't need more idle cycles.
                Logger.Info("Coordinator",
                    "Running render automation (preset/skybox/xml/export). " +
                    "idle #" + ctx.IdleCallCount + ", " + (int)elapsed + "s elapsed.");

                if (!RunPresetSwitch(ctx))      { Finalize(uiApp, false, "Preset switch: "     + ctx.PresetStatus);          return; }
                if (!RunSkyboxSwitch(ctx))      { Finalize(uiApp, false, "Skybox switch: "     + ctx.SkyboxStatus);          return; }
                RunSkyboxRotation(ctx); // non-fatal: log warning but continue if rotation fails
                if (!RunViewPathLoad(ctx))      { Finalize(uiApp, false, "View Path XML: "     + ctx.ViewPathStatus);        return; }
                if (!RunVideoExport(ctx))    { Finalize(uiApp, false, "Video Export: "   + ctx.ExportStatus);   return; }

                // Export triggered â€” hand off to Phase 3 (wait for file)
                ctx.ExportTriggered   = true;
                ctx.ExportTriggeredAt = DateTime.Now;
                ctx.LastFileCheckAt   = DateTime.Now;
                ctx.FileSizeStableAt  = DateTime.Now;
                Logger.Info("Coordinator",
                    "Export triggered. Waiting for output file: " + ctx.OutputAbsolutePath);
                e.SetRaiseWithoutDelay();
            }
            catch (System.Windows.Automation.ElementNotAvailableException exStale)
            {
                // Enscape window handle became stale (typically happens when Enscape
                // re-attaches to the newly opened document). Re-fetch and retry.
                ctx.IdleReconnectCount++;
                Logger.Warn("Coordinator",
                    "Enscape element stale (reconnect #" + ctx.IdleReconnectCount + "): " + exStale.Message);
                if (ctx.IdleReconnectCount > 3)
                {
                    Finalize(uiApp, false, "Enscape element unavailable after 3 reconnect attempts.");
                    return;
                }
                string reDiag;
                System.Windows.Automation.AutomationElement fresh =
                    EnscapeAutomation.GetOrStartEnscape(out reDiag);
                if (fresh != null)
                {
                    ctx.EnscapeWindow        = fresh;
                    ctx.VisualSettingsOpened = false;
                    ctx.LastVsClickIdle      = 0;
                    Logger.Info("Coordinator", "Enscape window refreshed: " + reDiag);
                }
                else
                {
                    Finalize(uiApp, false, "Lost Enscape window and could not recover: " + reDiag);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Coordinator", "Unhandled exception: " + ex.GetType().Name + " - " + ex.Message);
                Finalize(uiApp, false, "Exception: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------
        // Phase 1 helper
        // ---------------------------------------------------------------
        private static bool TryOpenVisualSettings(RenderAutomationContext ctx)
        {
            // Already open?
            AutomationElement panel = ctx.EnscapeWindow.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ClassNameProperty, "VisualSettingsWindow"));
            if (panel != null) return true;

            // Click GuiVisualSettings every Nth idle tick.
            // No Thread.Sleep here: we just trigger the click and return.
            // If Enscape ignored it (still loading), the next tick will retry.
            if (ctx.IdleCallCount - ctx.LastVsClickIdle >= VsClickEveryNIdles)
            {
                AutomationElement btn = ctx.EnscapeWindow.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "GuiVisualSettings"));
                if (btn != null)
                {
                    ctx.LastVsClickIdle = ctx.IdleCallCount;
                    Logger.Info("Coordinator",
                        "Click GuiVisualSettings (idle #" + ctx.IdleCallCount + ")");
                    EnscapeAutomation.ClickElement(btn);
                }
                else
                {
                    Logger.Warn("Coordinator",
                        "GuiVisualSettings button not in tree on idle #" + ctx.IdleCallCount);
                }
            }
            return false;
        }

        // ---------------------------------------------------------------
        // Phase 2 helpers - return false on hard failure
        // ---------------------------------------------------------------
        private static bool RunPresetSwitch(RenderAutomationContext ctx)
        {
            if (string.IsNullOrEmpty(ctx.SelectedPreset))
            {
                ctx.PresetStatus = "(no preset selected)";
                return true;
            }
            AutomationResult r = EnscapeAutomation.SwitchPreset(ctx.EnscapeWindow, ctx.SelectedPreset);
            ctx.PresetStatus = (r.Ok ? "OK -> " : "FAILED -> ") + r.Message;
            Logger.Info("Coordinator", "Preset: " + ctx.PresetStatus);
            return r.Ok;
        }

        private static bool RunSkyboxSwitch(RenderAutomationContext ctx)
        {
            if (string.IsNullOrEmpty(ctx.SelectedSkyboxPath))
            {
                ctx.SkyboxStatus = "(no skybox selected)";
                return true;
            }
            AutomationResult r = EnscapeAutomation.SwitchSkyboxViaSendKeys(
                ctx.EnscapeWindow, ctx.SelectedSkyboxPath);
            ctx.SkyboxStatus = (r.Ok ? "OK -> " : "FAILED -> ") + r.Message;
            Logger.Info("Coordinator", "Skybox: " + ctx.SkyboxStatus);
            return r.Ok;
        }

        private static bool RunSkyboxRotation(RenderAutomationContext ctx)
        {
            if (ctx.SkyboxRotation == 0)
            {
                ctx.SkyboxRotationStatus = "(0Â° â€” skipped)";
                return true;
            }
            AutomationResult r = EnscapeAutomation.SetSkyboxRotation(
                ctx.EnscapeWindow, ctx.SkyboxRotation);
            ctx.SkyboxRotationStatus = (r.Ok ? "OK -> " : "FAILED -> ") + r.Message;
            Logger.Info("Coordinator", "Skybox rotation: " + ctx.SkyboxRotationStatus);
            return r.Ok;
        }

        private static bool RunViewPathLoad(RenderAutomationContext ctx)
        {
            if (string.IsNullOrEmpty(ctx.SelectedViewPathFile))
            {
                ctx.ViewPathStatus = "(no view path)";
                return true;
            }
            AutomationResult r = EnscapeAutomation.LoadViewPathXml(
                ctx.EnscapeWindow, ctx.SelectedViewPathFile);
            ctx.ViewPathStatus = (r.Ok ? "OK -> " : "FAILED -> ") + r.Message;
            Logger.Info("Coordinator", "ViewPath: " + ctx.ViewPathStatus);
            return r.Ok;
        }

        private static bool RunVideoExport(RenderAutomationContext ctx)
        {
            if (string.IsNullOrEmpty(ctx.OutputAbsolutePath))
            {
                ctx.ExportStatus = "(no output path)";
                return false;
            }
            AutomationResult r = EnscapeAutomation.TriggerVideoExport(
                ctx.EnscapeWindow, ctx.OutputAbsolutePath);
            ctx.ExportStatus = (r.Ok ? "OK -> " : "FAILED -> ") + r.Message;
            Logger.Info("Coordinator", "Export: " + ctx.ExportStatus);
            return r.Ok;
        }

        // ---------------------------------------------------------------
        // Finalize: move ticket, write log, send Slack, unhook idling.
        // ---------------------------------------------------------------
        private static void Finalize(UIApplication uiApp, bool ok, string failureReason)
        {
            RenderAutomationContext ctx = _ctx;
            try
            {
                if (ctx == null) return;

                string dest = ok
                    ? ProcessNextCommand.MoveTicketToFolder(ctx.ManifestPath, Config.ProcessedDir, "_OK")
                    : ProcessNextCommand.MoveTicketToFolder(ctx.ManifestPath, Config.FailedDir,    "_FAIL");

                ProcessNextCommand.WriteLog(
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | " +
                    (ok ? "OK   " : "FAIL ") + " | " +
                    ctx.TicketName + " | " +
                    "preset=" + Trunc(ctx.PresetStatus,          30) + " | " +
                    "skybox=" + Trunc(ctx.SkyboxStatus,          30) + " | " +
                    "rotation=" + Trunc(ctx.SkyboxRotationStatus, 20) + " | " +
                    "export=" + Trunc(ctx.ExportStatus,          50));

                if (ok)
                {
                    SlackNotifier.NotifyCompleted(
                        ctx.ProjectName,
                        ctx.OutputAbsolutePath ?? "(unknown)",
                        ctx.TicketName);
                }
                else
                {
                    SlackNotifier.NotifyFailed(
                        ctx.ProjectName,
                        ctx.TicketName,
                        string.IsNullOrEmpty(failureReason) ? "Unknown failure" : failureReason);
                }

                Logger.Info("Coordinator",
                    "Finalized. ok=" + ok + ". Ticket dest=" + (dest ?? "(left in pending)"));
            }
            catch (Exception ex)
            {
                Logger.Error("Coordinator", "Exception in Finalize: " + ex.Message);
            }
            finally
            {
                // Wait for Enscape to finish writing the .mp4 BEFORE closing it.
                // TriggerVideoExport only kicks off the encoding; the actual
                // render takes minutes-to-tens-of-minutes. If we Kill() Enscape
                // before the file is done, the .mp4 is corrupted or missing.
                // We only wait on successful renders (ok=true) - on failure
                // there's no point waiting.
                if (ok && ctx != null && !string.IsNullOrEmpty(ctx.OutputAbsolutePath))
                {
                    try
                    {
                        EnscapeAutomation.WaitForVideoExportComplete(
                            ctx.OutputAbsolutePath, /* maxMinutes */ 120);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("Coordinator",
                            "WaitForVideoExportComplete threw: " + ex.Message +
                            " - proceeding to CloseSession anyway.");
                    }
                }

                // Clean up: close Enscape and unload the document to free RAM.
                // Revit itself stays open so the add-in keeps polling.
                CloseSession(uiApp, ctx);
                _ctx = null;
                if (uiApp != null) uiApp.Idling -= OnIdling;
            }
        }


        // ---------------------------------------------------------------
        // CloseSession: shut down Enscape and unload the document after
        // each render so the render PC returns to a clean idle state.
        // ---------------------------------------------------------------
        // Win32 P/Invoke for sending WM_CLOSE to a window
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private const uint WM_CLOSE = 0x0010;

        private static void CloseSession(UIApplication uiApp, RenderAutomationContext ctx)
        {
            // 1. Close Enscape via its window (not by process name - the
            //    Enscape executable can be named differently across
            //    installs, so finding-by-process-name was unreliable).
            //
            //    Strategy:
            //      (a) Find the RendererWindow via UI Automation
            //      (b) Read its PID + HWND
            //      (c) PostMessage WM_CLOSE (graceful shutdown)
            //      (d) If still alive after 5 s, Process.Kill() by PID
            //      (e) Fallback: scan all processes whose name contains
            //          "Enscape" and kill them
            try
            {
                AutomationElement enscapeWin = EnscapeAutomation.FindEnscapeRendererWindowPublic(2000);

                if (enscapeWin != null)
                {
                    int    pid  = 0;
                    IntPtr hwnd = IntPtr.Zero;
                    try { pid  = enscapeWin.Current.ProcessId; }         catch { }
                    try { hwnd = new IntPtr(enscapeWin.Current.NativeWindowHandle); } catch { }

                    Logger.Info("Cleanup",
                        "Found Enscape RendererWindow (PID=" + pid + " HWND=" + hwnd.ToInt64() + "). Sending WM_CLOSE.");

                    if (hwnd != IntPtr.Zero)
                    {
                        try { PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); } catch { }
                        Thread.Sleep(3000);
                    }

                    // If the process is still alive, kill by PID
                    if (pid > 0)
                    {
                        try
                        {
                            var proc = System.Diagnostics.Process.GetProcessById(pid);
                            if (!proc.HasExited)
                            {
                                Logger.Warn("Cleanup",
                                    "Enscape PID " + pid + " (name='" + proc.ProcessName +
                                    "') still alive after WM_CLOSE. Killing.");
                                proc.Kill();
                                proc.WaitForExit(5000);
                            }
                            Logger.Info("Cleanup",
                                "Enscape closed. Process name was '" + proc.ProcessName + "' (for future reference).");
                        }
                        catch (ArgumentException)
                        {
                            Logger.Info("Cleanup", "Enscape PID " + pid + " already exited - good.");
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn("Cleanup", "Could not finalize PID " + pid + ": " + ex.Message);
                        }
                    }
                }
                else
                {
                    // No RendererWindow found - either Enscape is genuinely closed
                    // or its window is in a weird state. Scan for any process
                    // whose name contains "Enscape" as a defensive fallback.
                    int killed = 0;
                    foreach (var p in System.Diagnostics.Process.GetProcesses())
                    {
                        try
                        {
                            if (p.ProcessName.IndexOf("Enscape", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                Logger.Info("Cleanup",
                                    "No window found but process '" + p.ProcessName + "' (PID " + p.Id + ") matches - killing.");
                                p.Kill();
                                killed++;
                            }
                        }
                        catch { }
                    }
                    if (killed == 0)
                        Logger.Info("Cleanup", "No Enscape window, no Enscape-named processes. Already clean.");
                    else
                        Logger.Info("Cleanup", "Killed " + killed + " Enscape-named process(es) via fallback scan.");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Cleanup", "Exception during Enscape close: " + ex.Message);
            }

            // 2. Close Revit documents.
            //
            //    Document.Close(false) works for any document EXCEPT the active
            //    one when it's the only one open (Revit requires at least one
            //    document loaded). For that case we have to use the built-in
            //    "Close" menu command via PostCommand - that one CAN close the
            //    last document and return Revit to the Home dashboard.
            try
            {
                if (uiApp != null)
                {
                    Autodesk.Revit.DB.Document activeDoc = null;
                    try { activeDoc = uiApp.ActiveUIDocument != null ? uiApp.ActiveUIDocument.Document : null; }
                    catch { }

                    // 2a. Close all non-active, non-linked docs via the API
                    int closedDirectly = 0;
                    var toClose = new System.Collections.Generic.List<Autodesk.Revit.DB.Document>();
                    foreach (Autodesk.Revit.DB.Document d in uiApp.Application.Documents)
                    {
                        if (d.IsLinked) continue;
                        if (activeDoc != null && d.Equals(activeDoc)) continue;
                        toClose.Add(d);
                    }
                    foreach (var d in toClose)
                    {
                        try
                        {
                            if (d.Close(false)) closedDirectly++;
                        }
                        catch { }
                    }
                    Logger.Info("Cleanup",
                        "Closed " + closedDirectly + " non-active document(s) directly.");

                    // 2b. Close the active document via the Revit menu command.
                    // Document.Close(false) on the active doc returns false silently
                    // when it's the only doc open - Revit needs at least one doc.
                    // PostCommand schedules the built-in "Close" command which
                    // can close the last document and return to the Home screen.
                    if (activeDoc != null)
                    {
                        try
                        {
                            var closeCmdId = Autodesk.Revit.UI.RevitCommandId.LookupPostableCommandId(
                                Autodesk.Revit.UI.PostableCommand.Close);
                            if (closeCmdId != null && uiApp.CanPostCommand(closeCmdId))
                            {
                                // Spawn a background thread BEFORE posting Close so it's ready
                                // to dismiss the "Save changes?" dialog without human intervention.
                                System.Threading.Thread dismissThread = new System.Threading.Thread(() =>
                                {
                                    DismissSaveDialog(timeoutMs: 10000);
                                });
                                dismissThread.IsBackground = true;
                                dismissThread.Start();

                                uiApp.PostCommand(closeCmdId);
                                Logger.Info("Cleanup",
                                    "Posted PostableCommand.Close - dismiss thread watching for Save dialog.");
                            }
                            else
                            {
                                Logger.Warn("Cleanup",
                                    "Cannot post Close command (closeCmdId=" +
                                    (closeCmdId == null ? "null" : "ok") +
                                    "). Active document will stay open.");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn("Cleanup", "PostCommand(Close) threw: " + ex.Message);
                        }
                    }
                    else
                    {
                        Logger.Info("Cleanup", "No active document - server already at Home.");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Cleanup", "Could not close documents: " + ex.Message);
            }
        }

        /// <summary>
        /// Watches for a Revit "Save changes?" dialog and clicks "Don't Save" automatically.
        /// Runs on a background thread after PostableCommand.Close is posted.
        /// Tries button texts: "Don't Save", "No", "Nicht speichern" (localization fallback).
        /// </summary>
        private static void DismissSaveDialog(int timeoutMs)
        {
            int elapsed = 0;
            int pollMs  = 300;
            string[] dontSaveTexts = new[] { "Don't Save", "Dont Save", "No", "Nicht speichern", "No guardar", "Ne pas enregistrer" };

            while (elapsed < timeoutMs)
            {
                try
                {
                    // Look for any dialog window whose title or content suggests a save prompt
                    var desktop = AutomationElement.RootElement;
                    var dialogs = desktop.FindAll(TreeScope.Children,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));

                    foreach (AutomationElement dialog in dialogs)
                    {
                        string title = "";
                        try { title = dialog.Current.Name; } catch { continue; }

                        // Revit save dialogs typically have "Revit" or the project name in title
                        // and contain a "Don't Save" or "No" button
                        var buttons = dialog.FindAll(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

                        foreach (AutomationElement btn in buttons)
                        {
                            string btnName = "";
                            try { btnName = btn.Current.Name; } catch { continue; }

                            foreach (string candidate in dontSaveTexts)
                            {
                                if (string.Equals(btnName.Trim(), candidate, StringComparison.OrdinalIgnoreCase))
                                {
                                    try
                                    {
                                        var ip = btn.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
                                        if (ip != null)
                                        {
                                            ip.Invoke();
                                            Logger.Info("Cleanup", "Save dialog dismissed: clicked '" + btnName + "' in dialog '" + title + "'.");
                                            return;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Logger.Warn("Cleanup", "DismissSaveDialog: failed to click '" + btnName + "': " + ex.Message);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("Cleanup", "DismissSaveDialog poll threw: " + ex.Message);
                }

                System.Threading.Thread.Sleep(pollMs);
                elapsed += pollMs;
            }

            Logger.Warn("Cleanup", "DismissSaveDialog: no Save dialog found within " + timeoutMs + "ms.");
        }

        private static string Trunc(string s, int n)
        {
            if (s == null) return "";
            return s.Length <= n ? s : s.Substring(0, n);
        }
    }
}
