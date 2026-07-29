# Install the "Create Ticket" plugin — designer guide

> This guide is for anyone on the qbiq team who needs to **send render tickets** from their PC to the render server.
> You don't need to code anything. You don't need Visual Studio. Just Revit + Dropbox.

---

## Requirements

- [ ] Have **Revit 2024** installed
- [ ] Have **Dropbox** synced under your qbiq account (wait for the initial sync to fully complete)
- [ ] Know where the `Q Dropbox\3D Projects\#_RenderServer` folder is on your PC

You don't need:
- ❌ Enscape (only the render server needs it)
- ❌ Visual Studio or .NET SDK (the plugin comes pre-compiled)

---

## Steps (5 minutes)

### 1. Close Revit
**Completely.** Close all Revit windows. If Revit is open during install, it will fail.

### 2. Go to the installer folder in Dropbox

In Windows Explorer:
```
Q Dropbox\3D Projects\#_RenderServer\Run-Setup\deploy\
```

You'll see two files:
- `Install-CreateTicket.bat`
- `QbiqRenderDispatcher.dll`

### 3. Double-click `Install-CreateTicket.bat`

A black cmd window will appear and:
1. Verify Revit isn't running
2. Copy the plugin DLL to `%APPDATA%\qbiq\RenderDispatcher\`
3. Install the `.addin` manifest in `%APPDATA%\Autodesk\Revit\Addins\2024\`

When it says "Instalacion completada", press a key to close the window.

### 4. Open Revit

When it loads, look at the tabs at the top. You'll see a new one: **Render Server**

With a single button: **Create Ticket**

Done! You can now send tickets.

---

## How to send a ticket

1. Open the `.rvt` you want to render in Revit.
2. Click the **Render Server** tab → **Create Ticket** button.
3. The dialog appears. Fill in:
   - **Project name**: auto-detected, editable
   - **Enscape preset**: defaults to "Qbiq preset"
   - **Skybox**: dropdown with available files
     - `PROJECT:` = lives in the project folder
     - `LIBRARY:` = lives in the Skybox Library on Dropbox
     - Leave `(none)` to keep the current .rvt skybox
   - **View Path XML**: the `.xml` with the animated Enscape camera (must be in the project folder)
   - **Revit view**: locked to "3D Enscape"
   - **Notes**: optional
4. Click **Save Ticket**.
5. Confirmation appears. The ticket lands in `#_RenderServer\pending\` waiting for the server PC.

---

## What does the project need to have?

For a render to work, the project on Dropbox needs:

1. **The `.rvt` file** you're working on.
2. **An Enscape view path `.xml`** in the same project folder. To generate it:
   - In Enscape: open the Video Editor
   - Set up your camera path
   - Click the menu (3 dots) → "Save path to file..."
   - Save it in the project folder next to the .rvt
3. **(Optional) A custom skybox** (.png / .jpg / .hdr) in the project folder. Otherwise one from the Skybox Library is used.

---

## When does my ticket get processed?

- **If the server PC is on and someone clicks GO RENDER SERVER:** it's processed in arrival order (oldest first).
- **If the server PC has auto-poll enabled:** it processes on its own within minutes.
- **If the server PC is off:** your ticket stays in `pending/` waiting. It will be picked up the next time the server is running.

You'll receive a Slack notification (channel `#renders`) when it starts, finishes, or fails.

The video `.mp4` is saved **in the project folder** where the .rvt lives (not in a central folder).

---

## Troubleshooting

### "Render Server" tab doesn't appear in Revit
- Did you close Revit BEFORE double-clicking `Install-CreateTicket.bat`?
- If not, repeat: close Revit → run installer → open Revit
- If it still doesn't show up, check `%APPDATA%\Autodesk\Revit\Addins\2024\` for `QbiqRenderDispatcher.addin`

### Create Ticket dialog can't find my View Path XML
- Verify the `.xml` is in the **same folder** as the `.rvt`
- Filename can be anything, as long as the extension is `.xml`
- Try refreshing: close and reopen the dialog

### My ticket appears in `failed/` after a while
- Click "View Log" (if you have it) or ask the server operator for the log
- 90% of the time it's: the .rvt didn't sync to Dropbox in time, or the view path .xml was in a different folder

### Ticket sits in `pending/` and never processes
- The server PC is off, or no one has clicked GO RENDER SERVER yet.
- If urgent, ping someone in Slack.

---

## Uninstalling

If you need to remove the plugin:

1. Close Revit.
2. Delete these two files/folders:
   - `%APPDATA%\Autodesk\Revit\Addins\2024\QbiqRenderDispatcher.addin`
   - `%APPDATA%\qbiq\RenderDispatcher\` (entire folder)
3. Open Revit — the Render Server tab is gone.

The Dropbox folders are not touched.
