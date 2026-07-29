# Changelog

Every significant plugin change is logged here. Format:
**`vX.Y.Z` (YYYY-MM-DD) — Short description**, then details.

---

## v1.0.9-server (2026-06-03) — Auto-dismiss "Save changes?" dialog

### Server only
- New `DismissSaveDialog()` method runs on a background thread immediately before `PostableCommand.Close` is posted.
- Polls the desktop every 300ms for up to 10 seconds looking for a dialog with a "Don't Save" / "No" button (with localization fallbacks for Spanish, French, German).
- When found, clicks it automatically via `InvokePattern` so Revit closes without human intervention.
- Without this fix, if Enscape modified the document (which it always does on load), Revit would show the save dialog and block indefinitely in unattended mode.

---

## v1.0.8-server (2026-06-03) — Fix race condition in auto-poll

### Server only
- `TryAutoProcess`: ticket is now moved to `in-progress/` immediately after `PickNextJob()`, before opening the .rvt or doing anything else. Previously the ticket stayed in `pending/` until the render finished, so a second auto-poll tick could pick the same ticket and start a duplicate render.
- The filename is preserved (no timestamp added) when moving to `in-progress/`, so `processed/` and `failed/` filenames remain clean.
- If the move to `in-progress/` fails (e.g. file already moved by another process), the ticket is skipped with an error log instead of proceeding.

---

## v1.0.7-server (2026-06-03) — Fix skybox rotation field lookup

### Server only
- `SetSkyboxRotation`: replaced AutomationId search (which failed — Enscape uses the generic `PART_TextBox` for all numeric inputs) with a class-based search for `NumericTextBox` elements on the Sky tab. Takes `[0]` which is the rotation angle field (confirmed via live diagnosis on Enscape 4.x).
- Rotation step is now **non-fatal**: if it fails, the render continues without rotation and logs a warning. Previously a rotation failure aborted the entire render.

---

## v1.0.6-server / v1.0.2-sender (2026-06-03) — Skybox rotation

### Both (sender + server)
- New field `skybox_rotation` (int, degrees 0-360, default 0) added to `RenderManifest`.
- `CreateTicketDialog`: new "Skybox rotation (°)" numeric input below the skybox dropdown. Defaults to 0 (no rotation). Value is written to the ticket JSON.

### Server only
- New `EnscapeAutomation.SetSkyboxRotation()`: opens Visual Settings, navigates to the Sky tab, and sets the rotation field.
  - Tries AutomationIds: `PART_RotationAngle`, `PART_SkyRotation`, `PART_Rotation`, `RotationAngle`.
  - Falls back to SendKeys if ValuePattern is unavailable.
  - If the field is not found, logs all editable descendants on the Sky tab to `dispatch.log` so the correct AutomationId can be identified and added.
- `RenderCoordinator`: new `RunSkyboxRotation` step runs after `RunSkyboxSwitch`. Skipped automatically if rotation == 0.
- Rotation status included in the final summary log line.

---

## v1.0.5-server (2026-06-01) — Universal script (runtime paths)

### Server only
- `Config.cs`: `DropboxRoot`, `SkyboxLibraryRoot`, and `RemapLocalPrefix` are now computed at runtime using `Environment.SpecialFolder.UserProfile` instead of being hardcoded at compile time.
- Previously the script had `C:\Users\RENDER\...` hardcoded — setting up a new render server required manually editing the script if the Windows username was different. Now `Run-Setup-Server.bat` works on any PC without edits.
- Removed the now-redundant `RemapSourcePrefix` constant (it was never used in the actual remap logic, which already used a regex matching any username).
- `$DropboxQueueRoot`, `$SkyboxLibraryRoot`, and `$PathRemapLocal` PowerShell variables at the top of the script are still present for build-time validation messages, but no longer baked into the DLL.

---

## v1.0.1-sender (2026-06-01) — Universal DLL (runtime paths)

### Sender only
- `Config.cs`: `DropboxRoot` and `SkyboxLibraryRoot` are now computed at runtime using `Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)` instead of being hardcoded at compile time.
- Previously the pre-compiled DLL in `deploy/` only worked on PCs with the same Windows username as the machine that compiled it. Now the same DLL works on any designer's PC regardless of their Windows username.
- `deploy/QbiqRenderDispatcher.dll` updated — designers can now install with `Install-CreateTicket.bat` without needing .NET SDK on their machine.

---

## v1.0.4 (2026-05-20) — Close active .rvt to Home dashboard

### Server only
- `CloseSession()` now actually closes the active Revit document (it was staying open in v1.0.3 even though the log said otherwise).
- Root cause: `Document.Close(false)` returns `false` silently when the doc is the only one open + active. Revit requires at least one document loaded at all times via the API.
- Fix: use the built-in Revit menu command via `UIApplication.PostCommand(PostableCommand.Close)`. That command CAN close the last document and returns Revit to the Home dashboard.
- New flow:
  1. Close every non-active, non-linked document via `Document.Close(false)`
  2. For the active document, post `PostableCommand.Close` (executes async on the Revit main thread once idle)
- Confirmed working with Enscape process name `Enscape.RendererHost`.

---

## v1.0.3 (2026-05-20) — Robust Enscape shutdown

### Server only
- `CloseSession()` no longer relies on `Process.GetProcessesByName("Enscape" | "Enscape.App")`. The Enscape executable name varies across installs (silently observed in production: process name didn't match either string, so the kill was silently skipped and Enscape stayed open).
- New strategy:
  1. Find the Enscape `RendererWindow` via UI Automation (already do this elsewhere)
  2. Read its PID + HWND
  3. Send `WM_CLOSE` (graceful shutdown — same as clicking the X)
  4. If still alive after 3s, `Process.Kill()` **by PID** (PID never fails on naming)
  5. Fallback: if no window found, scan ALL processes containing "Enscape" in the name and kill them
- Logs the discovered process name on successful close so we know it for future reference.
- New public method `EnscapeAutomation.FindEnscapeRendererWindowPublic(timeoutMs)` so `RenderCoordinator` can reuse the existing window-finding logic.
- New P/Invoke for `user32.dll!PostMessage` to send `WM_CLOSE`.

---

## v1.0.2 (2026-05-20) — Wait for MP4 to finish before closing

### Server only
- New method `EnscapeAutomation.WaitForVideoExportComplete(outputPath, maxMinutes=120)`.
  - Polls the output .mp4 every 5 seconds
  - Returns true when file size stays unchanged for 15 seconds (encoding done)
  - Returns false on timeout (default 2 hours) or if the file never appears within 5 min
- `RenderCoordinator.Finalize()` now calls this method **before** `CloseSession()` on successful renders.
  - Previously: TriggerVideoExport returned immediately → Coordinator finalized → CloseSession tried to kill Enscape mid-encode → MP4 corrupted or Kill silently failed (Enscape stayed open).
  - Now: Coordinator waits for the .mp4 to be fully written → kills Enscape → closes the .rvt → server is clean for the next ticket.
- Failed renders skip the wait (no point waiting for a file that's not coming).

### Known limitations (for later)
- Slack "Render completed" notification still fires immediately on trigger (not when the .mp4 is actually done). Will revisit when Slack messaging timing is reworked.

---

## v1.0.1 (2026-05-20) — Ribbon icons + English docs

### Plugin (both server + sender)
- New file `RibbonIcons.cs` that generates 32x32 / 16x16 button icons at runtime using `System.Drawing`. No external PNGs needed — icons live inside the DLL.
- Three icons:
  - **GO RENDER SERVER**: green rounded square with white play triangle
  - **View Log**: dark gray rounded square with white document + text lines
  - **Create Ticket**: qbiq-blue rounded square with white paper airplane
- Server App.cs assigns icons to all three buttons; Sender App.cs assigns the Create Ticket icon.

### Docs
- All 4 documentation files (`PROJECT-CONTEXT.md`, `SETUP-SERVER.md`, `SETUP-CLIENT.md`, `CHANGELOG.md`) translated to English so the whole team can read them.

---

## v1.0.0 (2026-05-14) — Production

### Server (`Setup-RenderDispatcher-Server.ps1`)
- Auto-poll mode reintroduced via `AutoRenderHandler.cs`. The render server processes tickets automatically without human intervention.
- Path remapping so tickets created on PC "Eitan 3D" resolve correctly on PC server "Eitan Abaud" (`$PathRemapSource` / `$PathRemapLocal`).
- Coordinator based on `UIApplication.Idling` so Revit's main thread isn't blocked while Enscape loads geometry.
- Detailed logging in `logs/dispatch.log`.
- Fire-and-forget Slack notifications (start / completed / failed).

### Sender (`Setup-CreateTicket.ps1`)
- Client-only plugin version: only the **Create Ticket** button, no GO RENDER SERVER, no View Log.
- `$DropboxQueueRoot` and `$SkyboxLibraryRoot` use `$env:USERPROFILE` so no manual per-user editing is needed.

### Deploy
- Pre-compiled DLL in `deploy/QbiqRenderDispatcher.dll` for designers without .NET SDK.
- `deploy/Install-CreateTicket.bat` — trivial installer with no dependencies.

### Documentation
- `PROJECT-CONTEXT.md` — master handoff doc for future Claude / new team members
- `SETUP-SERVER.md` — detailed steps to configure the render server PC
- `SETUP-CLIENT.md` — steps to install the sender plugin on a designer PC
- `CHANGELOG.md` — this file

---

## v0.9.5 (2026-05-15) — Pre-production

- Idling-event coordinator (`RenderCoordinator.cs`) replaces the inline render loop. Fixed the "Visual Settings panel did not open" issue when Enscape was still loading.
- `PostVsOpenDelaySeconds = 45` configurable — buffer between Visual Settings opening and starting the preset switch.
- Removed zombie code from TicketDialog (references to ComboBoxes that didn't exist).

---

## v0.9.4 (2026-05-12) — Visual Settings retry

- Retry loop in `EnsureVisualSettingsOpen` (up to 12 attempts × ~7s).

---

## v0.9.3 (2026-04-30) — Wait for Enscape ready

- After detecting the `RendererWindow`, wait until the `GuiVisualSettings` toolbar is accessible.

---

## v0.9.2 (2026-04-29) — Ribbon API for auto-start

- Replaced UI Automation with `Autodesk.Windows.ComponentManager.Ribbon` (`AdWindows.dll`) for starting Enscape. UI Automation couldn't see Revit's WPF ribbon tabs.

---

## v0.9.0 (2026-04-29) — Slack + ticket lifecycle

- Slack notifications (start / completed / failed).
- Ticket lifecycle: `pending/` → `processed/` or `failed/` with timestamps.
- Detailed per-step logging.
- Auto Mode polling REMOVED (later reintroduced in v1.0.0).

---

## v0.8.x — Create Ticket button

- New **Create Ticket** button in the ribbon. Auto-detects `Document.PathName` (zero typos possible).
- Dropdowns with filesystem scanning (project folder + Skybox Library).

---

## v0.7.x — End-to-end render automation

- Switch preset / skybox / view path XML / video export. First complete version that renders a ticket end-to-end.

---

## v0.6.x — UI Automation foundations

- Spike scripts (`Spike-EnscapeUI.ps1`) to map out Enscape's UI.
- `EnscapeAutomation.cs` with basic methods: GetOrStartEnscape, SwitchPreset, SwitchSkyboxViaSendKeys.

---

## v0.5 — Queue scaffolding

- `JobQueue.cs` with `pending/`, `processed/`, `failed/`, `in-progress/`, `logs/`.
- Manifest schema (`RenderManifest.cs`) with fields `rvt_path`, `project`, `output_name`, `enscape_preset`, `skybox_file`, `view_path_xml`, etc.

---

## v0.1 — Hello World

- Revit plugin that registers a button on a custom "Render Server" tab.
