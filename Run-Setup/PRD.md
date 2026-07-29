# PRD — qbiq Render Dispatcher

**Version:** 1.0.4  
**Date:** 2026-06-01  
**Owner:** Eitan Abaud (eitan@qbiq.ai)  
**Status:** In production

---

## 1. Problem

qbiq designers need to export Enscape videos (3D walkthroughs) as part of the delivery workflow. The manual process requires the designer to keep their PC busy for the entire duration of the render — which can take anywhere from 30 minutes to several hours. This blocks the designer's machine and interrupts their work.

---

## 2. Solution

An automatic render queue system built on top of Dropbox. The designer creates a "ticket" from their PC (a `.json` file) with a single click in Revit. The ticket automatically syncs to a dedicated render server PC, which processes the render in the background without human intervention. The designer can keep working on their own machine.

---

## 3. Users

| Role | Description |
|------|-------------|
| **Designer** | Any team member with Revit. Only needs the "sender" plugin (Create Ticket button). |
| **Render Server** | Dedicated PC (currently Windows user `RENDER`) with Revit + Enscape running all day. Has the full plugin. |

---

## 4. Architecture

```
┌─────────────────────┐     ┌─────────────────────┐     ┌─────────────────────┐
│   DESIGNER PC       │     │      DROPBOX        │     │   RENDER SERVER     │
│                     │     │  (#_RenderServer/)  │     │   (user: RENDER)    │
│  [Create Ticket] ───┼────▶│  pending/*.json     │────▶│  auto-poll          │
│                     │◀────┼─ processed/*.json   │◀────┼─ processes ticket   │
│                     │     │  failed/*.json      │     │  exports .mp4       │
└─────────────────────┘     └─────────────────────┘     └─────────────────────┘
```

Dropbox is the transport layer between machines — no local network, VPN, or port configuration required.

---

## 5. Workflow

### 5.1 Designer — creating a ticket

1. Open the project `.rvt` in Revit.
2. Click the **Render Server** tab → **Create Ticket** button.
3. The dialog auto-populates with:
   - Project name (from the `.rvt` filename)
   - Enscape preset (default: "Qbiq preset")
   - Available skyboxes (scans project folder + Skybox Library)
   - Available view path XMLs (scans project folder)
   - Revit view (fixed: "3D Enscape")
4. Click **Save Ticket** → the `.json` appears in `pending/`.
5. The designer receives a Slack notification when the render starts, finishes, or fails.

### 5.2 Render Server — processing a ticket

1. Auto-poll detects a new ticket in `pending/` every N seconds.
2. Moves the ticket to `in-progress/`.
3. Opens the `.rvt` in Revit (with automatic path remapping if the ticket came from a different PC).
4. Launches Enscape, applies preset + skybox + view path XML.
5. Triggers the video export.
6. Waits for the `.mp4` to finish writing to disk.
7. Closes Enscape and the `.rvt`.
8. Moves the ticket to `processed/` (success) or `failed/` (error).
9. Sends a Slack notification.

---

## 6. Functional Requirements

### Sender Plugin (designer PCs)
- **FR-01** "Create Ticket" button in the Render Server tab in Revit.
- **FR-02** Auto-detect the path of the open `.rvt` (no manual typing, no path errors).
- **FR-03** Automatically scan available skyboxes (project folder + Skybox Library).
- **FR-04** Automatically scan available view path `.xml` files in the project folder.
- **FR-05** Auto-generate the output filename (`<Project> VIDEO_<id>.mp4`).
- **FR-06** Ticket includes `sender_username` and `sender_dropbox_root` so the server can remap paths.

### Server Plugin (render server PC)
- **FR-07** "GO RENDER SERVER" button for manual processing.
- **FR-08** Auto-poll: automatically process tickets from `pending/` every N seconds.
- **FR-09** Path remapping: rewrite ticket paths so they resolve correctly on the server.
- **FR-10** Enscape automation via UI Automation + Revit Idling events (without blocking the main thread).
- **FR-11** Wait for the `.mp4` to be fully written before closing Enscape.
- **FR-12** Ticket lifecycle: `pending/` → `in-progress/` → `processed/` / `failed/`.
- **FR-13** Detailed timestamped log in `logs/dispatch.log`.
- **FR-14** Slack notifications on start / completed / failed.

### Hardcoded settings (not exposed to the user)
- Resolution: Full HD (1920×1080)
- Compression: Maximum
- FPS: 60
- Revit view: "3D Enscape"

---

## 7. Non-Functional Requirements

| # | Requirement |
|---|-------------|
| **NFR-01** | Renders must not block Revit's main thread. |
| **NFR-02** | The system must tolerate Dropbox sync delays (tickets wait indefinitely in `pending/`). |
| **NFR-03** | The output `.mp4` is saved in the project folder, not in a central location. |
| **NFR-04** | The server must be able to run unattended (auto-poll enabled, Revit open all day). |
| **NFR-05** | The sender plugin requires no .NET SDK on the designer's PC (pre-compiled DLL). |
| **NFR-06** | The system must work on Revit 2024 + Enscape 4.17 or later. |

---

## 8. File Structure

```
Q Dropbox/
└── 3D Projects/
    └── #_RenderServer/
        ├── pending/          ← tickets waiting to be processed
        ├── in-progress/      ← ticket currently being processed
        ├── processed/        ← successfully completed tickets
        ├── failed/           ← tickets that failed
        ├── logs/
        │   └── dispatch.log  ← detailed server log
        └── Run-Setup/
            ├── Setup-RenderDispatcher-Server.ps1   ← server build + deploy script
            ├── Setup-CreateTicket.ps1              ← sender build + deploy script
            ├── Run-Setup-Server.bat                ← server launcher
            ├── Run-CreateTicket.bat                ← sender launcher
            └── deploy/
                ├── QbiqRenderDispatcher.dll        ← pre-compiled DLL (sender)
                └── Install-CreateTicket.bat        ← installer for designers
```

---

## 9. Ticket Schema (`.json`)

```json
{
  "rvt_path":           "C:\\Users\\...\\project.rvt",
  "project":            "Project name",
  "output_name":        "Project name VIDEO_<id>.mp4",
  "revit_view":         "3D Enscape",
  "enscape_preset":     "Qbiq preset",
  "skybox_file":        "Urban-Built_6-19.png",
  "view_path_xml":      "path_<id>.xml",
  "resolution":         "Full HD (1920x1080)",
  "fps":                60,
  "quality":            "Maximum",
  "notes":              "",
  "sender_username":    "Eitan 3D",
  "sender_dropbox_root": "C:\\Users\\Eitan 3D\\Q Dropbox"
}
```

---

## 10. Installation

### Render Server (once)
1. Verify Revit 2024 + Enscape 4.17 + .NET SDK are installed.
2. (Optional) Configure Slack webhook in `Setup-RenderDispatcher-Server.ps1`.
3. Close Revit → double-click `Run-Setup-Server.bat`.
4. Verify the **Render Server** tab appears in Revit with 3 buttons.

### Designer PC (for each new machine)
1. Confirm Dropbox is fully synced.
2. Close Revit → double-click `deploy/Install-CreateTicket.bat`.
3. Open Revit → verify the **Render Server** tab with the **Create Ticket** button.

> **Current limitation:** the pre-compiled DLL has paths hardcoded from the PC where it was compiled. If the designer's Windows username differs, the plugin must be compiled locally using `Run-CreateTicket.bat` (requires .NET SDK) instead of using `Install-CreateTicket.bat`.

---

## 11. Known Limitations & Tech Debt

| # | Description | Priority |
|---|-------------|----------|
| **L-01** | Pre-compiled DLL has hardcoded paths — not universal across PCs with different Windows usernames. Fix: use `Environment.SpecialFolder.UserProfile` at runtime. | Medium |
| **L-02** | Server path remapping only supports a single source/local pair. With multiple designer PCs, the regex already handles it in practice — but the `$PathRemapSource` variable in the script is misleading. | Low |
| **L-03** | The Slack "Render completed" notification fires when the export is triggered, not when the `.mp4` is actually ready. | Low |
| **L-04** | No UI to view queue status (how many tickets are pending, which one is processing). | Medium |
| **L-05** | Auto-poll is disabled by default — must be enabled manually in the code. | Low |

---

## 12. Version History

| Version | Date | Main Change |
|---------|------|-------------|
| v1.0.4 | 2026-05-20 | Correct closure of the active .rvt via `PostableCommand.Close` |
| v1.0.3 | 2026-05-20 | Robust Enscape shutdown by PID + WM_CLOSE |
| v1.0.2 | 2026-05-20 | Wait for .mp4 to finish writing before closing |
| v1.0.1 | 2026-05-20 | Runtime-generated ribbon icons + English docs |
| v1.0.0 | 2026-05-14 | Production: auto-poll, path remapping, Slack, logging |
