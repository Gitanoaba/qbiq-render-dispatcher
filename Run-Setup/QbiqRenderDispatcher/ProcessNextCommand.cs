using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QbiqRenderDispatcher
{
    /// <summary>
    /// Handler invoked when the user clicks "Process Next Render".
    ///
    /// v0.8 manual-mode flow:
    ///   1. Pick the next ticket (.json) from pending\
    ///   2. Resolve and validate skybox + view path XML
    ///   3. Close other open Revit docs to free memory
    ///   4. Open the .rvt
    ///   5. Activate the Revit view from manifest.revit_view
    ///   6. Auto-start Enscape, switch preset, skybox, load view path XML
    ///   7. Show TicketDialog (confirmation popup)
    ///   8. Trigger Video Export
    ///   9. Move ticket to processed\ (OK) or failed\ (error)
    ///  10. Write a log entry to logs\
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ProcessNextCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;

            Logger.Section("PROCESS NEXT RENDER");

            // ---------------------------------------------------------
            // Step 1: Make sure queue folders exist
            // ---------------------------------------------------------
            Logger.Step("Step 1: Ensure queue folders exist");
            string folderError;
            if (!JobQueue.EnsureFolders(out folderError))
            {
                Logger.Error("Step1", folderError);
                ShowError("Cannot access queue folders", folderError);
                return Result.Cancelled;
            }
            Logger.Info("Step1", "Queue folders OK at " + Config.DropboxRoot);

            // ---------------------------------------------------------
            // Step 2: Pick a ticket
            // ---------------------------------------------------------
            Logger.Step("Step 2: Pick a ticket from pending\\");
            int totalTickets = JobQueue.CountPendingTickets();
            int readyJobs    = JobQueue.CountReadyJobs();
            Logger.Info("Step2", "Pending tickets: " + totalTickets + " total, " + readyJobs + " ready");
            RenderJob job    = JobQueue.PickNextJob();

            if (totalTickets == 0)
            {
                ShowInfo("Queue is empty",
                    "No .json tickets in:\n" + Config.PendingDir + "\n\n" +
                    "Drop a .json ticket with rvt_path pointing to your .rvt file.");
                return Result.Succeeded;
            }

            if (job == null)
            {
                List<string> reasons = JobQueue.GetInvalidTicketReasons();
                string reasonsText = reasons.Count == 0
                    ? "(no diagnostic available)"
                    : string.Join("\n   - ", reasons.ToArray());

                ShowInfo("No ready tickets",
                    totalTickets + " ticket(s) in pending but none are ready.\n\n" +
                    "Reasons:\n   - " + reasonsText + "\n\n" +
                    "Each ticket must have:\n" +
                    "  - Valid JSON\n" +
                    "  - 'rvt_path' field pointing to an existing .rvt on disk");
                return Result.Succeeded;
            }

            string rvtName       = Path.GetFileName(job.RvtPath);
            string ticketName    = Path.GetFileName(job.ManifestPath);
            string projectFolder = Path.GetDirectoryName(job.RvtPath) ?? "";
            RenderManifest m     = job.Manifest;

            Logger.Info("Step2", "Picked ticket: " + ticketName);
            Logger.Info("Step2", "RVT path: " + job.RvtPath);
            Logger.Info("Step2", "Project folder: " + projectFolder);
            Logger.Info("Step2", "Manifest preset='" + (m.enscape_preset ?? "") +
                                 "' skybox='" + (m.skybox_file ?? "") +
                                 "' view_path_xml='" + (m.view_path_xml ?? "") +
                                 "' revit_view='" + (m.revit_view ?? "") + "'");

            // ---------------------------------------------------------
            // Step 3: Resolve and validate Enscape assets (fail-fast)
            // ---------------------------------------------------------
            Logger.Step("Step 3: Resolve assets");
            // Skybox: project folder first, then library
            Resolution skybox = ResolveAsset(m.skybox_file, projectFolder, Config.SkyboxLibraryRoot);
            if (!string.IsNullOrEmpty(m.skybox_file) && !skybox.Found)
            {
                ShowError("Skybox file not found",
                    "Manifest references skybox:\n   " + m.skybox_file + "\n\n" +
                    "Tried (in order):\n" +
                    "  1. Project folder: " + Path.Combine(projectFolder, m.skybox_file) + "\n" +
                    "  2. Library:        " + Path.Combine(Config.SkyboxLibraryRoot, m.skybox_file) + "\n\n" +
                    "Drop the file in the project folder, or save it in the library and check the filename (case-sensitive).");
                return Result.Failed;
            }

            // View Path XML: project folder only
            Resolution viewPath = ResolveAsset(m.view_path_xml, projectFolder, null);
            if (!string.IsNullOrEmpty(m.view_path_xml) && !viewPath.Found)
            {
                ShowError("Enscape View Path XML not found",
                    "Manifest references view path:\n   " + m.view_path_xml + "\n\n" +
                    "Resolved to:\n   " + viewPath.ResolvedPath + "\n\n" +
                    "Save the View Path .xml inside the project folder next to the .rvt.");
                return Result.Failed;
            }

            Logger.Info("Step3",
                "Skybox resolved: source=" + skybox.Source + " found=" + skybox.Found +
                " path='" + (skybox.ResolvedPath ?? "") + "'");
            Logger.Info("Step3",
                "ViewPath resolved: source=" + viewPath.Source + " found=" + viewPath.Found +
                " path='" + (viewPath.ResolvedPath ?? "") + "'");

            // ---------------------------------------------------------
            // Step 4: Close all currently open (non-linked) documents to free memory
            // ---------------------------------------------------------
            Logger.Step("Step 4-5: Close other docs and open the .rvt");
            List<Document> docsToClose = new List<Document>();
            foreach (Document d in uiApp.Application.Documents)
            {
                if (!d.IsLinked) docsToClose.Add(d);
            }
            Logger.Info("Step4", "Will close " + docsToClose.Count + " open document(s) after the new one loads");

            // ---------------------------------------------------------
            // Step 5: Open the new document from its real project folder
            // ---------------------------------------------------------
            UIDocument uiDoc = null;
            try
            {
                Logger.Info("Step5", "Calling OpenAndActivateDocument('" + job.RvtPath + "')...");
                uiDoc = uiApp.OpenAndActivateDocument(job.RvtPath);
                Logger.Info("Step5", "OpenAndActivateDocument returned successfully.");
            }
            catch (Exception ex)
            {
                Logger.Error("Step5", "OpenAndActivateDocument threw: " + ex.GetType().Name + " - " + ex.Message);
                ShowError("Failed to open document",
                    "File: " + rvtName + "\n" +
                    "Path: " + job.RvtPath + "\n\n" +
                    "Error: " + ex.GetType().Name + "\n" + ex.Message + "\n\n" +
                    "Common causes:\n" +
                    "  - File saved in a newer Revit version\n" +
                    "  - File still being synced by Dropbox (try again in 30 seconds)\n" +
                    "  - File is a workshared central in a network location not accessible\n" +
                    "  - File is corrupted");
                return Result.Failed;
            }

            Document doc = uiDoc.Document;

            // ---------------------------------------------------------
            // Step 6: Close the previously open docs (memory cleanup)
            // ---------------------------------------------------------
            int closedOk   = 0;
            int closedFail = 0;
            foreach (Document d in docsToClose)
            {
                if (d.Equals(doc)) continue;
                if (!string.IsNullOrEmpty(d.PathName) && !string.IsNullOrEmpty(doc.PathName) &&
                    string.Equals(d.PathName, doc.PathName, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    if (d.Close(false)) closedOk++;
                    else                closedFail++;
                }
                catch
                {
                    closedFail++;
                }
            }

            // ---------------------------------------------------------
            // Step 7: Activate the Revit view from the manifest (if any)
            // ---------------------------------------------------------
            Logger.Step("Step 7: Activate Revit view");
            string viewStatus = "(not specified, kept current view)";
            if (!string.IsNullOrEmpty(m.revit_view))
            {
                Logger.Info("Step7", "Looking for Revit view named '" + m.revit_view + "'");
                View target = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .FirstOrDefault(v => !v.IsTemplate && v.Name == m.revit_view);

                if (target == null)
                {
                    viewStatus = "NOT FOUND -> '" + m.revit_view + "' does not exist in this document";
                    Logger.Warn("Step7", viewStatus);
                }
                else
                {
                    try
                    {
                        uiDoc.ActiveView = target;
                        viewStatus = "OK -> " + target.Name + " (" + target.ViewType + ")";
                        Logger.Info("Step7", viewStatus);
                    }
                    catch (Exception ex)
                    {
                        viewStatus = "FAILED -> " + ex.Message;
                        Logger.Error("Step7", viewStatus);
                    }
                }
            }
            else
            {
                Logger.Info("Step7", "No revit_view in manifest - keeping current view");
            }

            // ---------------------------------------------------------
            // Step 8: Auto-start Enscape (needed for the popup to read presets)
            // ---------------------------------------------------------
            Logger.Step("Step 8: Auto-start Enscape");
            // Brief wait for Revit to finish activating the doc before we
            // try to click the Enscape ribbon button (it stays disabled
            // for 1-3s after OpenAndActivateDocument returns).
            Logger.Info("Step8", "Sleeping 2500ms for Revit ribbon to re-enable...");
            System.Threading.Thread.Sleep(2500);

            string enscapeStartStatus = "(not attempted)";
            System.Windows.Automation.AutomationElement enscapeWin = null;

            string startDiag;
            enscapeWin = EnscapeAutomation.GetOrStartEnscape(out startDiag);
            enscapeStartStatus = startDiag;
            Logger.Info("Step8", "GetOrStartEnscape diagnostic: " + startDiag);
            Logger.Info("Step8", "enscapeWin == null ? " + (enscapeWin == null));

            // ---------------------------------------------------------
            // Step 8b: If auto-start failed, prompt the user to start it
            // manually and then re-check.
            // ---------------------------------------------------------
            if (enscapeWin == null)
            {
                TaskDialog askDlg = new TaskDialog("Render Dispatcher")
                {
                    MainInstruction = "Please start Enscape manually",
                    MainContent =
                        "Auto-start failed. Please:\n\n" +
                        "1. Click the 'Enscapeâ„¢' tab in the Revit ribbon\n" +
                        "2. Click 'Start Enscape'\n" +
                        "3. Wait for the Enscape window to fully load\n" +
                        "4. Click 'Continue' below\n\n" +
                        "Diagnostic: " + enscapeStartStatus,
                    CommonButtons = TaskDialogCommonButtons.Cancel
                };
                askDlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                    "Continue (Enscape is now running)");

                TaskDialogResult ans = askDlg.Show();
                if (ans == TaskDialogResult.Cancel || ans == TaskDialogResult.Close)
                {
                    ShowInfo("Render cancelled",
                        "User cancelled before Enscape started. Ticket left in pending\\.");
                    return Result.Cancelled;
                }

                // Re-check after the user clicks Continue
                enscapeWin = EnscapeAutomation.GetOrStartEnscape(out startDiag);
                enscapeStartStatus = startDiag + "  [after manual prompt]";

                if (enscapeWin == null)
                {
                    ShowError("Still cannot find Enscape",
                        "After the manual prompt, the Enscape window still isn't detected.\n\n" +
                        "Diagnostic: " + startDiag + "\n\n" +
                        "Ticket left in pending\\. Try again once Enscape is fully loaded.");
                    return Result.Cancelled;
                }
            }

            // ---------------------------------------------------------
            // Step 9: Show the Render Ticket popup (v0.7.0)
            // ---------------------------------------------------------
            TicketDialog dlg = new TicketDialog(job);
            System.Windows.Forms.DialogResult dlgResult = dlg.ShowDialog();

            // The popup may be re-shown after the user opens Visual Settings.
            // Loop until the user clicks Render Now or Cancel.
            while (dlgResult == System.Windows.Forms.DialogResult.Retry)
            {
                if (enscapeWin != null)
                    EnscapeAutomation.OpenVisualSettingsPanel(enscapeWin);

                dlg = new TicketDialog(job);
                dlgResult = dlg.ShowDialog();
            }

            if (dlgResult != System.Windows.Forms.DialogResult.OK)
            {
                ShowInfo("Render cancelled",
                    "User cancelled before render started. Job ticket left in pending\\.");
                return Result.Cancelled;
            }

            // ---------------------------------------------------------
            // Step 10a: Slack notification - render started
            // ---------------------------------------------------------
            string projectName = NotEmpty(m.project, Path.GetFileNameWithoutExtension(rvtName));
            SlackNotifier.NotifyStarted(project: projectName, ticketName: ticketName);

            // ---------------------------------------------------------
            // Step 10-15: Hand off to the Idling-event coordinator.
            //
            // Enscape needs the Revit main thread to be free to fetch the
            // document's geometry. If we ran preset/skybox/xml/export
            // inline here (with Thread.Sleep retries), Revit's thread
            // would be blocked and Enscape could never finish loading the
            // model. The coordinator runs the automation across multiple
            // UIApplication.Idling events, yielding to Enscape between
            // steps so it can finish loading.
            // ---------------------------------------------------------
            if (enscapeWin == null)
            {
                MoveTicketToFolder(job.ManifestPath, Config.FailedDir, "_FAIL");
                SlackNotifier.NotifyFailed(projectName, ticketName,
                    "Enscape window not available: " + enscapeStartStatus);
                ShowError("Enscape not available",
                    "The Enscape window was not detected. Ticket moved to failed\\.\n\n" +
                    "Diagnostic: " + enscapeStartStatus);
                return Result.Failed;
            }

            RenderAutomationContext ctx = new RenderAutomationContext
            {
                EnscapeWindow        = enscapeWin,
                SelectedPreset       = dlg.SelectedPreset,
                SelectedSkyboxPath   = dlg.SelectedSkyboxPath,
                SkyboxRotation       = dlg.SkyboxRotation,
                SelectedViewPathFile = dlg.SelectedViewPathFile,
                OutputAbsolutePath   = dlg.OutputAbsolutePath,
                ManifestPath         = job.ManifestPath,
                TicketName           = ticketName,
                ProjectName          = projectName
            };

            RenderCoordinator.Start(uiApp, ctx);

            ShowInfo("Render queued",
                "The render is now running in the background.\n\n" +
                "RVT:    " + rvtName + "\n" +
                "Output: " + NotEmpty(dlg.OutputAbsolutePath, "(unknown)") + "\n\n" +
                "Enscape will finish loading and the render will start automatically. " +
                "Slack will notify you when it's done (or if anything fails). " +
                "Click 'View Log' for live diagnostic output.\n\n" +
                "Build: v0.9.5 | Slack: " +
                (string.IsNullOrEmpty(Config.SlackWebhookUrl) ? "disabled" : "enabled"));

            return Result.Succeeded;
        }

        /// <summary>
        /// Builds a one-line failure reason from the per-step statuses.
        /// Returns the first step that contains "FAIL".
        /// </summary>
        private static string BuildFailureReason(
            string enscapeStart, string preset, string skybox, string viewPath, string export)
        {
            if (enscapeStart != null && enscapeStart.IndexOf("Could not", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Enscape start: " + enscapeStart;
            if (preset != null   && preset.IndexOf("FAIL", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Preset switch: " + preset;
            if (skybox != null   && skybox.IndexOf("FAIL", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Skybox switch: " + skybox;
            if (viewPath != null && viewPath.IndexOf("FAIL", StringComparison.OrdinalIgnoreCase) >= 0)
                return "View Path XML: " + viewPath;
            if (export != null   && export.IndexOf("FAIL", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Video Export: " + export;
            return "Unknown step (check the log file)";
        }

        // ---------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------

        /// <summary>
        /// Asset resolution result. Tells where the file was found (or where we tried to find it).
        /// </summary>
        private struct Resolution
        {
            public string ResolvedPath;
            public string Source;   // "project", "library", "absolute", "not_specified"
            public bool Found;
        }

        /// <summary>
        /// Resolves an asset reference (skybox, xml, etc.) using a layered lookup:
        ///   1. If absolute, use as-is.
        ///   2. Try projectFolder + assetRef.
        ///   3. Try libraryFolder + assetRef (only if libraryFolder is provided).
        /// </summary>
        private static Resolution ResolveAsset(string assetRef, string projectFolder, string libraryFolder)
        {
            Resolution r = new Resolution();

            if (string.IsNullOrEmpty(assetRef))
            {
                r.Source = "not_specified";
                return r;
            }

            if (Path.IsPathRooted(assetRef))
            {
                r.ResolvedPath = assetRef;
                r.Source       = "absolute";
                r.Found        = File.Exists(assetRef);
                return r;
            }

            if (!string.IsNullOrEmpty(projectFolder))
            {
                string projectPath = Path.Combine(projectFolder, assetRef);
                if (File.Exists(projectPath))
                {
                    r.ResolvedPath = projectPath;
                    r.Source       = "project";
                    r.Found        = true;
                    return r;
                }
            }

            if (!string.IsNullOrEmpty(libraryFolder))
            {
                string libPath = Path.Combine(libraryFolder, assetRef);
                r.ResolvedPath = libPath;
                r.Source       = "library";
                r.Found        = File.Exists(libPath);
                return r;
            }

            // Nothing matched and no library to fall back to
            r.ResolvedPath = string.IsNullOrEmpty(projectFolder) ? assetRef : Path.Combine(projectFolder, assetRef);
            r.Source       = "project";
            r.Found        = false;
            return r;
        }

        private static string NotEmpty(string value, string fallback)
        {
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        /// <summary>
        /// Moves the ticket JSON to targetDir, appending suffix + timestamp.
        /// Returns the destination path, or null if the move fails.
        /// </summary>
        internal static string MoveTicketToFolder(string ticketPath, string targetDir, string suffix)
        {
            try
            {
                if (!File.Exists(ticketPath)) return null;
                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                string name = Path.GetFileNameWithoutExtension(ticketPath);
                string ext  = Path.GetExtension(ticketPath);
                string ts   = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string dest = Path.Combine(targetDir, name + suffix + "_" + ts + ext);
                int    n    = 0;
                while (File.Exists(dest))
                    dest = Path.Combine(targetDir, name + suffix + "_" + ts + "_" + (++n) + ext);

                File.Move(ticketPath, dest);
                return dest;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Appends a single log line to logs\dispatch.log.
        /// Non-throwing.
        /// </summary>
        internal static void WriteLog(string line)
        {
            try
            {
                string logFile = Path.Combine(Config.LogsDir, "dispatch.log");
                if (!Directory.Exists(Config.LogsDir)) Directory.CreateDirectory(Config.LogsDir);
                File.AppendAllText(logFile, line + Environment.NewLine);
            }
            catch { /* swallow - log failure must not break render flow */ }
        }


        // ============================================================
        // Auto-process entry point (called by AutoRenderHandler, no dialogs)
        // ============================================================
        /// <summary>
        /// Processes the next ready ticket entirely without UI prompts.
        /// Called by AutoRenderHandler when the background poll fires.
        /// On failure the ticket is moved to failed\ and an error is logged;
        /// no TaskDialog is shown so the render server stays unattended.
        /// </summary>
        internal static Result TryAutoProcess(UIApplication uiApp)
        {
            Logger.Section("AUTO PROCESS NEXT RENDER");

            // Step 1: queue folders
            string folderError;
            if (!JobQueue.EnsureFolders(out folderError))
            { Logger.Error("Auto.Step1", folderError); return Result.Failed; }

            // Step 2: pick ticket and immediately claim it by moving to in-progress/
            // This prevents the auto-poll from picking the same ticket again on the next timer tick.
            RenderJob job = JobQueue.PickNextJob();
            if (job == null)
            { Logger.Info("Auto.Step2", "No ready ticket found."); return Result.Succeeded; }

            // Move to in-progress/ preserving the original filename (no timestamp added here).
            // This claims the ticket so the next poll won't pick it up again.
            string inProgressPath = System.IO.Path.Combine(
                Config.InProgressDir, System.IO.Path.GetFileName(job.ManifestPath));
            try
            {
                if (!System.IO.Directory.Exists(Config.InProgressDir))
                    System.IO.Directory.CreateDirectory(Config.InProgressDir);
                System.IO.File.Move(job.ManifestPath, inProgressPath);
                job.ManifestPath = inProgressPath;
                Logger.Info("Auto.Step2", "Ticket claimed: moved to in-progress/");
            }
            catch (Exception ex)
            {
                Logger.Error("Auto.Step2", "Could not move ticket to in-progress/: " + ex.Message + " â€” skipping to avoid double-processing.");
                return Result.Failed;
            }

            string rvtName       = System.IO.Path.GetFileName(job.RvtPath);
            string ticketName    = System.IO.Path.GetFileName(job.ManifestPath);
            // job.RvtPath is already remapped by JobQueue.PickNextJob
            string projectFolder = System.IO.Path.GetDirectoryName(job.RvtPath) ?? "";
            RenderManifest m     = job.Manifest;
            // Remap any absolute asset paths baked into the manifest
            if (m.skybox_file   != null) m.skybox_file   = Config.RemapPath(m.skybox_file);
            if (m.view_path_xml != null) m.view_path_xml = Config.RemapPath(m.view_path_xml);

            // Auto-create a directory junction so decals / linked files
            // stored under the sender's username resolve on this machine.
            EnsureDropboxJunction(m.sender_username, m.sender_dropbox_root);

            Logger.Info("Auto.Step2", "Ticket: " + ticketName + " | RVT: " + job.RvtPath);

            // Step 3: resolve assets (fail-fast, no dialog)
            Resolution skybox   = ResolveAsset(m.skybox_file,   projectFolder, Config.SkyboxLibraryRoot);
            Resolution viewPath = ResolveAsset(m.view_path_xml, projectFolder, null);

            if (!string.IsNullOrEmpty(m.skybox_file) && !skybox.Found)
            {
                Logger.Error("Auto.Step3", "Skybox not found: " + m.skybox_file);
                MoveTicketToFolder(job.ManifestPath, Config.FailedDir, "_FAIL");
                SlackNotifier.NotifyFailed(m.project ?? rvtName, ticketName, "Skybox not found: " + m.skybox_file);
                return Result.Failed;
            }
            if (!string.IsNullOrEmpty(m.view_path_xml) && !viewPath.Found)
            {
                Logger.Error("Auto.Step3", "View path XML not found: " + m.view_path_xml);
                MoveTicketToFolder(job.ManifestPath, Config.FailedDir, "_FAIL");
                SlackNotifier.NotifyFailed(m.project ?? rvtName, ticketName, "View path XML not found: " + m.view_path_xml);
                return Result.Failed;
            }

            // Step 4-5: close other docs, open the RVT
            var docsToClose = new System.Collections.Generic.List<Autodesk.Revit.DB.Document>();
            foreach (Autodesk.Revit.DB.Document d in uiApp.Application.Documents)
                if (!d.IsLinked) docsToClose.Add(d);

            UIDocument uiDoc;
            try
            {
                Logger.Info("Auto.Step5", "Opening: " + job.RvtPath);
                uiDoc = uiApp.OpenAndActivateDocument(job.RvtPath);
            }
            catch (Exception ex)
            {
                Logger.Error("Auto.Step5", "OpenAndActivateDocument threw: " + ex.Message);
                MoveTicketToFolder(job.ManifestPath, Config.FailedDir, "_FAIL");
                SlackNotifier.NotifyFailed(m.project ?? rvtName, ticketName, "Cannot open RVT: " + ex.Message);
                return Result.Failed;
            }

            Autodesk.Revit.DB.Document doc = uiDoc.Document;

            foreach (Autodesk.Revit.DB.Document d in docsToClose)
            {
                if (d.Equals(doc)) continue;
                try { d.Close(false); } catch { }
            }

            // Step 7: activate Revit view
            if (!string.IsNullOrEmpty(m.revit_view))
            {
                var target = new Autodesk.Revit.DB.FilteredElementCollector(doc)
                    .OfClass(typeof(Autodesk.Revit.DB.View))
                    .Cast<Autodesk.Revit.DB.View>()
                    .FirstOrDefault(v => !v.IsTemplate && v.Name == m.revit_view);
                if (target != null)
                    try { uiDoc.ActiveView = target; } catch { }
            }

            // Step 8: start Enscape (no manual-prompt fallback in auto mode)
            System.Threading.Thread.Sleep(2500);
            string startDiag;
            System.Windows.Automation.AutomationElement enscapeWin =
                EnscapeAutomation.GetOrStartEnscape(out startDiag);

            if (enscapeWin == null)
            {
                Logger.Error("Auto.Step8", "Enscape not available: " + startDiag);
                MoveTicketToFolder(job.ManifestPath, Config.FailedDir, "_FAIL");
                SlackNotifier.NotifyFailed(m.project ?? rvtName, ticketName, "Enscape start failed: " + startDiag);
                return Result.Failed;
            }

            // Step 9 (SKIPPED in auto mode - no dialog, settings come straight from manifest)
            string projectName  = NotEmpty(m.project, System.IO.Path.GetFileNameWithoutExtension(rvtName));
            string outputName   = TicketDialog.DeriveOutputFilename(job.RvtPath);
            string outputPath   = System.IO.Path.Combine(projectFolder, outputName);
            string resolvedSkyboxPath   = skybox.Found   ? skybox.ResolvedPath   : null;
            string resolvedViewPathFile = viewPath.Found ? viewPath.ResolvedPath : null;

            SlackNotifier.NotifyStarted(project: projectName, ticketName: ticketName);

            // Step 10: hand off to RenderCoordinator (Idling-event driven)
            var ctx = new RenderAutomationContext
            {
                EnscapeWindow        = enscapeWin,
                SelectedPreset       = (m.enscape_preset ?? "").Trim(),
                SelectedSkyboxPath   = resolvedSkyboxPath,
                SkyboxRotation       = m.skybox_rotation,
                SelectedViewPathFile = resolvedViewPathFile,
                OutputAbsolutePath   = outputPath,
                ManifestPath         = job.ManifestPath,
                TicketName           = ticketName,
                ProjectName          = projectName
            };

            RenderCoordinator.Start(uiApp, ctx);
            Logger.Info("Auto.Step10", "Handed off to RenderCoordinator. Running unattended.");
            return Result.Succeeded;
        }


        /// <summary>
        /// Creates a directory junction C:\Users\{senderUser}\Q Dropbox
        /// pointing to this machine's Q Dropbox, so any absolute paths
        /// embedded in the .rvt (decals, linked files) resolve correctly.
        /// Safe to call multiple times - skips if junction already exists.
        /// </summary>
        private static void EnsureDropboxJunction(string senderUsername, string senderDropboxRoot)
        {
            try
            {
                if (string.IsNullOrEmpty(senderUsername) || string.IsNullOrEmpty(senderDropboxRoot))
                {
                    Logger.Info("Junction", "No sender info in ticket â€” skipping junction creation.");
                    return;
                }

                // The junction we want: C:\Users\{sender}\Q Dropbox
                string junctionPath  = System.IO.Path.Combine(@"C:\Users", senderUsername, "Q Dropbox");
                string junctionParent = System.IO.Path.Combine(@"C:\Users", senderUsername);

                // Render PC's own Q Dropbox (two levels up from queue root)
                string localQDropbox = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(Config.DropboxRoot, "..", ".."));

                // Skip if sender == this machine (no self-junction needed)
                if (string.Equals(senderDropboxRoot, localQDropbox,
                        StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Info("Junction", "Sender is this machine â€” no junction needed.");
                    return;
                }

                // Skip if junction already exists
                if (System.IO.Directory.Exists(junctionPath))
                {
                    Logger.Info("Junction", "Junction already exists: " + junctionPath);
                    return;
                }

                Logger.Info("Junction", "Creating junction for sender '" + senderUsername + "'...");

                // Create parent directory if needed
                if (!System.IO.Directory.Exists(junctionParent))
                    System.IO.Directory.CreateDirectory(junctionParent);

                // mklink /J does not require admin rights for directory junctions
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                    string.Format("/c mklink /J \"{0}\" \"{1}\"",
                                  junctionPath, localQDropbox))
                {
                    UseShellExecute  = false,
                    CreateNoWindow   = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true
                };

                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    string stdout = proc.StandardOutput.ReadToEnd();
                    string stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(8000);
                    Logger.Info("Junction",
                        string.Format("mklink exit={0} out='{1}' err='{2}'",
                                      proc.ExitCode, stdout.Trim(), stderr.Trim()));
                }

                if (System.IO.Directory.Exists(junctionPath))
                    Logger.Info("Junction", "Junction created: " + junctionPath + " -> " + localQDropbox);
                else
                    Logger.Warn("Junction", "Junction creation may have failed â€” check log above.");
            }
            catch (Exception ex)
            {
                Logger.Warn("Junction", "EnsureDropboxJunction threw: " + ex.Message);
            }
        }

        private static void ShowInfo(string title, string content)
        {
            TaskDialog dlg = new TaskDialog("Render Dispatcher")
            {
                MainInstruction = title,
                MainContent     = content,
                CommonButtons   = TaskDialogCommonButtons.Ok
            };
            dlg.Show();
        }

        private static void ShowError(string title, string content)
        {
            TaskDialog dlg = new TaskDialog("Render Dispatcher")
            {
                MainInstruction = title,
                MainContent     = content,
                CommonButtons   = TaskDialogCommonButtons.Ok,
                MainIcon        = TaskDialogIcon.TaskDialogIconError
            };
            dlg.Show();
        }
    }
}
