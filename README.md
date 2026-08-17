# qbiq Render Dispatcher

Automated Revit/Enscape video render queue for the qbiq team. Uses Dropbox as a transport layer — designers submit render tickets from their PCs, the render server picks them up and processes them automatically.

## How it works

```
Designer PC                    Dropbox                    Render Server
     │                            │                              │
     │  Create Ticket             │                              │
     │ ──────────────────────► pending/                         │
     │                            │                              │
     │                            │  ◄── auto-poll every 30s ───│
     │                            │                              │
     │                         in-progress/ ◄─── claimed ───────│
     │                            │                              │
     │                            │            open .rvt         │
     │                            │            switch skybox     │
     │                            │            export video      │
     │                            │                              │
     │                         processed/  ──── done ───────────►│
     │                            │                              │
     │                    (Slack notification — optional)         │
```

## Roles

| PC | Plugin | What it does |
|----|--------|-------------|
| Designer PCs | Sender | "Create Ticket" button — submits a render job |
| Render Server | Server | Auto-polls Dropbox, runs Revit + Enscape unattended |

## Requirements

- Revit 2024
- Enscape 4.x
- Dropbox synced on all PCs (same shared folder)
- .NET 4.8 (included with Windows)
- Render server: must run Revit as Administrator (required for decal path remapping via junctions)

## Installation

### Designer PC (sender)

1. Close Revit
2. Run `Run-Setup/deploy/Install-CreateTicket.bat`
3. Open Revit → "Render Server" tab → "Create Ticket" button appears

### Render Server

1. Clone or sync this folder via Dropbox
2. Open `Run-Setup/Run-Setup-Server.bat` **as Administrator**
3. Open Revit **as Administrator** → "Render Server" tab → click **GO RENDER SERVER**

See [`Run-Setup/SETUP-SERVER.md`](Run-Setup/SETUP-SERVER.md) for detailed server setup including junction creation.

> **Slack notifications:** the `SlackNotifier.cs` is fully implemented but the webhook URL is currently empty (`Config.cs` → `SlackWebhookUrl`). To enable: create an incoming webhook in your Slack workspace, paste the URL there, and recompile the server plugin.

## Ticket schema

```json
{
  "rvt_path":           "C:\\...\\project.rvt",
  "project":            "Project Name",
  "output_name":        "Project Name VIDEO_<timestamp>.mp4",
  "revit_view":         "3D Enscape",
  "enscape_preset":     "Qbiq preset",
  "skybox_file":        "Urban-Built_0-5.png",
  "skybox_rotation":    0,
  "view_path_xml":      "path_file.xml",
  "resolution":         "Full HD (1920x1080)",
  "fps":                60,
  "quality":            "Maximum",
  "sender_username":    "Eitan",
  "sender_dropbox_root":"C:\\Users\\Eitan\\Q Dropbox"
}
```

## Folder structure

```
#_RenderServer/
├── pending/          # Submitted tickets waiting to be picked up
├── in-progress/      # Ticket currently being rendered
├── processed/        # Completed renders
├── failed/           # Failed renders (with error info in logs)
├── logs/             # dispatch.log — full render history
├── Run-Setup/
│   ├── Setup-CreateTicket.ps1          # Sender plugin build script
│   ├── Setup-RenderDispatcher-Server.ps1  # Server plugin build script
│   ├── deploy/
│   │   ├── QbiqRenderDispatcher.dll    # Pre-compiled sender DLL
│   │   └── Install-CreateTicket.bat   # One-click installer for designers
│   ├── PRD.md
│   ├── CHANGELOG.md
│   ├── SETUP-SERVER.md
│   └── SETUP-CLIENT.md
└── _setup/           # Diagnostic scripts
```

## Recompiling

If you need to modify the plugin, you need .NET SDK 6+ and Revit 2024 installed:

```powershell
# Sender plugin (designer PCs)
powershell -ExecutionPolicy Bypass -File "Run-Setup/Setup-CreateTicket.ps1"

# Server plugin (render server)
powershell -ExecutionPolicy Bypass -File "Run-Setup/Setup-RenderDispatcher-Server.ps1"
```

After compiling the sender, copy `bin/Release/QbiqRenderDispatcher.dll` to `deploy/` so other designers can install without recompiling.

## Changelog

See [`Run-Setup/CHANGELOG.md`](Run-Setup/CHANGELOG.md).
