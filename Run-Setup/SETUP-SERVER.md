# Render Server PC Setup

> This guide is for configuring a **dedicated PC** that will process renders automatically.
> If you want to install the plugin on a designer's PC (Create Ticket only), see **SETUP-CLIENT.md**.

---

## Prerequisites

Confirm on the target PC that you have installed:

- [ ] **Windows 10 or 11**
- [ ] **Autodesk Revit 2024** at `C:\Program Files\Autodesk\Revit 2024`
- [ ] **Enscape 4.17 or later** — the "Enscape™" tab must appear in the Revit ribbon and "Start Enscape" must work manually
- [ ] **.NET SDK** — open `cmd.exe` and run `dotnet --version`. If it responds with a number (e.g. `8.x.x` or `10.x.x`), you're good. If it says "command not found", install Visual Studio 2022 Community with the **".NET desktop development"** workload (https://visualstudio.microsoft.com/vs/community/).
- [ ] **Dropbox** installed and fully synced under the qbiq account

---

## Step 1 — Verify the Windows username

The script assumes Windows user `Eitan Abaud`. Verify:
```cmd
echo %USERNAME%
```

**If it's different from `Eitan Abaud`:**
1. Open `Setup-RenderDispatcher-Server.ps1` in Notepad
2. Find the lines with `Eitan Abaud` (lines ~32 and ~35) and change them to your username
3. Heads-up: the line `$PathRemapLocal = "C:\Users\Eitan Abaud\Q Dropbox"` too — change it to the current user

---

## Step 2 — Configure Slack (optional)

If you want Slack notifications when a render starts / finishes / fails:

1. Go to https://api.slack.com/apps → Create New App → From scratch → "qbiq Render Server"
2. Activate **Incoming Webhooks** → Add New Webhook to Workspace → pick a channel (`#renders` for example)
3. Copy the URL (format: `https://hooks.slack.com/services/T.../B.../xxxxx`)
4. Open `Setup-RenderDispatcher-Server.ps1` in Notepad and paste the URL into `$SlackWebhookUrl = "..."`

If you don't want Slack, leave the field empty (`""`).

---

## Step 3 — Run setup

1. **Close Revit completely** (the script refuses to run while Revit is open, because Revit locks the plugin DLL).
2. Double-click **`Run-Setup-Server.bat`** (inside the `#_RenderServer\Run-Setup\` Dropbox folder).
3. The script will:
   - Validate Revit + Enscape + .NET + Dropbox folder
   - Generate the C# source under `QbiqRenderDispatcher/`
   - Compile `QbiqRenderDispatcher.dll`
   - Deploy the `.addin` manifest to `%APPDATA%\Autodesk\Revit\Addins\2024\`
4. **Total runtime:** about 30-60 seconds.
5. When you see `Setup completed successfully`, you're done.

### If the build fails

- Copy the full terminal output
- Send it to Claude for diagnosis
- 99% of the time it's: Revit was open, .NET SDK missing, or `AdWindows.dll` missing in the Revit folder

---

## Step 4 — Verify in Revit

1. Open Revit.
2. You should see a new tab: **Render Server**.
3. Three buttons:
   - **GO RENDER SERVER** — process the next pending ticket (manual)
   - **View Log** — open `logs/dispatch.log` in Notepad
   - **Create Ticket** — create a ticket for the open .rvt

---

## Step 5 — End-to-end test

1. Open any `.rvt` in Revit.
2. Click **Create Ticket** → fill in the dialog → Save Ticket.
3. Verify a file appeared in `Q Dropbox\3D Projects\#_RenderServer\pending\`.
4. If Enscape isn't running, open it manually and wait for it to load (first time).
5. Click **GO RENDER SERVER**.
6. You should see:
   - "Confirm Render" dialog appears with preset/skybox/viewpath
   - Click Render Now
   - "Render queued" message appears and the dialog closes
   - The render starts in the background (no more dialogs)
   - Slack notifies when it finishes (if configured)
   - The .json ticket moves to `processed/` or `failed/`

---

## Step 6 — Auto-poll mode (optional)

The server plugin has **auto-poll**: if enabled, it checks `pending/` every N seconds and processes the next ticket without anyone clicking anything. To enable:

1. (How to enable depends on the current script version — check the README inside the .ps1 or `AutoRenderHandler.cs` for details. Off by default to avoid accidents.)
2. Ideal for leaving the PC running unattended overnight / on weekends.

---

## Common troubleshooting

### "Render Server" tab doesn't appear
- Did you close Revit before running setup? If not, the .addin got written but the .dll couldn't be replaced.
- Verify these 2 files exist:
  - `%APPDATA%\Autodesk\Revit\Addins\2024\QbiqRenderDispatcher.addin`
  - The `.dll` referenced by the manifest (open it with Notepad to see the path)

### Render fails with "Visual Settings panel did not open"
- Enscape is probably still loading geometry. Increase `PostVsOpenDelaySeconds` in `RenderCoordinator.cs` (default 45 seconds) — it needs more time on this model.

### Tickets don't appear even though the other PC created them
- Wait for Dropbox to finish syncing (check the icon in the system tray).
- If the ticket paths point to a different user (e.g. `Eitan 3D`), confirm that `$PathRemapSource` / `$PathRemapLocal` is configured in the script.

---

## Redeploy after an update

When Claude/you modify `Setup-RenderDispatcher-Server.ps1`:

1. Wait for Dropbox to sync the new version.
2. Close Revit.
3. Double-click `Run-Setup-Server.bat`.
4. Reopen Revit.

The script is idempotent: it overwrites everything needed and cleans up obsolete files.
