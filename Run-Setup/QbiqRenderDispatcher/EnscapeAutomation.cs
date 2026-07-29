using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

namespace QbiqRenderDispatcher
{
    /// <summary>
    /// Outcome of an automation operation. Carries success/failure plus a
    /// human-readable message so the calling command can show it to the user.
    /// </summary>
    public class AutomationResult
    {
        public bool Ok;
        public string Message;
        public static AutomationResult Success(string msg) { return new AutomationResult { Ok = true,  Message = msg }; }
        public static AutomationResult Failure(string msg) { return new AutomationResult { Ok = false, Message = msg }; }
    }

    /// <summary>
    /// UI Automation against the Enscape Avalonia UI.
    ///
    /// Enscape 4.17 uses Avalonia, exposing stable AutomationIds for many key
    /// elements (GuiVisualSettings, PresetsSearch, PresetsList, SettingsTabs,
    /// PART_SelectFileButton, etc.). We use those whenever possible; we fall
    /// back to text/class matching only where unavoidable.
    /// </summary>
    public static class EnscapeAutomation
    {
        // ----- Tunables -----
        private const int FindEnscapeQuickTimeoutMs   = 2000;     // poll for an already-running Enscape window
        private const int FindEnscapeAfterStartTimeoutMs = 90000;  // 90s: ample for Enscape first-load (typically 30-60s)
        private const int EnscapeReadyTimeoutMs        = 120000;   // 2 min: wait for Enscape UI/toolbar to be fully loaded after window appears
        private const int VisualSettingsOpenTimeoutMs = 5000;
        private const int SearchSettleMs              = 600;      // give the list time to filter
        private const int ClickSettleMs               = 400;
        private const int PollIntervalMs              = 250;
        private const int PresetApplySettleMs         = 3000;     // wait for preset to fully apply visually
        private const int SkyboxApplySettleMs         = 10000;    // Enscape can take ~10s to load+apply a skybox to the scene

        // ============================================================
        // Public API
        // ============================================================

        /// <summary>
        /// Returns an Enscape RendererWindow if one is running. Starts Enscape
        /// (via Revit's Enscape ribbon button) if not.
        /// Retries up to 4 times with a 3s pause to handle the case where the
        /// Revit ribbon is briefly disabled while a document is still being loaded.
        /// </summary>
        public static AutomationElement GetOrStartEnscape(out string diagnostic)
        {
            diagnostic = "";
            Logger.Step("GetOrStartEnscape");

            Logger.Info("GetOrStartEnscape", "Quick check: is Enscape RendererWindow already running?");
            AutomationElement existing = FindEnscapeRendererWindow(FindEnscapeQuickTimeoutMs);
            if (existing != null)
            {
                Logger.Info("GetOrStartEnscape", "Enscape window FOUND - already running. Verifying UI is ready...");
                bool readyExisting = WaitForEnscapeReady(existing, 5000); // quick check, should be ready
                Logger.Info("GetOrStartEnscape", "UI ready check (already-running): " + readyExisting);
                diagnostic = "Enscape was already running.";
                return existing;
            }
            Logger.Info("GetOrStartEnscape", "No Enscape window found. Will try to auto-start.");

            const int MaxAttempts  = 4;
            const int RetryDelayMs = 3000;
            string lastError = "";

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                Logger.Info("GetOrStartEnscape",
                    string.Format("Attempt {0}/{1}: clicking 'Start Enscape' in Revit ribbon...",
                                  attempt, MaxAttempts));
                diagnostic = string.Format(
                    "Enscape not running. Clicking 'Start Enscape' (attempt {0}/{1})...",
                    attempt, MaxAttempts);

                string startError;
                bool clicked = ClickStartEnscapeInRevit(out startError);
                lastError = startError;

                Logger.Info("GetOrStartEnscape",
                    string.Format("Attempt {0}: ClickStartEnscapeInRevit returned {1}. Error='{2}'",
                                  attempt, clicked, startError));

                if (clicked)
                {
                    Logger.Info("GetOrStartEnscape",
                        "Click succeeded. Waiting up to " + (FindEnscapeAfterStartTimeoutMs / 1000) +
                        "s for Enscape RendererWindow to appear...");
                    AutomationElement started = FindEnscapeRendererWindow(FindEnscapeAfterStartTimeoutMs);
                    if (started != null)
                    {
                        Logger.Info("GetOrStartEnscape",
                            string.Format("Enscape window detected on attempt {0}.", attempt));

                        // CRITICAL: the RendererWindow appears very early (during the splash screen).
                        // Enscape's UI (toolbar, Visual Settings button) takes another 30-90 seconds
                        // to be fully ready. Poll for the GuiVisualSettings toolbar button to verify
                        // Enscape is interactable before returning.
                        Logger.Info("GetOrStartEnscape",
                            "Waiting up to " + (EnscapeReadyTimeoutMs / 1000) +
                            "s for Enscape UI/toolbar to finish loading (splash screen to disappear)...");
                        bool ready = WaitForEnscapeReady(started, EnscapeReadyTimeoutMs);
                        if (!ready)
                        {
                            Logger.Warn("GetOrStartEnscape",
                                "Enscape window appeared but GuiVisualSettings toolbar button never showed up. " +
                                "Proceeding anyway - subsequent steps may fail.");
                        }
                        else
                        {
                            Logger.Info("GetOrStartEnscape",
                                "Enscape UI is ready - GuiVisualSettings toolbar button is accessible.");
                        }

                        diagnostic = string.Format("Enscape started (attempt {0}/{1}). UI ready: {2}",
                                                   attempt, MaxAttempts, ready);
                        return started;
                    }
                    Logger.Warn("GetOrStartEnscape", "Click registered but RendererWindow did not appear within timeout.");
                }

                if (attempt < MaxAttempts)
                {
                    Logger.Info("GetOrStartEnscape", "Waiting " + RetryDelayMs + "ms before next attempt...");
                    Thread.Sleep(RetryDelayMs);
                }
            }

            // Final check - maybe it loaded slowly
            Logger.Info("GetOrStartEnscape", "Retry loop exhausted. Final quick check for Enscape window...");
            AutomationElement late = FindEnscapeRendererWindow(FindEnscapeQuickTimeoutMs);
            if (late != null)
            {
                Logger.Info("GetOrStartEnscape", "Enscape window detected on final check.");
                diagnostic = "Enscape started (detected after retry loop).";
                return late;
            }

            Logger.Error("GetOrStartEnscape",
                "FAILED after " + MaxAttempts + " attempts. Last click error: " + lastError);
            diagnostic = "Could not auto-start Enscape after " + MaxAttempts + " attempts.\n" +
                         "Last error: " + lastError + "\n\n" +
                         "Make sure the 'Enscape' tab is visible in the Revit ribbon, " +
                         "then click 'Process Next Render' again.";
            return null;
        }

        /// <summary>
        /// Switches to the Visual Settings preset whose name matches presetName.
        /// Strategy: open Visual Settings panel -> type name in PresetsSearch ->
        /// click first ListBoxItem in PresetsList -> verify by reading the
        /// Heading1 element ('Qbiq preset' style).
        /// </summary>
        public static AutomationResult SwitchPreset(AutomationElement enscapeWindow, string presetName)
        {
            if (enscapeWindow == null)
                return AutomationResult.Failure("Enscape window is null.");

            if (string.IsNullOrEmpty(presetName))
                return AutomationResult.Failure("No preset name provided.");

            // 1. Make sure Visual Settings panel is open
            AutomationResult openResult = EnsureVisualSettingsOpen(enscapeWindow);
            if (!openResult.Ok) return openResult;

            // 2. Find the Visual Settings panel
            AutomationElement visualSettings = WaitForElement(enscapeWindow,
                new PropertyCondition(AutomationElement.ClassNameProperty, "VisualSettingsWindow"),
                VisualSettingsOpenTimeoutMs);
            if (visualSettings == null)
                return AutomationResult.Failure("Visual Settings panel not found after opening.");

            // 3. Find the search box
            AutomationElement searchBox = visualSettings.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "PresetsSearch"));
            if (searchBox == null)
                return AutomationResult.Failure("Could not find PresetsSearch box.");

            // 4. Set search text
            if (!TrySetText(searchBox, presetName, out string setError))
                return AutomationResult.Failure("Failed to type into PresetsSearch: " + setError);

            Thread.Sleep(SearchSettleMs);

            // 5. Find PresetsList and the first list item
            AutomationElement presetsList = visualSettings.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "PresetsList"));
            if (presetsList == null)
            {
                ClearText(searchBox);
                return AutomationResult.Failure("Could not find PresetsList.");
            }

            AutomationElement firstItem = presetsList.FindFirst(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            if (firstItem == null)
            {
                ClearText(searchBox);
                return AutomationResult.Failure(
                    "No matching preset found for '" + presetName + "'.\n\n" +
                    "Make sure the preset exists in this project's Enscape Visual Settings " +
                    "(it must be saved INTO the .rvt before the render).");
            }

            // 6. Select the item
            if (!TrySelectListItem(firstItem, out string selectError))
            {
                ClearText(searchBox);
                return AutomationResult.Failure("Failed to select preset: " + selectError);
            }

            Thread.Sleep(ClickSettleMs);

            // 7. Clear the search
            ClearText(searchBox);
            Thread.Sleep(150);

            // 8. Verify by reading the Heading1 (the title that shows the active preset name)
            string activePreset = ReadActivePresetName(visualSettings);
            if (string.IsNullOrEmpty(activePreset))
                return AutomationResult.Success(
                    "Preset selected (could not verify - active-preset heading not readable).");

            if (string.Equals(activePreset, presetName, StringComparison.OrdinalIgnoreCase))
            {
                // Let Enscape finish applying the preset (lighting, materials,
                // sky, etc.) before downstream operations like skybox switch
                // or Video Export trigger.
                Thread.Sleep(PresetApplySettleMs);
                return AutomationResult.Success(
                    "Preset switched to '" + activePreset + "' [verified, settled " +
                    (PresetApplySettleMs / 1000) + "s].");
            }

            return AutomationResult.Failure(
                "Preset clicked, but heading shows '" + activePreset + "' instead of '" + presetName + "'.");
        }

        /// <summary>
        /// Loads a skybox file into the active Visual Settings preset.
        /// Strategy: open Visual Settings -> click 'Sky' tab -> click
        /// PART_SelectFileButton -> wait for OS file dialog -> type path ->
        /// click Open -> verify by reading PART_PathLink button name.
        /// </summary>
        public static AutomationResult SwitchSkybox(AutomationElement enscapeWindow, string skyboxAbsolutePath)
        {
            if (enscapeWindow == null)
                return AutomationResult.Failure("Enscape window is null.");

            if (string.IsNullOrEmpty(skyboxAbsolutePath))
                return AutomationResult.Failure("No skybox path provided.");

            if (!System.IO.File.Exists(skyboxAbsolutePath))
                return AutomationResult.Failure("Skybox file does not exist on disk: " + skyboxAbsolutePath);

            string expectedFileName = System.IO.Path.GetFileName(skyboxAbsolutePath);

            // 1. Make sure Visual Settings is open
            AutomationResult openResult = EnsureVisualSettingsOpen(enscapeWindow);
            if (!openResult.Ok) return openResult;

            AutomationElement visualSettings = WaitForElement(enscapeWindow,
                new PropertyCondition(AutomationElement.ClassNameProperty, "VisualSettingsWindow"),
                VisualSettingsOpenTimeoutMs);
            if (visualSettings == null)
                return AutomationResult.Failure("Visual Settings panel not found.");

            // 2. Idempotency check: if PART_PathLink already shows the right file, skip
            AutomationElement existingPathLink = FindPathLink(visualSettings);
            if (existingPathLink != null)
            {
                try
                {
                    string current = existingPathLink.Current.Name;
                    if (string.Equals(current, expectedFileName, StringComparison.OrdinalIgnoreCase))
                        return AutomationResult.Success("Skybox already set to '" + current + "', no change needed.");
                }
                catch { /* ignore - we'll proceed to set it */ }
            }

            // 3. Click the 'Sky' tab
            AutomationElement tabControl = visualSettings.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsTabs"));
            if (tabControl == null)
                return AutomationResult.Failure("Could not find SettingsTabs.");

            AutomationElement skyTab = FindTabItemByText(tabControl, "Sky");
            if (skyTab == null)
                return AutomationResult.Failure("Could not find the 'Sky' tab inside SettingsTabs.");

            string tabError;
            if (!TrySelectListItem(skyTab, out tabError))
                return AutomationResult.Failure("Failed to select Sky tab: " + tabError);

            Thread.Sleep(ClickSettleMs);

            // 4. Find and click the browse button (PART_SelectFileButton)
            AutomationElement browseBtn = WaitForElement(visualSettings,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "PART_SelectFileButton"),
                3000);
            if (browseBtn == null)
                return AutomationResult.Failure("Could not find PART_SelectFileButton on the Sky tab.");

            // Snapshot ALL window handles BEFORE clicking, including nested
            // children of RendererWindow (Avalonia exposes Visual Settings and
            // file dialogs as nested windows, not always top-level).
            HashSet<int> handlesBefore = SnapshotAllWindowHandles(enscapeWindow);

            string clickError;
            if (!TryInvoke(browseBtn, out clickError))
                return AutomationResult.Failure("Failed to click browse button: " + clickError);

            // 5. Wait for the file dialog (top-level OR nested inside RendererWindow)
            string detectionDiag;
            AutomationElement fileDialog = WaitForFileDialog(
                handlesBefore, enscapeWindow, 12000, out detectionDiag);

            if (fileDialog == null)
                return AutomationResult.Failure(
                    "File dialog did not appear within 12s after clicking browse.\n\n" +
                    "Diagnostic (windows seen during last poll):\n" + detectionDiag +
                    "\nIf you see a dialog open, Cancel it manually and report.");

            // 6. Type the path and submit
            AutomationResult submitResult = TypePathAndOpen(fileDialog, skyboxAbsolutePath);
            if (!submitResult.Ok) return submitResult;

            // 7. Wait briefly for Enscape to apply the skybox
            Thread.Sleep(800);

            // 8. Verify by re-reading PART_PathLink
            AutomationElement pathLinkAfter = WaitForElement(visualSettings,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "PART_PathLink"),
                4000);
            if (pathLinkAfter == null)
                return AutomationResult.Success("Skybox path submitted (could not verify - PART_PathLink not found).");

            string newName = "";
            try { newName = pathLinkAfter.Current.Name; } catch { /* leave empty */ }

            if (string.Equals(newName, expectedFileName, StringComparison.OrdinalIgnoreCase))
                return AutomationResult.Success("Skybox loaded: '" + newName + "' [verified].");

            return AutomationResult.Failure(
                "Browse dialog submitted, but PART_PathLink shows '" + newName +
                "' instead of '" + expectedFileName + "'.");
        }

        /// <summary>
        /// Sets the skybox rotation in Enscape Visual Settings (Sky tab).
        /// Strategy:
        ///   1. Open Visual Settings and select the Sky tab (same as SwitchSkybox).
        ///   2. Search for a numeric input with AutomationId "PART_RotationAngle"
        ///      (the most likely candidate in Enscape 4.x Avalonia UI).
        ///   3. If found: set via ValuePattern, then verify.
        ///   4. If not found: log all editable descendants so we can identify
        ///      the correct AutomationId and update this method.
        /// A rotation of 0 is a no-op (caller skips this call for 0Â°).
        /// </summary>
        public static AutomationResult SetSkyboxRotation(AutomationElement enscapeWindow, int degrees)
        {
            if (enscapeWindow == null)
                return AutomationResult.Failure("Enscape window is null.");

            degrees = ((degrees % 360) + 360) % 360; // normalise to 0-359

            // 1. Open Visual Settings
            AutomationResult openResult = EnsureVisualSettingsOpen(enscapeWindow);
            if (!openResult.Ok) return openResult;

            AutomationElement visualSettings = WaitForElement(enscapeWindow,
                new PropertyCondition(AutomationElement.ClassNameProperty, "VisualSettingsWindow"),
                5000);
            if (visualSettings == null)
                return AutomationResult.Failure("VisualSettingsWindow not found.");

            // 2. Click the Sky tab
            AutomationElement tabControl = visualSettings.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsTabs"));
            if (tabControl == null)
                return AutomationResult.Failure("Could not find SettingsTabs.");

            AutomationElement skyTab = FindTabItemByText(tabControl, "Sky");
            if (skyTab == null)
                return AutomationResult.Failure("Could not find the 'Sky' tab.");

            string tabError;
            if (!TrySelectListItem(skyTab, out tabError))
                return AutomationResult.Failure("Failed to select Sky tab: " + tabError);

            Thread.Sleep(ClickSettleMs);

            // 3. Find the rotation input.
            // Enscape 4.x uses AutomationId="PART_TextBox" Class="NumericTextBox" for numeric
            // inputs on the Sky tab (confirmed via dispatch.log diagnosis). There are two such
            // fields: the first is the skybox rotation angle, the second is something else.
            // We locate PART_PathLink (the skybox filename label) and take the first NumericTextBox
            // that appears after it in the visual tree.
            AutomationElement rotationField = null;

            try
            {
                var allNumeric = visualSettings.FindAll(TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "PART_TextBox"),
                        new PropertyCondition(AutomationElement.ClassNameProperty, "NumericTextBox")));

                // Skip the PresetsSearch box and take the first NumericTextBox (rotation angle)
                if (allNumeric.Count > 0)
                    rotationField = allNumeric[0];
            }
            catch (Exception ex)
            {
                Logger.Warn("SetSkyboxRotation", "Error finding NumericTextBox: " + ex.Message);
            }

            if (rotationField == null)
                return AutomationResult.Failure("Could not find NumericTextBox (rotation field) on the Sky tab.");

            Logger.Info("SetSkyboxRotation", "Found rotation field (NumericTextBox[0]) on Sky tab.");

            // 5. Set via ValuePattern
            try
            {
                ValuePattern vp = rotationField.GetCurrentPattern(ValuePattern.Pattern) as ValuePattern;
                if (vp != null)
                {
                    vp.SetValue(degrees.ToString());
                    Thread.Sleep(500);
                    string set = vp.Current.Value;
                    if (set == degrees.ToString())
                        return AutomationResult.Success("Rotation set to " + degrees + "Â° via ValuePattern [verified].");
                    return AutomationResult.Success("Rotation submitted (" + degrees + "Â°), read back: '" + set + "' (may not match due to Enscape formatting).");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("SetSkyboxRotation", "ValuePattern failed: " + ex.Message + " â€” falling back to SendKeys.");
            }

            // 6. Fallback: click field + SendKeys
            try
            {
                string clickErr;
                TryInvoke(rotationField, out clickErr);
                rotationField.SetFocus();
                Thread.Sleep(200);
                System.Windows.Forms.SendKeys.SendWait("^a");
                Thread.Sleep(100);
                System.Windows.Forms.SendKeys.SendWait(degrees.ToString());
                Thread.Sleep(200);
                System.Windows.Forms.SendKeys.SendWait("{ENTER}");
                return AutomationResult.Success("Rotation set to " + degrees + "Â° via SendKeys fallback.");
            }
            catch (Exception ex2)
            {
                return AutomationResult.Failure("Both ValuePattern and SendKeys failed for rotation: " + ex2.Message);
            }
        }


        /// <summary>
        /// Simpler skybox switch that does NOT try to detect the file dialog.
        /// Strategy: open Visual Settings -> Sky tab -> click PART_SelectFileButton
        /// -> wait 2 seconds (the dialog has focus on the filename edit by
        /// default) -> SendKeys the absolute path + Enter.
        ///
        /// More robust than dialog detection because we never need to find or
        /// inspect the dialog window - we just type into whatever has focus.
        /// </summary>
        public static AutomationResult SwitchSkyboxViaSendKeys(AutomationElement enscapeWindow, string skyboxAbsolutePath)
        {
            if (enscapeWindow == null)
                return AutomationResult.Failure("Enscape window is null.");
            if (string.IsNullOrEmpty(skyboxAbsolutePath))
                return AutomationResult.Failure("No skybox path provided.");
            if (!System.IO.File.Exists(skyboxAbsolutePath))
                return AutomationResult.Failure("Skybox file does not exist: " + skyboxAbsolutePath);

            string expectedFileName = System.IO.Path.GetFileName(skyboxAbsolutePath);

            AutomationResult openResult = EnsureVisualSettingsOpen(enscapeWindow);
            if (!openResult.Ok) return openResult;

            AutomationElement visualSettings = WaitForElement(enscapeWindow,
                new PropertyCondition(AutomationElement.ClassNameProperty, "VisualSettingsWindow"),
                VisualSettingsOpenTimeoutMs);
            if (visualSettings == null)
                return AutomationResult.Failure("Visual Settings panel not found.");

            // Idempotency check
            AutomationElement existingPathLink = FindPathLink(visualSettings);
            if (existingPathLink != null)
            {
                try
                {
                    string current = existingPathLink.Current.Name;
                    if (string.Equals(current, expectedFileName, StringComparison.OrdinalIgnoreCase))
                        return AutomationResult.Success("Skybox already set to '" + current + "', no change needed.");
                }
                catch { }
            }

            // Activate the Sky tab
            AutomationElement tabControl = visualSettings.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsTabs"));
            if (tabControl == null)
                return AutomationResult.Failure("Could not find SettingsTabs.");

            AutomationElement skyTab = FindTabItemByText(tabControl, "Sky");
            if (skyTab == null)
                return AutomationResult.Failure("Could not find the 'Sky' tab.");

            string tabError;
            if (!TrySelectListItem(skyTab, out tabError))
                return AutomationResult.Failure("Failed to select Sky tab: " + tabError);

            Thread.Sleep(ClickSettleMs);

            // Click PART_SelectFileButton
            AutomationElement browseBtn = WaitForElement(visualSettings,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "PART_SelectFileButton"),
                3000);
            if (browseBtn == null)
                return AutomationResult.Failure("Could not find PART_SelectFileButton.");

            string clickError;
            if (!TryInvoke(browseBtn, out clickError))
                return AutomationResult.Failure("Failed to click browse button: " + clickError);

            // Wait for the dialog to be ready to receive input
            Thread.Sleep(2200);

            // Type the path and submit. We assume the dialog has focus on its
            // filename edit by default (this is the OS file dialog convention).
            try
            {
                System.Windows.Forms.SendKeys.SendWait("^a");          // select any default text
                Thread.Sleep(100);
                System.Windows.Forms.SendKeys.SendWait("{DEL}");
                Thread.Sleep(100);
                System.Windows.Forms.SendKeys.SendWait(EscapeForSendKeys(skyboxAbsolutePath));
                Thread.Sleep(200);
                System.Windows.Forms.SendKeys.SendWait("{ENTER}");
            }
            catch (Exception ex)
            {
                return AutomationResult.Failure("SendKeys failed: " + ex.Message);
            }

            // Wait a moment for Enscape to process the file
            Thread.Sleep(1500);

            // Verify
            AutomationElement pathLinkAfter = WaitForElement(visualSettings,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "PART_PathLink"),
                4000);
            if (pathLinkAfter == null)
                return AutomationResult.Success("Skybox path submitted via SendKeys (could not verify).");

            string newName = "";
            try { newName = pathLinkAfter.Current.Name; } catch { }

            if (string.Equals(newName, expectedFileName, StringComparison.OrdinalIgnoreCase))
            {
                // Wait for Enscape to actually finish loading the skybox into
                // the scene (decompressing texture, uploading to GPU, applying).
                // This MUST happen before the Video Export trigger, otherwise
                // the render starts with a black/wrong sky.
                Thread.Sleep(SkyboxApplySettleMs);
                return AutomationResult.Success(
                    "Skybox loaded: '" + newName + "' [verified, settled " +
                    (SkyboxApplySettleMs / 1000) + "s].");
            }

            return AutomationResult.Failure(
                "SendKeys completed, but PART_PathLink still shows '" + newName +
                "' instead of '" + expectedFileName + "'. The dialog may not have had focus, or Enscape rejected the path.");
        }

        /// <summary>
        /// Public wrapper so callers (the popup) can ask Enscape to open
        /// the Visual Settings panel without going through SwitchPreset.
        /// </summary>
        public static AutomationResult OpenVisualSettingsPanel(AutomationElement enscapeWindow)
        {
            if (enscapeWindow == null)
                return AutomationResult.Failure("Enscape window is null.");
            return EnsureVisualSettingsOpen(enscapeWindow);
        }

        /// <summary>
        /// Loads a View Path XML file into Enscape's Video Editor mode by:
        ///   1. Activating Video Editor mode (click GuiModeVideoEditor).
        ///   2. Clicking the Burger menu in the VideoEditPanel.
        ///   3. Searching the resulting menu/popup for a "Load View Path"
        ///      item (multi-strategy: by name keywords, across the Enscape
        ///      window AND the desktop root for popup windows).
        ///   4. Clicking the item -> file dialog opens.
        ///   5. SendKeys the absolute XML path + Enter.
        ///   6. Wait for Enscape to process the XML and update the timeline.
        /// </summary>
        public static AutomationResult LoadViewPathXml(
            AutomationElement enscapeWindow, string xmlAbsolutePath)
        {
            if (enscapeWindow == null)
                return AutomationResult.Failure("Enscape window is null.");
            if (string.IsNullOrEmpty(xmlAbsolutePath))
                return AutomationResult.Failure("No XML path provided.");
            if (!System.IO.File.Exists(xmlAbsolutePath))
                return AutomationResult.Failure("XML file does not exist: " + xmlAbsolutePath);

            // 1. Make sure Video Editor mode is active
            AutomationElement editorBtn = enscapeWindow.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "GuiModeVideoEditor"));
            if (editorBtn == null)
                return AutomationResult.Failure("Could not find GuiModeVideoEditor button.");

            string err;
            TryInvoke(editorBtn, out err); // idempotent; ignore any failure
            Thread.Sleep(1500);

            // 2. Find Burger button in VideoEditPanel
            AutomationElement burgerBtn = WaitForElement(enscapeWindow,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "Burger"),
                3000);
            if (burgerBtn == null)
                return AutomationResult.Failure(
                    "Could not find Burger button in Video Editor mode. " +
                    "(Are you sure Video Editor mode opened correctly?)");

            // 3. Click Burger to open the menu
            if (!TryInvoke(burgerBtn, out err))
                return AutomationResult.Failure("Failed to click Burger: " + err);

            Thread.Sleep(800);

            // 4. Search for a menu item with a "Load"-related name
            AutomationElement loadItem = FindLoadViewPathMenuItem(enscapeWindow);
            if (loadItem == null)
                loadItem = FindLoadViewPathMenuItem(AutomationElement.RootElement);

            if (loadItem == null)
            {
                // Burger may have triggered a direct action (no menu) - try sending Enter
                // in case the menu opened with first item focused. Then SendKeys path.
                System.Windows.Forms.SendKeys.SendWait("{ENTER}");
                Thread.Sleep(1500);
            }
            else
            {
                if (!TryInvoke(loadItem, out err))
                    return AutomationResult.Failure(
                        "Found Load menu item but failed to click it: " + err);
                Thread.Sleep(2000); // give the file dialog time to render
            }

            // 5. Type the XML path into whatever has focus and submit
            try
            {
                System.Windows.Forms.SendKeys.SendWait("^a");
                Thread.Sleep(100);
                System.Windows.Forms.SendKeys.SendWait("{DEL}");
                Thread.Sleep(100);
                System.Windows.Forms.SendKeys.SendWait(EscapeForSendKeys(xmlAbsolutePath));
                Thread.Sleep(200);
                System.Windows.Forms.SendKeys.SendWait("{ENTER}");
            }
            catch (Exception ex)
            {
                return AutomationResult.Failure("SendKeys for XML load failed: " + ex.Message);
            }

            // 6. Wait for Enscape to ingest the XML and refresh the timeline
            Thread.Sleep(3000);

            return AutomationResult.Success(
                "View Path XML load triggered: " +
                System.IO.Path.GetFileName(xmlAbsolutePath));
        }

        /// <summary>
        /// Finds a menu item whose visible name suggests "Load View Path".
        /// Looks across MenuItem, ListItem, and Button control types because
        /// Avalonia menus may surface items as any of those.
        /// Order of preference: "Load View Path" > "Load Path" > "Load" > "Open".
        /// </summary>
        private static AutomationElement FindLoadViewPathMenuItem(AutomationElement scope)
        {
            if (scope == null) return null;

            string[] preferred = new string[] {
                "Load View Path", "Load Path", "Load", "Open"
            };

            try
            {
                AutomationElementCollection candidates = scope.FindAll(TreeScope.Descendants,
                    new OrCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));

                // First pass: exact case-insensitive match against preferred list
                foreach (string kw in preferred)
                {
                    foreach (AutomationElement el in candidates)
                    {
                        string n;
                        try { n = el.Current.Name ?? ""; } catch { continue; }
                        if (string.IsNullOrEmpty(n)) continue;

                        if (string.Equals(n, kw, StringComparison.OrdinalIgnoreCase))
                            return el;
                    }
                }

                // Second pass: contains-match on the same keywords (handles "Load View Path...")
                foreach (string kw in preferred)
                {
                    foreach (AutomationElement el in candidates)
                    {
                        string n;
                        try { n = el.Current.Name ?? ""; } catch { continue; }
                        if (string.IsNullOrEmpty(n)) continue;

                        // Only match if the keyword is a "word" in the name, not part of unrelated text
                        if (n.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                            return el;
                    }
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// v0.7.4: full Video Export flow assuming the View Path XML is
        /// already loaded (either auto-loaded from the .rvt's saved state or
        /// pre-loaded manually by the operator).
        ///
        /// Steps:
        ///   1. Click GuiModeVideoEditor in the Enscape toolbar.
        ///   2. Wait for the VideoTimelinePanel to appear.
        ///   3. Click the Export button inside the VideoTimelinePanel.
        ///   4. Wait for the "Video Export" settings dialog (Resolution / FPS / Quality).
        ///   5. Click the Export button in that dialog.
        ///   6. Wait ~2.5s for the Save As dialog and SendKeys the output
        ///      absolute path + Enter.
        ///
        /// If the View Path XML is NOT loaded, step 3 will fail (Export button
        /// disabled or no-op). We'll add automatic XML loading via the Burger
        /// menu in v0.7.5 once we have the spike of that menu.
        /// </summary>
        public static AutomationResult TriggerVideoExport(
            AutomationElement enscapeWindow, string outputAbsolutePath)
        {
            if (enscapeWindow == null)
                return AutomationResult.Failure("Enscape window is null.");
            if (string.IsNullOrEmpty(outputAbsolutePath))
                return AutomationResult.Failure("No output path provided.");

            // 1. Activate Video Editor mode
            AutomationElement editorBtn = enscapeWindow.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "GuiModeVideoEditor"));
            if (editorBtn == null)
                return AutomationResult.Failure(
                    "Could not find GuiModeVideoEditor button in the Enscape toolbar.");

            string clickError;
            if (!TryInvoke(editorBtn, out clickError))
                return AutomationResult.Failure(
                    "Failed to click Video Editor mode: " + clickError);

            Thread.Sleep(2000);

            // 2. Find the VideoTimelinePanel and the Export button inside it
            AutomationElement timelinePanel = WaitForElement(enscapeWindow,
                new PropertyCondition(AutomationElement.ClassNameProperty, "VideoTimelinePanel"),
                4000);
            if (timelinePanel == null)
                return AutomationResult.Failure(
                    "Could not find VideoTimelinePanel after entering Video Editor mode.");

            AutomationElement exportInTimeline = timelinePanel.FindFirst(TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, "Export")));
            if (exportInTimeline == null)
                return AutomationResult.Failure(
                    "Could not find Export button inside VideoTimelinePanel. " +
                    "Make sure the View Path XML is loaded (timeline shows a duration > 00:00:00).");

            bool exportEnabled = true;
            try { exportEnabled = exportInTimeline.Current.IsEnabled; } catch { }
            if (!exportEnabled)
                return AutomationResult.Failure(
                    "Export button in timeline is DISABLED. This usually means no View Path is loaded. " +
                    "Click the Burger menu in Video Editor mode and load the .xml view path first. " +
                    "v0.7.5 will automate this once we have the menu spike.");

            // 3. Snapshot windows BEFORE clicking - the Video Export dialog will be a new window
            HashSet<int> handlesBefore = SnapshotAllWindowHandles(enscapeWindow);

            if (!TryInvoke(exportInTimeline, out clickError))
                return AutomationResult.Failure(
                    "Failed to click Export in timeline: " + clickError);

            // 4. Wait for the "Video Export" settings dialog
            string detectionDiag;
            AutomationElement videoExportDialog = WaitForFileDialog(
                handlesBefore, enscapeWindow, 8000, out detectionDiag);
            if (videoExportDialog == null)
                return AutomationResult.Failure(
                    "Video Export dialog did not appear within 8s.\n\nDiagnostic:\n" + detectionDiag);

            // 5. Click Export inside the Video Export dialog
            AutomationElement exportInDialog = FindExportButton(videoExportDialog);
            if (exportInDialog == null)
                return AutomationResult.Failure(
                    "Could not find 'Export' button inside the Video Export dialog.");

            string exportClickErr;
            if (!TryInvoke(exportInDialog, out exportClickErr))
                return AutomationResult.Failure("Failed to click Export in dialog: " + exportClickErr);

            // 6. Wait ~2.5s for Save As, then SendKeys the path + Enter
            Thread.Sleep(2500);

            try
            {
                System.Windows.Forms.SendKeys.SendWait("^a");
                Thread.Sleep(100);
                System.Windows.Forms.SendKeys.SendWait("{DEL}");
                Thread.Sleep(100);
                System.Windows.Forms.SendKeys.SendWait(EscapeForSendKeys(outputAbsolutePath));
                Thread.Sleep(200);
                System.Windows.Forms.SendKeys.SendWait("{ENTER}");
            }
            catch (Exception ex)
            {
                return AutomationResult.Failure("SendKeys to Save As failed: " + ex.Message);
            }

            // Best-effort wait for Enscape to start the render
            Thread.Sleep(1500);

            return AutomationResult.Success(
                "Video Export triggered. Output target: " + outputAbsolutePath +
                "\nEnscape is now rendering. You can close this dialog and let it run.");
        }

        /// <summary>
        /// Blocks until Enscape finishes writing the .mp4 output file.
        ///
        /// Polls the file size every few seconds. When the size stops growing
        /// for StableSeconds, the export is considered complete and the
        /// method returns true.
        ///
        /// Returns false if:
        ///   - the file never appears within the initial-wait window, or
        ///   - the total wait exceeds maxMinutes (default 2 hours).
        ///
        /// Safe to call after TriggerVideoExport() has already returned
        /// (which only triggers the export; the actual encoding takes
        /// minutes to tens of minutes depending on length + quality).
        /// </summary>
        public static bool WaitForVideoExportComplete(string outputPath, int maxMinutes = 120)
        {
            if (string.IsNullOrEmpty(outputPath))
            {
                Logger.Warn("WaitExport", "No output path provided - cannot wait.");
                return false;
            }

            const int StableSeconds        = 15;    // file size unchanged this long = done
            const int InitialWaitSeconds   = 300;   // file must appear within 5 min of trigger
            const int PollIntervalMs       = 5000;  // check every 5 s
            const int LogProgressEverySec  = 30;    // log file-size progress this often

            DateTime startTime        = DateTime.Now;
            DateTime deadline         = startTime.AddMinutes(maxMinutes);
            DateTime fileFirstSeen    = DateTime.MinValue;
            DateTime lastSizeChangeAt = DateTime.Now;
            DateTime lastLogAt        = DateTime.MinValue;
            long     lastSize         = -1;

            Logger.Info("WaitExport",
                "Waiting for Enscape to finish writing the .mp4 (max " + maxMinutes + " min): " + outputPath);

            while (DateTime.Now < deadline)
            {
                Thread.Sleep(PollIntervalMs);

                // File doesn't exist yet
                if (!System.IO.File.Exists(outputPath))
                {
                    if (fileFirstSeen == DateTime.MinValue)
                    {
                        double waited = (DateTime.Now - startTime).TotalSeconds;
                        if (waited > InitialWaitSeconds)
                        {
                            Logger.Error("WaitExport",
                                "Output file never appeared after " + InitialWaitSeconds +
                                "s. Enscape may have crashed or never started encoding.");
                            return false;
                        }
                        if ((DateTime.Now - lastLogAt).TotalSeconds >= LogProgressEverySec)
                        {
                            Logger.Info("WaitExport",
                                "Waiting for file to appear (" + (int)waited + "s elapsed)...");
                            lastLogAt = DateTime.Now;
                        }
                    }
                    continue;
                }

                // File just appeared
                if (fileFirstSeen == DateTime.MinValue)
                {
                    fileFirstSeen = DateTime.Now;
                    lastSizeChangeAt = DateTime.Now;
                    Logger.Info("WaitExport",
                        "Output file detected on disk. Will wait for size to stabilize for " +
                        StableSeconds + "s before declaring 'done'.");
                }

                // Read file size (handle locks)
                long size;
                try
                {
                    size = new System.IO.FileInfo(outputPath).Length;
                }
                catch
                {
                    // File locked / mid-write - skip this tick
                    continue;
                }

                if (size != lastSize)
                {
                    lastSize         = size;
                    lastSizeChangeAt = DateTime.Now;
                    if ((DateTime.Now - lastLogAt).TotalSeconds >= LogProgressEverySec)
                    {
                        double mb = size / (1024.0 * 1024.0);
                        Logger.Info("WaitExport",
                            "Encoding... " + mb.ToString("F1") + " MB written so far.");
                        lastLogAt = DateTime.Now;
                    }
                }
                else
                {
                    double stableFor = (DateTime.Now - lastSizeChangeAt).TotalSeconds;
                    if (stableFor >= StableSeconds)
                    {
                        double mb = size / (1024.0 * 1024.0);
                        double totalMin = (DateTime.Now - startTime).TotalMinutes;
                        Logger.Info("WaitExport",
                            "Export COMPLETE. Final size: " + mb.ToString("F1") + " MB. " +
                            "Total wait: " + totalMin.ToString("F1") + " min.");
                        return true;
                    }
                }
            }

            Logger.Error("WaitExport",
                "TIMEOUT after " + maxMinutes + " min. Closing session anyway - the .mp4 " +
                "may still be encoding in the background.");
            return false;
        }

        /// <summary>
        /// Finds the "Export" button inside the Video Export settings dialog.
        /// Tries multi-language and partial-match like FindOpenButton.
        /// </summary>
        private static AutomationElement FindExportButton(AutomationElement dialog)
        {
            string[] candidates = new string[] {
                "Export", "&Export",
                "Exportar",                  // Spanish
                "Exporter",                  // French
                "Esporta",                   // Italian
                "Exportieren"                // German
            };

            try
            {
                AutomationElementCollection buttons = dialog.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

                foreach (AutomationElement b in buttons)
                {
                    string n;
                    try { n = b.Current.Name ?? ""; } catch { continue; }
                    foreach (string cand in candidates)
                        if (string.Equals(n, cand, StringComparison.OrdinalIgnoreCase))
                            return b;
                }

                foreach (AutomationElement b in buttons)
                {
                    string n;
                    try { n = b.Current.Name ?? ""; } catch { continue; }
                    foreach (string cand in candidates)
                        if (n.StartsWith(cand, StringComparison.OrdinalIgnoreCase))
                            return b;
                }
            }
            catch { }

            return null;
        }

        // ============================================================
        // Internals
        // ============================================================

        /// <summary>
        /// Public wrapper of FindEnscapeRendererWindow - used by
        /// RenderCoordinator.CloseSession to locate Enscape's window
        /// for graceful WM_CLOSE shutdown.
        /// </summary>
        public static AutomationElement FindEnscapeRendererWindowPublic(int timeoutMs)
        {
            return FindEnscapeRendererWindow(timeoutMs);
        }

        private static AutomationElement FindEnscapeRendererWindow(int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                AutomationElement root = AutomationElement.RootElement;
                if (root != null)
                {
                    PropertyCondition cls = new PropertyCondition(
                        AutomationElement.ClassNameProperty, "RendererWindow");
                    AutomationElement found = root.FindFirst(TreeScope.Children, cls);
                    if (found != null) return found;
                }
                Thread.Sleep(PollIntervalMs);
                waited += PollIntervalMs;
            }
            return null;
        }

        /// <summary>
        /// After Enscape's RendererWindow appears, wait for the toolbar to be ready.
        /// We poll for the GuiVisualSettings toolbar button which only becomes
        /// reachable in the UI tree once Enscape's main UI is fully initialized
        /// (i.e. the splash screen has gone away).
        /// </summary>
        private static bool WaitForEnscapeReady(AutomationElement enscapeWindow, int timeoutMs)
        {
            if (enscapeWindow == null) return false;

            int waited = 0;
            int lastLogTimeMs = 0;
            const int LogIntervalMs = 5000;

            while (waited < timeoutMs)
            {
                try
                {
                    AutomationElement btn = enscapeWindow.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "GuiVisualSettings"));
                    if (btn != null)
                    {
                        Logger.Info("WaitForEnscapeReady",
                            "GuiVisualSettings toolbar button found after " + waited + "ms");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("WaitForEnscapeReady", "Exception while polling: " + ex.Message);
                }

                if (waited - lastLogTimeMs >= LogIntervalMs)
                {
                    Logger.Info("WaitForEnscapeReady",
                        "Still waiting for Enscape UI... (" + (waited / 1000) + "s elapsed, timeout " +
                        (timeoutMs / 1000) + "s)");
                    lastLogTimeMs = waited;
                }

                Thread.Sleep(PollIntervalMs);
                waited += PollIntervalMs;
            }
            Logger.Error("WaitForEnscapeReady",
                "TIMEOUT after " + (timeoutMs / 1000) + "s - GuiVisualSettings never appeared");
            return false;
        }

        /// <summary>
        /// Finds the running Revit window and clicks the "Start Enscape" ribbon
        /// button under the Enscape tab. Returns false if anything along the way
        /// could not be located.
        /// </summary>
        private static bool ClickStartEnscapeInRevit(out string error)
        {
            error = "";

            // -----------------------------------------------------------
            // Use Autodesk.Windows ribbon API (NOT UI Automation).
            // UI Automation cannot see the WPF ribbon tabs/buttons in
            // Revit reliably. The ribbon API gives us direct access to
            // the actual RibbonTab/RibbonItem objects with their
            // CommandHandlers, so we can execute commands without any
            // mouse simulation.
            // -----------------------------------------------------------
            try
            {
                Autodesk.Windows.RibbonControl ribbon = Autodesk.Windows.ComponentManager.Ribbon;
                if (ribbon == null)
                {
                    error = "Autodesk.Windows.ComponentManager.Ribbon is null - Revit ribbon not initialized.";
                    Logger.Error("RibbonApi", error);
                    return false;
                }

                Logger.Info("RibbonApi", "Total ribbon tabs: " + ribbon.Tabs.Count);

                // Log every tab for debugging
                foreach (Autodesk.Windows.RibbonTab tab in ribbon.Tabs)
                {
                    string tt = tab.Title ?? "";
                    string tid = tab.Id ?? "";
                    Logger.Info("RibbonApi", "  Tab: Title='" + tt + "' Id='" + tid + "' Visible=" + tab.IsVisible);
                }

                // Find the Enscape tab. Match by Title (most stable across versions)
                // or by Id as a fallback. Also strip the TM symbol just in case.
                Autodesk.Windows.RibbonTab enscapeTab = null;
                foreach (Autodesk.Windows.RibbonTab tab in ribbon.Tabs)
                {
                    string title = tab.Title ?? "";
                    string id    = tab.Id ?? "";
                    if (title.IndexOf("Enscape", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        id.IndexOf("Enscape", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        enscapeTab = tab;
                        Logger.Info("RibbonApi",
                            "Enscape tab FOUND. Title='" + title + "' Id='" + id + "' Panels=" + tab.Panels.Count);
                        break;
                    }
                }

                if (enscapeTab == null)
                {
                    error = "No Enscape tab in Revit ribbon. Is the Enscape addin loaded?";
                    Logger.Error("RibbonApi", error);
                    return false;
                }

                // Activate the tab so the user sees what we're doing
                try { enscapeTab.IsActive = true; } catch { /* not critical */ }

                // Walk all panels and items, log everything, find Start Enscape
                Autodesk.Windows.RibbonItem startItem = null;
                foreach (Autodesk.Windows.RibbonPanel panel in enscapeTab.Panels)
                {
                    string panelTitle = "(unknown)";
                    try { panelTitle = panel.Source.Title ?? "(no title)"; } catch { }
                    Logger.Info("RibbonApi", "  Panel: '" + panelTitle + "' Items=" + panel.Source.Items.Count);

                    foreach (Autodesk.Windows.RibbonItem item in panel.Source.Items)
                    {
                        string itemText = item.Text ?? "";
                        string itemId   = item.Id ?? "";
                        Logger.Info("RibbonApi",
                            "    Item: Text='" + itemText + "' Id='" + itemId + "' Type=" + item.GetType().Name);

                        // Match "Start Enscape", "Start", or any item whose text contains both words
                        bool matches =
                            itemText.IndexOf("Start", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            (itemText.IndexOf("Enscape", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             panelTitle.IndexOf("Enscape", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             itemText.Length <= 20);  // short "Start" label inside Enscape panel

                        if (startItem == null && matches)
                        {
                            startItem = item;
                            Logger.Info("RibbonApi",
                                "    -> CANDIDATE for Start Enscape: '" + itemText + "'");
                        }
                    }
                }

                if (startItem == null)
                {
                    error = "Found Enscape tab but no 'Start Enscape' button in any of its panels.";
                    Logger.Error("RibbonApi", error);
                    return false;
                }

                // Execute the command via the item's CommandHandler
                Autodesk.Windows.RibbonCommandItem cmdItem = startItem as Autodesk.Windows.RibbonCommandItem;
                if (cmdItem == null)
                {
                    error = "Start Enscape item is not a RibbonCommandItem (type=" +
                            startItem.GetType().Name + "). Cannot execute.";
                    Logger.Error("RibbonApi", error);
                    return false;
                }

                if (cmdItem.CommandHandler == null)
                {
                    error = "Start Enscape item has no CommandHandler.";
                    Logger.Error("RibbonApi", error);
                    return false;
                }

                Logger.Info("RibbonApi", "Executing CommandHandler on '" + (cmdItem.Text ?? "") + "'...");
                if (!cmdItem.CommandHandler.CanExecute(cmdItem))
                {
                    error = "CommandHandler.CanExecute returned false (button is disabled).";
                    Logger.Warn("RibbonApi", error);
                    // Try anyway - sometimes CanExecute is pessimistic
                }

                cmdItem.CommandHandler.Execute(cmdItem);
                Logger.Info("RibbonApi", "CommandHandler.Execute completed OK.");
                return true;
            }
            catch (Exception ex)
            {
                error = "Ribbon API exception: " + ex.GetType().Name + " - " + ex.Message;
                Logger.Error("RibbonApi", error);
                return false;
            }
        }

        /// <summary>
        /// Finds the "Enscape" tab in the Revit ribbon and activates it
        /// (Selects it via SelectionItemPattern, or mouse-clicks if not).
        /// Best-effort: returns silently if the tab isn't found.
        /// </summary>
        private static void ActivateEnscapeRibbonTab(AutomationElement revitWindow)
        {
            try
            {
                AutomationElementCollection tabs = revitWindow.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));

                Logger.Info("ActivateEnscapeTab", "Found " + tabs.Count + " TabItem elements in Revit window.");

                foreach (AutomationElement tab in tabs)
                {
                    string n;
                    try { n = tab.Current.Name; } catch { continue; }
                    if (string.IsNullOrEmpty(n)) continue;

                    // Match "Enscape", "Enscapeâ„¢", or starts with "Enscape"
                    if (n.IndexOf("Enscape", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Logger.Info("ActivateEnscapeTab", "Found Enscape tab. Name='" + n + "'. Selecting...");

                        // Try SelectionItemPattern first
                        object pat;
                        if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pat))
                        {
                            try
                            {
                                ((SelectionItemPattern)pat).Select();
                                Logger.Info("ActivateEnscapeTab", "Tab selected via SelectionItemPattern.");
                                return;
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn("ActivateEnscapeTab",
                                    "SelectionItemPattern.Select threw: " + ex.Message + " - falling back to mouse click");
                            }
                        }

                        // Fallback: physical mouse click on the tab
                        string err;
                        bool clicked = TryMouseClick(tab, out err);
                        Logger.Info("ActivateEnscapeTab",
                            "Mouse-click on tab returned " + clicked + " (error='" + err + "')");
                        return;
                    }
                }

                Logger.Warn("ActivateEnscapeTab",
                    "No tab whose Name contains 'Enscape' was found. Is the Enscape addin loaded?");
            }
            catch (Exception ex)
            {
                Logger.Warn("ActivateEnscapeTab", "Exception: " + ex.Message);
            }
        }

        /// <summary>
        /// Searches the Revit window for a button matching one of the given name candidates.
        /// Tries both case-insensitive equals and contains.
        /// </summary>
        private static AutomationElement FindRevitRibbonButton(AutomationElement revitWindow, string[] nameCandidates)
        {
            try
            {
                AutomationElementCollection allButtons = revitWindow.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

                // Pass 1: exact equality
                foreach (AutomationElement b in allButtons)
                {
                    string n;
                    try { n = b.Current.Name; } catch { continue; }
                    if (string.IsNullOrEmpty(n)) continue;
                    foreach (string cand in nameCandidates)
                        if (string.Equals(n, cand, StringComparison.OrdinalIgnoreCase))
                            return b;
                }

                // Pass 2: contains
                foreach (AutomationElement b in allButtons)
                {
                    string n;
                    try { n = b.Current.Name; } catch { continue; }
                    if (string.IsNullOrEmpty(n)) continue;
                    foreach (string cand in nameCandidates)
                        if (n.IndexOf(cand, StringComparison.OrdinalIgnoreCase) >= 0)
                            return b;
                }
            }
            catch { /* fall through to null */ }

            return null;
        }

        private static AutomationResult EnsureVisualSettingsOpen(AutomationElement enscapeWindow)
        {
            // The GuiVisualSettings toolbar button is in the WPF UI tree even
            // while Enscape is still loading the splash screen, but clicks are
            // silently dropped during that time. We retry: click, wait 2.5s,
            // check if VisualSettingsWindow appeared, repeat. Total wait can
            // be up to ~75s which covers a slow Enscape first-load.
            const int MaxAttempts          = 12;
            const int WaitAfterClickMs     = 2500;
            const int WaitBetweenRetriesMs = 4000;

            Logger.Info("EnsureVS", "Trying to open Visual Settings panel (up to " +
                MaxAttempts + " retries, max ~" +
                (MaxAttempts * (WaitAfterClickMs + WaitBetweenRetriesMs) / 1000) + "s)...");

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                // Already open?
                AutomationElement existing = enscapeWindow.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ClassNameProperty, "VisualSettingsWindow"));
                if (existing != null)
                {
                    Logger.Info("EnsureVS",
                        "Visual Settings panel is OPEN (attempt " + attempt + "/" + MaxAttempts + ").");
                    return AutomationResult.Success("Visual Settings open (attempt " + attempt + ").");
                }

                // Find the toolbar button (it should be there since we already passed WaitForEnscapeReady)
                AutomationElement btn = enscapeWindow.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "GuiVisualSettings"));
                if (btn == null)
                {
                    Logger.Warn("EnsureVS",
                        "GuiVisualSettings button not in tree on attempt " + attempt + " - waiting and retrying.");
                    if (attempt < MaxAttempts) Thread.Sleep(WaitBetweenRetriesMs);
                    continue;
                }

                Logger.Info("EnsureVS",
                    "Click attempt " + attempt + "/" + MaxAttempts + " on GuiVisualSettings...");
                string err;
                bool clicked = TryInvoke(btn, out err);
                if (!clicked)
                {
                    Logger.Warn("EnsureVS", "Click failed: " + err + " - retrying.");
                    if (attempt < MaxAttempts) Thread.Sleep(WaitBetweenRetriesMs);
                    continue;
                }

                Thread.Sleep(WaitAfterClickMs);

                AutomationElement opened = enscapeWindow.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ClassNameProperty, "VisualSettingsWindow"));
                if (opened != null)
                {
                    Logger.Info("EnsureVS",
                        "Visual Settings panel opened on click attempt " + attempt + ".");
                    return AutomationResult.Success("Opened Visual Settings panel (attempt " + attempt + ").");
                }

                Logger.Info("EnsureVS",
                    "Click " + attempt + " did not open the panel (Enscape probably still loading). " +
                    "Waiting " + WaitBetweenRetriesMs + "ms before next attempt...");
                if (attempt < MaxAttempts) Thread.Sleep(WaitBetweenRetriesMs);
            }

            Logger.Error("EnsureVS",
                "FAILED: Visual Settings panel did not open after " + MaxAttempts + " attempts.");
            return AutomationResult.Failure(
                "Visual Settings panel did not open after " + MaxAttempts +
                " click attempts (~" + (MaxAttempts * (WaitAfterClickMs + WaitBetweenRetriesMs) / 1000) +
                "s total). Enscape may be frozen or the panel opened on a non-default monitor.");
        }

        /// <summary>
        /// The active preset name is shown as a Heading1 TextBlock outside the
        /// PresetsList (Class='Heading1'). We find it by ClassName.
        /// </summary>
        private static string ReadActivePresetName(AutomationElement visualSettings)
        {
            try
            {
                AutomationElement heading = visualSettings.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ClassNameProperty, "Heading1"));
                if (heading == null) return null;
                return heading.Current.Name;
            }
            catch { return null; }
        }

        // ----- Pattern helpers -----

        /// <summary>
        /// Public wrapper for TryInvoke - used by RenderCoordinator to click
        /// elements without exposing the internal pattern fallback logic.
        /// </summary>
        public static bool ClickElement(AutomationElement element)
        {
            string err;
            return TryInvoke(element, out err);
        }

        private static bool TryInvoke(AutomationElement element, out string error)
        {
            error = "";
            try
            {
                object pat;
                if (element.TryGetCurrentPattern(InvokePattern.Pattern, out pat))
                {
                    try
                    {
                        ((InvokePattern)pat).Invoke();
                        return true;
                    }
                    catch
                    {
                        // InvokePattern exists but threw (e.g. element temporarily disabled
                        // while Revit loads). Fall through to Win32 mouse click.
                    }
                }

                // Fall back to SelectionItem.Select for list-item-like things
                if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pat))
                {
                    ((SelectionItemPattern)pat).Select();
                    return true;
                }

                // Fall back to TogglePattern
                if (element.TryGetCurrentPattern(TogglePattern.Pattern, out pat))
                {
                    ((TogglePattern)pat).Toggle();
                    return true;
                }

                // Last resort: physical mouse click at element's center.
                // Used for: Avalonia menu items without UIA patterns, and WPF/Revit
                // ribbon buttons whose InvokePattern throws (disabled state).
                return TryMouseClick(element, out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // ----- Win32 mouse click fallback -----

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP   = 0x0004;

        /// <summary>
        /// Performs a physical mouse left-click at the center of the element's
        /// screen bounding rectangle. Used when no UIA pattern is available
        /// (common for Avalonia menu items in popups).
        /// </summary>
        private static bool TryMouseClick(AutomationElement element, out string error)
        {
            error = "";
            try
            {
                System.Windows.Rect bounds;
                try { bounds = element.Current.BoundingRectangle; }
                catch (Exception ex) { error = "Could not read bounds: " + ex.Message; return false; }

                if (bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1)
                {
                    error = "Element has no usable bounds for click.";
                    return false;
                }

                int cx = (int)(bounds.X + bounds.Width  / 2);
                int cy = (int)(bounds.Y + bounds.Height / 2);

                if (!SetCursorPos(cx, cy))
                {
                    error = "SetCursorPos failed (Win32 error " +
                            Marshal.GetLastWin32Error() + ")";
                    return false;
                }

                Thread.Sleep(80);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
                Thread.Sleep(50);
                mouse_event(MOUSEEVENTF_LEFTUP,   0, 0, 0, IntPtr.Zero);

                return true;
            }
            catch (Exception ex)
            {
                error = "Mouse click failed: " + ex.Message;
                return false;
            }
        }

        private static bool TrySelectListItem(AutomationElement item, out string error)
        {
            error = "";
            try
            {
                object pat;
                if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pat))
                {
                    ((SelectionItemPattern)pat).Select();
                    return true;
                }
                if (item.TryGetCurrentPattern(InvokePattern.Pattern, out pat))
                {
                    ((InvokePattern)pat).Invoke();
                    return true;
                }
                error = "No SelectionItem or Invoke pattern on list item.";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TrySetText(AutomationElement edit, string text, out string error)
        {
            error = "";
            try
            {
                edit.SetFocus();
                Thread.Sleep(100);

                object pat;
                if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out pat))
                {
                    ((ValuePattern)pat).SetValue(text);
                    return true;
                }

                // Fall back to keyboard simulation
                System.Windows.Forms.SendKeys.SendWait("^a"); // select all
                Thread.Sleep(50);
                System.Windows.Forms.SendKeys.SendWait("{DEL}");
                Thread.Sleep(50);
                System.Windows.Forms.SendKeys.SendWait(EscapeForSendKeys(text));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void ClearText(AutomationElement edit)
        {
            try
            {
                edit.SetFocus();
                Thread.Sleep(50);
                object pat;
                if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out pat))
                    ((ValuePattern)pat).SetValue("");
                else
                {
                    System.Windows.Forms.SendKeys.SendWait("^a");
                    Thread.Sleep(50);
                    System.Windows.Forms.SendKeys.SendWait("{DEL}");
                }
            }
            catch { /* best-effort */ }
        }

        private static string EscapeForSendKeys(string s)
        {
            // SendKeys treats +, ^, %, ~, (, ), {, }, [, ] specially; brace them.
            var chars = "+^%~(){}[]";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (char c in s)
            {
                if (chars.IndexOf(c) >= 0) sb.Append('{').Append(c).Append('}');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static AutomationElement WaitForElement(AutomationElement scope, Condition cond, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                AutomationElement found;
                try { found = scope.FindFirst(TreeScope.Descendants, cond); }
                catch { found = null; }

                if (found != null) return found;
                Thread.Sleep(PollIntervalMs);
                waited += PollIntervalMs;
            }
            return null;
        }

        // ----- Skybox-specific helpers -----

        private static AutomationElement FindPathLink(AutomationElement visualSettings)
        {
            try
            {
                return visualSettings.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "PART_PathLink"));
            }
            catch { return null; }
        }

        /// <summary>
        /// Finds a TabItem whose visible text matches tabName. Avalonia exposes
        /// the tab text inside descendant Text/TextBlock elements rather than
        /// on the TabItem's own Name property.
        /// </summary>
        private static AutomationElement FindTabItemByText(AutomationElement tabControl, string tabName)
        {
            try
            {
                AutomationElementCollection tabItems = tabControl.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));

                foreach (AutomationElement tab in tabItems)
                {
                    AutomationElement text;
                    try
                    {
                        text = tab.FindFirst(TreeScope.Descendants,
                            new AndCondition(
                                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                                new PropertyCondition(AutomationElement.NameProperty, tabName)));
                    }
                    catch { continue; }

                    if (text != null) return tab;
                }
            }
            catch { /* fall through */ }

            return null;
        }

        /// <summary>
        /// Snapshots window handles across THREE scopes:
        ///   1. Top-level windows on the desktop
        ///   2. Descendants of the Enscape RendererWindow that are themselves Window-controltype
        ///      (Avalonia frequently exposes panels and dialogs as nested Window elements)
        /// The combined set is used as the "before" baseline for dialog detection.
        /// </summary>
        private static HashSet<int> SnapshotAllWindowHandles(AutomationElement enscapeWindow)
        {
            HashSet<int> set = new HashSet<int>();

            try
            {
                AutomationElement root = AutomationElement.RootElement;
                if (root != null)
                {
                    AutomationElementCollection topLevel = root.FindAll(TreeScope.Children,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
                    foreach (AutomationElement w in topLevel)
                    {
                        try { set.Add(w.Current.NativeWindowHandle); } catch { }
                    }
                }
            }
            catch { }

            if (enscapeWindow != null)
            {
                try
                {
                    AutomationElementCollection nested = enscapeWindow.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
                    foreach (AutomationElement w in nested)
                    {
                        try { set.Add(w.Current.NativeWindowHandle); } catch { }
                    }
                }
                catch { }
            }

            return set;
        }

        /// <summary>
        /// Waits for a file dialog to appear, searching BOTH top-level windows
        /// AND windows nested inside RendererWindow. Heuristics for "this is a
        /// file dialog":
        ///   1. Window not in the "before" snapshot, AND has at least one Edit + Button.
        ///   2. Window whose Name contains keywords like Load, Skybox, Open, File, Browse...
        ///
        /// On failure, fills `diagnostic` with a list of the windows that were
        /// observed during the last poll iteration so we can see why detection failed.
        /// </summary>
        private static AutomationElement WaitForFileDialog(
            HashSet<int> handlesBefore,
            AutomationElement enscapeWindow,
            int timeoutMs,
            out string diagnostic)
        {
            string[] keywords = new string[] {
                "Load", "Skybox", "Open", "File", "Choose", "Select", "Browse"
            };
            string[] skipClasses = new string[] {
                "VisualSettingsWindow", "RendererWindow", "AvaloniaDumbWindow"
            };

            List<string> lastSeen = new List<string>();
            int waited = 0;

            while (waited < timeoutMs)
            {
                lastSeen.Clear();

                try
                {
                    AutomationElement root = AutomationElement.RootElement;

                    // Pass 1: top-level windows
                    if (root != null)
                    {
                        AutomationElementCollection topLevel = root.FindAll(TreeScope.Children,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));

                        foreach (AutomationElement w in topLevel)
                        {
                            AutomationElement found = EvaluateWindowAsDialog(
                                w, handlesBefore, keywords, skipClasses, "top", lastSeen);
                            if (found != null) { diagnostic = ""; return found; }
                        }
                    }

                    // Pass 2: windows nested inside Enscape RendererWindow
                    if (enscapeWindow != null)
                    {
                        AutomationElementCollection nested = enscapeWindow.FindAll(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));

                        foreach (AutomationElement w in nested)
                        {
                            AutomationElement found = EvaluateWindowAsDialog(
                                w, handlesBefore, keywords, skipClasses, "nested", lastSeen);
                            if (found != null) { diagnostic = ""; return found; }
                        }
                    }
                }
                catch { }

                Thread.Sleep(PollIntervalMs);
                waited += PollIntervalMs;
            }

            // Build diagnostic
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            int max = Math.Min(15, lastSeen.Count);
            if (lastSeen.Count == 0)
            {
                sb.AppendLine("  (no windows observed - UI Automation may be blocked)");
            }
            for (int i = 0; i < max; i++)
            {
                sb.AppendLine("  - " + lastSeen[i]);
            }
            if (lastSeen.Count > max)
            {
                sb.AppendLine("  ... (+" + (lastSeen.Count - max) + " more)");
            }
            diagnostic = sb.ToString();
            return null;
        }

        /// <summary>
        /// Inspects a single window-control-type element and decides if it
        /// looks like a newly-opened file dialog. Also logs what it saw to
        /// the diagnostic list for failure reporting.
        /// </summary>
        private static AutomationElement EvaluateWindowAsDialog(
            AutomationElement w,
            HashSet<int> handlesBefore,
            string[] keywords,
            string[] skipClasses,
            string scope,
            List<string> seenLog)
        {
            try
            {
                int    handle    = w.Current.NativeWindowHandle;
                string name      = w.Current.Name      ?? "";
                string className = w.Current.ClassName ?? "";
                bool   isNew     = !handlesBefore.Contains(handle);

                if (seenLog.Count < 50)
                {
                    seenLog.Add(string.Format(
                        "[{0}] handle={1} new={2} name='{3}' class='{4}'",
                        scope, handle, isNew, name, className));
                }

                if (!isNew) return null;

                // Skip well-known non-dialog windows
                foreach (string skip in skipClasses)
                {
                    if (className.IndexOf(skip, StringComparison.OrdinalIgnoreCase) >= 0)
                        return null;
                }

                // Heuristic A: name contains a dialog keyword
                foreach (string kw in keywords)
                {
                    if (name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                        return w;
                }

                // Heuristic B: contains both an Edit and a Button (file dialog signature)
                AutomationElement anyEdit;
                AutomationElement anyButton;
                try
                {
                    anyEdit = w.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                    anyButton = w.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                }
                catch { return null; }

                if (anyEdit != null && anyButton != null) return w;
            }
            catch { /* swallow per-element errors */ }

            return null;
        }

        /// <summary>
        /// Types a full path into the file dialog's filename combo/edit and clicks Open.
        /// </summary>
        private static AutomationResult TypePathAndOpen(AutomationElement fileDialog, string fullPath)
        {
            // Strategy 1: find the filename Edit by ControlType + NameProperty contains "name"
            // Strategy 2: find the filename ComboBox (AutomationId 1148 in older common dialogs)
            // Strategy 3: send keys directly (the dialog's filename field has focus by default)

            AutomationElement edit = null;
            try
            {
                // The filename edit is typically inside a ComboBox; find any Edit descendant
                AutomationElementCollection edits = fileDialog.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

                // Prefer the Edit closest to a "File name" label
                foreach (AutomationElement e in edits)
                {
                    try
                    {
                        if (e.Current.IsKeyboardFocusable && e.Current.IsEnabled)
                        {
                            edit = e;
                            break;
                        }
                    }
                    catch { }
                }
            }
            catch { /* fall through to SendKeys */ }

            if (edit != null)
            {
                try
                {
                    edit.SetFocus();
                    Thread.Sleep(150);

                    object pat;
                    if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out pat))
                    {
                        ((ValuePattern)pat).SetValue(fullPath);
                    }
                    else
                    {
                        // Clear and type
                        System.Windows.Forms.SendKeys.SendWait("^a");
                        Thread.Sleep(50);
                        System.Windows.Forms.SendKeys.SendWait("{DEL}");
                        Thread.Sleep(50);
                        System.Windows.Forms.SendKeys.SendWait(EscapeForSendKeys(fullPath));
                    }
                }
                catch (Exception ex)
                {
                    return AutomationResult.Failure("Failed to set filename in dialog: " + ex.Message);
                }
            }
            else
            {
                // No edit found - just type, the dialog usually has focus on the filename field
                Thread.Sleep(200);
                System.Windows.Forms.SendKeys.SendWait(EscapeForSendKeys(fullPath));
            }

            Thread.Sleep(300);

            // Click the "Open" button - try multiple language variants and partial match
            AutomationElement openBtn = FindOpenButton(fileDialog);

            if (openBtn != null)
            {
                string err;
                if (!TryInvoke(openBtn, out err))
                {
                    // Last-ditch: send Enter
                    System.Windows.Forms.SendKeys.SendWait("{ENTER}");
                }
            }
            else
            {
                // No matching button found - send Enter to activate the default button
                System.Windows.Forms.SendKeys.SendWait("{ENTER}");
            }

            return AutomationResult.Success("File dialog submitted.");
        }

        /// <summary>
        /// Finds the "Open" button in a file dialog, handling multi-language
        /// installs and ampersand-prefixed names ("&Open"). Falls back to any
        /// button that's marked as the default.
        /// </summary>
        private static AutomationElement FindOpenButton(AutomationElement fileDialog)
        {
            string[] candidates = new string[]
            {
                "Open", "&Open", "Open File",
                "Abrir", "&Abrir",        // Spanish
                "Ouvrir", "&Ouvrir",      // French
                "Apri",                   // Italian
                "Offnen", "Ã–ffnen",       // German
                "Abrir Arquivo"           // Portuguese
            };

            try
            {
                AutomationElementCollection buttons = fileDialog.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

                // Pass 1: exact (case-insensitive) match
                foreach (AutomationElement b in buttons)
                {
                    string n;
                    try { n = b.Current.Name ?? ""; } catch { continue; }
                    foreach (string cand in candidates)
                        if (string.Equals(n, cand, StringComparison.OrdinalIgnoreCase))
                            return b;
                }

                // Pass 2: starts-with match (handles "Open" + suffix like "Open (default)")
                foreach (AutomationElement b in buttons)
                {
                    string n;
                    try { n = b.Current.Name ?? ""; } catch { continue; }
                    foreach (string cand in candidates)
                        if (n.StartsWith(cand, StringComparison.OrdinalIgnoreCase))
                            return b;
                }
            }
            catch { }

            return null;
        }
    }
}
