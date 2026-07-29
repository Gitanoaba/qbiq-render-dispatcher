#requires -Version 5.0
<#
.SYNOPSIS
    qbiq Render Dispatcher - Setup, build and deploy script
.DESCRIPTION
    Creates the project structure, writes the C# source files, compiles the
    Revit add-in DLL, and deploys the .addin manifest to the correct Windows
    locations so Revit picks it up on next launch.

    Re-run this script every time Claude sends an updated version - it is
    fully idempotent and simply overwrites the source files and rebuilds.

.NOTES
    Version: 1.0.2-sender (Skybox rotation field in Create Ticket dialog)
    Requires: Visual Studio 2022 Community with ".NET desktop development"
              workload installed (provides the .NET SDK + msbuild).
#>

# =====================================================================
# CONFIGURATION
# =====================================================================
$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

$ProjectRoot      = Join-Path $PSScriptRoot "QbiqRenderDispatcher"
$RevitInstallPath = "C:\Program Files\Autodesk\Revit 2024"
$RevitVersion     = "2024"
$ProjectName      = "QbiqRenderDispatcher"
$AddinDir         = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"

# Dropbox queue path - hardcoded into Config.cs at build time.
$DropboxQueueRoot = "$env:USERPROFILE\Q Dropbox\3D Projects\#_RenderServer"

# Skybox library path - hardcoded into Config.cs at build time.
$SkyboxLibraryRoot = "$env:USERPROFILE\Q Dropbox\3D Materials\Skybox Library\Skybox Library V2"

# Slack incoming webhook URL - hardcoded into Config.cs at build time.
# Leave empty to disable Slack notifications. To enable, create an Incoming
# Webhook at https://api.slack.com/apps and paste the URL here.
$SlackWebhookUrl = ""

# =====================================================================
# HELPERS
# =====================================================================
function Write-Step($msg) { Write-Host ""; Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-OK($msg)   { Write-Host "    [OK]   $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "    [WARN] $msg" -ForegroundColor Yellow }
function Write-Err($msg)  { Write-Host "    [FAIL] $msg" -ForegroundColor Red }
function Write-Info($msg) { Write-Host "    $msg"          -ForegroundColor Gray }

Write-Host ""
Write-Host "================================================================" -ForegroundColor Magenta
Write-Host "  qbiq Render Dispatcher - Setup script v1.0.0-sender (Create Ticket only)" -ForegroundColor Magenta
Write-Host "================================================================" -ForegroundColor Magenta

# =====================================================================
# STEP 1 - VALIDATE PREREQUISITES
# =====================================================================
Write-Step "Checking prerequisites"

$revitApiDll   = Join-Path $RevitInstallPath "RevitAPI.dll"
$revitApiUiDll = Join-Path $RevitInstallPath "RevitAPIUI.dll"
$adWindowsDll  = Join-Path $RevitInstallPath "AdWindows.dll"
$uiFrameworkDll = Join-Path $RevitInstallPath "UIFramework.dll"

if (-not (Test-Path $revitApiDll)) {
    Write-Err "RevitAPI.dll not found at: $revitApiDll"
    Write-Info "If Revit is installed in a different folder, edit `$RevitInstallPath at the top of this script."
    exit 1
}
if (-not (Test-Path $revitApiUiDll)) {
    Write-Err "RevitAPIUI.dll not found at: $revitApiUiDll"
    exit 1
}
if (-not (Test-Path $adWindowsDll)) {
    Write-Err "AdWindows.dll not found at: $adWindowsDll"
    Write-Info "This DLL is needed for ribbon API access."
    exit 1
}
if (-not (Test-Path $uiFrameworkDll)) {
    Write-Err "UIFramework.dll not found at: $uiFrameworkDll"
    exit 1
}
Write-OK "Revit $RevitVersion found at $RevitInstallPath"

$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) {
    Write-Err ".NET SDK (dotnet) not found in PATH."
    Write-Info "Install Visual Studio 2022 Community with the '.NET desktop development' workload."
    Write-Info "Download: https://visualstudio.microsoft.com/vs/community/"
    exit 1
}

$dotnetVersion = & dotnet --version
Write-OK ".NET SDK $dotnetVersion found"

# Block the build if Revit is running - it locks the plugin .dll in memory
$revitProcs = Get-Process -Name "Revit" -ErrorAction SilentlyContinue
if ($revitProcs) {
    Write-Host ""
    Write-Err "Revit is currently running (PID: $($revitProcs.Id -join ', '))."
    Write-Info "Revit holds the plugin DLL locked while loaded, so the build cannot replace it."
    Write-Info ""
    Write-Info "CLOSE Revit completely, then re-run this script."
    Write-Info "(Saving any open project first, of course.)"
    Write-Host ""
    exit 1
}
Write-OK "Revit is not running, build can proceed"

if (-not (Test-Path $DropboxQueueRoot)) {
    Write-Warn "Dropbox queue folder does not exist yet: $DropboxQueueRoot"
    Write-Info "The plugin will try to create the subfolders on first click."
    Write-Info "If Dropbox is not synced on this machine, the plugin will report an error at runtime."
} else {
    Write-OK "Dropbox queue folder found: $DropboxQueueRoot"
}

# =====================================================================
# STEP 2 - CREATE PROJECT STRUCTURE
# =====================================================================
Write-Step "Creating project structure at $ProjectRoot"

if (-not (Test-Path $ProjectRoot)) {
    New-Item -ItemType Directory -Path $ProjectRoot -Force | Out-Null
    Write-OK "Created $ProjectRoot"
} else {
    Write-OK "Project folder already exists"
}

# Clean up obsolete .cs files from previous versions so they don't get
# compiled alongside the current ones. Keep only the files this version writes.
$KeepFiles = @(
    'App.cs',
    'Config.cs',
    'RenderManifest.cs',
    'Logger.cs',
    'SlackNotifier.cs',
    'CreateTicketDialog.cs',
    'CreateTicketCommand.cs'
)

$existingCs = Get-ChildItem -Path $ProjectRoot -Filter "*.cs" -File -ErrorAction SilentlyContinue
foreach ($f in $existingCs) {
    if ($KeepFiles -notcontains $f.Name) {
        Remove-Item $f.FullName -Force
        Write-OK "Removed obsolete file: $($f.Name)"
    }
}

# Also nuke bin/obj so stale DLL references don't leak into the build
foreach ($dir in @('bin', 'obj')) {
    $path = Join-Path $ProjectRoot $dir
    if (Test-Path $path) {
        Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue
        Write-OK "Cleaned $dir\"
    }
}

# =====================================================================
# STEP 3 - WRITE PROJECT FILES
# =====================================================================
Write-Step "Writing project files"

# --- .csproj ---
$csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <OutputType>Library</OutputType>
    <AssemblyName>$ProjectName</AssemblyName>
    <RootNamespace>$ProjectName</RootNamespace>
    <LangVersion>latest</LangVersion>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>
    <Deterministic>true</Deterministic>
    <GenerateAssemblyInfo>true</GenerateAssemblyInfo>
    <Product>qbiq Render Dispatcher</Product>
    <Company>qbiq.ai</Company>
    <Version>1.0.0</Version>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="RevitAPI">
      <HintPath>$revitApiDll</HintPath>
      <Private>False</Private>
    </Reference>
    <Reference Include="RevitAPIUI">
      <HintPath>$revitApiUiDll</HintPath>
      <Private>False</Private>
    </Reference>
    <Reference Include="AdWindows">
      <HintPath>$RevitInstallPath\AdWindows.dll</HintPath>
      <Private>False</Private>
    </Reference>
    <Reference Include="UIFramework">
      <HintPath>$RevitInstallPath\UIFramework.dll</HintPath>
      <Private>False</Private>
    </Reference>
    <Reference Include="System.Web.Extensions" />
    <Reference Include="UIAutomationClient" />
    <Reference Include="UIAutomationTypes" />
    <Reference Include="WindowsBase" />
    <Reference Include="PresentationCore" />
    <Reference Include="PresentationFramework" />
    <Reference Include="System.Xaml" />
    <Reference Include="System.Windows.Forms" />
    <Reference Include="System.Drawing" />
  </ItemGroup>
</Project>
"@
Set-Content -Path (Join-Path $ProjectRoot "$ProjectName.csproj") -Value $csproj -Encoding UTF8
Write-OK "$ProjectName.csproj"

# --- App.cs ---
$appCs = @'
using Autodesk.Revit.UI;
using System;
using System.Reflection;

namespace QbiqRenderDispatcher
{
    /// <summary>
    /// Entry point for the Revit add-in (sender build).
    /// Adds a "Render Server" tab with only the "Create Ticket" button.
    /// Team members use this to queue renders without running them locally.
    /// </summary>
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            const string TabName    = "Render Server";
            const string PanelName  = "Tickets";

            try { application.CreateRibbonTab(TabName); }
            catch (Exception) { /* already exists */ }

            RibbonPanel panel = application.CreateRibbonPanel(TabName, PanelName);
            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            PushButtonData createBtn = new PushButtonData(
                name:         "CreateTicket",
                text:         "Create" + Environment.NewLine + "Ticket",
                assemblyName: assemblyPath,
                className:    "QbiqRenderDispatcher.CreateTicketCommand");

            createBtn.ToolTip =
                "Create a render ticket for the currently open .rvt file.\n" +
                "The ticket is sent to the render server queue automatically.";
            createBtn.LongDescription =
                "Generates a .json ticket using the active document's real path. " +
                "Auto-detects skybox files, view path .xml files and Revit views. " +
                "Drops the ticket into the Dropbox queue so the render server picks it up.";
            try { createBtn.LargeImage = RibbonIcons.CreateTicket(32); createBtn.Image = RibbonIcons.CreateTicket(16); } catch { }

            panel.AddItem(createBtn);
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }
    }
}
'@
Set-Content -Path (Join-Path $ProjectRoot "App.cs") -Value $appCs -Encoding UTF8
Write-OK "App.cs"

# --- Config.cs ---
# Paths are now resolved at runtime using Environment.SpecialFolder.UserProfile
# so the same pre-compiled DLL works on any team member's PC regardless of their Windows username.
$configCs = @"
using System;
using System.IO;

namespace QbiqRenderDispatcher
{
    /// <summary>
    /// Central configuration for the dispatcher.
    /// All paths and tunable settings live here.
    /// </summary>
    public static class Config
    {
        // Root folder of the render queue. Resolved at runtime from the current user's profile
        // so the same DLL works on any team member's PC regardless of their Windows username.
        public static string DropboxRoot
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    @"Q Dropbox\3D Projects\#_RenderServer");
            }
        }

        // Root folder of the skybox library. Resolved at runtime (same reason as above).
        public static string SkyboxLibraryRoot
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    @"Q Dropbox\3D Materials\Skybox Library\Skybox Library V2");
            }
        }

        // Slack incoming webhook URL. Empty = notifications disabled.
        public static readonly string SlackWebhookUrl =
            @"$SlackWebhookUrl";

        // Subfolders, computed off DropboxRoot.
        public static string PendingDir    { get { return Path.Combine(DropboxRoot, "pending");     } }
        public static string InProgressDir { get { return Path.Combine(DropboxRoot, "in-progress"); } }
        public static string CompletedDir  { get { return Path.Combine(DropboxRoot, "completed");   } }
        public static string ProcessedDir  { get { return Path.Combine(DropboxRoot, "processed");   } }
        public static string FailedDir     { get { return Path.Combine(DropboxRoot, "failed");      } }
        public static string LogsDir       { get { return Path.Combine(DropboxRoot, "logs");        } }

        public static string[] AllSubfolders
        {
            get
            {
                return new string[]
                {
                    PendingDir, InProgressDir, CompletedDir,
                    ProcessedDir, FailedDir, LogsDir
                };
            }
        }
    }
}
"@
Set-Content -Path (Join-Path $ProjectRoot "Config.cs") -Value $configCs -Encoding UTF8
Write-OK "Config.cs (paths resolved at runtime from UserProfile)"

# --- RenderManifest.cs ---
$manifestCs = @'
namespace QbiqRenderDispatcher
{
    /// <summary>
    /// Job manifest format. Users drop a .json file with this shape next to
    /// their .rvt to indicate the job is ready to be processed.
    ///
    /// IMPORTANT: property names use snake_case to match the JSON convention
    /// because System.Web.Script.Serialization.JavaScriptSerializer maps
    /// fields by exact name match. Do not rename without updating the JSON
    /// schema for users.
    /// </summary>
    public class RenderManifest
    {
        // === Ticket pointer (REQUIRED) ===
        // Absolute path to the .rvt in its real project folder.
        // The plugin opens the .rvt directly from this location, so linked models
        // and project-relative assets (like custom skyboxes) work transparently.
        public string rvt_path { get; set; }

        // === Identification ===
        public string project { get; set; }
        public string output_name { get; set; }

        // === Revit-side ===
        public string revit_view { get; set; }       // e.g. "3D Enscape" - the view to activate before rendering

        // === Enscape-side (consumed in v0.6+, validated now) ===
        public string enscape_preset { get; set; }   // e.g. "Qbiq preset" - the Visual Settings preset
        public string skybox_file { get; set; }      // resolved: project folder first, then SkyboxLibraryRoot
        public int    skybox_rotation { get; set; }  // skybox rotation in degrees (0-360, default 0)
        public string view_path_xml { get; set; }    // Enscape View Path .xml; resolved from project folder only

        // === Render output settings ===
        public string resolution { get; set; }
        public int fps { get; set; }
        public string quality { get; set; }

        // === Free-form ===
        public string notes { get; set; }

        // === Sender info (auto-populated by Create Ticket) ===
        // Used by the render server to auto-create path junctions
        // so Revit decals / linked files resolve on the render PC.
        public string sender_username    { get; set; }  // Windows username of the creator
        public string sender_dropbox_root { get; set; } // e.g. C:\Users\Eitan 3D\Q Dropbox
    }
}
'@
Set-Content -Path (Join-Path $ProjectRoot "RenderManifest.cs") -Value $manifestCs -Encoding UTF8
Write-OK "RenderManifest.cs"

# --- JobQueue.cs ---
$queueCs = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace QbiqRenderDispatcher
{
    /// <summary>
    /// Ticket-model job queue: each job is a .json file in pending\ that
    /// references the .rvt by absolute path (rvt_path field).
    ///
    /// The .rvt itself stays in its real project folder; only the .json
    /// (a few KB) is in the queue. Linked models and project-relative
    /// assets like custom skyboxes are reachable from the .rvt's own folder.
    /// </summary>
    public static class JobQueue
    {
        /// <summary>
        /// Ensures the queue subfolders exist. Creates them if missing.
        /// Returns true if the root is accessible and folders are ready.
        /// </summary>
        public static bool EnsureFolders(out string error)
        {
            error = null;

            try
            {
                if (!Directory.Exists(Config.DropboxRoot))
                {
                    error = "Dropbox root folder not found:\n" + Config.DropboxRoot +
                            "\n\nMake sure Dropbox is installed and synced on this machine, " +
                            "and that the path in Config.cs matches your local Dropbox layout.";
                    return false;
                }

                foreach (string sub in Config.AllSubfolders)
                {
                    if (!Directory.Exists(sub))
                    {
                        Directory.CreateDirectory(sub);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                error = "Could not prepare queue folders: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Returns the next ready job (oldest .json with a valid rvt_path
        /// pointing to an existing .rvt file). Returns null if no ready job.
        /// </summary>
        public static RenderJob PickNextJob()
        {
            FileInfo[] jsonFiles = new DirectoryInfo(Config.PendingDir)
                .GetFiles("*.json")
                .OrderBy(fi => fi.LastWriteTimeUtc)
                .ToArray();

            foreach (FileInfo jsonFi in jsonFiles)
            {
                RenderManifest manifest;
                if (!TryParseManifest(jsonFi.FullName, out manifest))
                    continue;

                if (manifest == null || string.IsNullOrEmpty(manifest.rvt_path))
                    continue;

                if (!File.Exists(manifest.rvt_path))
                    continue;

                return new RenderJob
                {
                    RvtPath      = manifest.rvt_path,
                    ManifestPath = jsonFi.FullName,
                    Manifest     = manifest
                };
            }

            return null;
        }

        /// <summary>
        /// Counts every .json file in the pending folder regardless of validity.
        /// </summary>
        public static int CountPendingTickets()
        {
            return Directory.GetFiles(Config.PendingDir, "*.json").Length;
        }

        /// <summary>
        /// Counts how many tickets are valid (parseable JSON + rvt_path exists).
        /// </summary>
        public static int CountReadyJobs()
        {
            int count = 0;
            foreach (string jsonPath in Directory.GetFiles(Config.PendingDir, "*.json"))
            {
                RenderManifest manifest;
                if (!TryParseManifest(jsonPath, out manifest)) continue;
                if (manifest == null || string.IsNullOrEmpty(manifest.rvt_path)) continue;
                if (!File.Exists(manifest.rvt_path)) continue;
                count++;
            }
            return count;
        }

        /// <summary>
        /// Returns a list of human-readable reasons why pending tickets are not ready.
        /// Useful for displaying diagnostics when CountReadyJobs() returns less than CountPendingTickets().
        /// </summary>
        public static List<string> GetInvalidTicketReasons()
        {
            List<string> reasons = new List<string>();

            foreach (string jsonPath in Directory.GetFiles(Config.PendingDir, "*.json"))
            {
                string fileName = Path.GetFileName(jsonPath);
                RenderManifest manifest;

                if (!TryParseManifest(jsonPath, out manifest))
                {
                    reasons.Add(fileName + ": invalid JSON or unreadable file");
                    continue;
                }

                if (manifest == null)
                {
                    reasons.Add(fileName + ": empty manifest");
                    continue;
                }

                if (string.IsNullOrEmpty(manifest.rvt_path))
                {
                    reasons.Add(fileName + ": missing 'rvt_path' field");
                    continue;
                }

                if (!File.Exists(manifest.rvt_path))
                {
                    reasons.Add(fileName + ": rvt_path does not exist on disk -> " + manifest.rvt_path);
                    continue;
                }
            }

            return reasons;
        }

        private static bool TryParseManifest(string jsonPath, out RenderManifest manifest)
        {
            manifest = null;
            string json;
            try { json = File.ReadAllText(jsonPath); }
            catch { return false; }

            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                manifest = serializer.Deserialize<RenderManifest>(json);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// A picked job ready to be processed (resolved .rvt path + manifest).
    /// </summary>
    public class RenderJob
    {
        public string RvtPath;        // absolute path to the .rvt (from manifest.rvt_path)
        public string ManifestPath;   // absolute path to the .json in pending\
        public RenderManifest Manifest;
    }
}
'@
Set-Content -Path (Join-Path $ProjectRoot "JobQueue.cs") -Value $queueCs -Encoding UTF8
Write-OK "JobQueue.cs"

# --- EnscapeAutomation.cs ---
$enscapeCs = @'
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

                    // Match "Enscape", "Enscape™", or starts with "Enscape"
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
                "Offnen", "Öffnen",       // German
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
'@
Set-Content -Path (Join-Path $ProjectRoot "EnscapeAutomation.cs") -Value $enscapeCs -Encoding UTF8
Write-OK "EnscapeAutomation.cs"

# --- TicketDialog.cs ---
$ticketDlgCs = @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace QbiqRenderDispatcher
{
    /// <summary>
    /// "Render Ticket" popup shown before the automation actually runs.
    /// Displays everything that's about to happen and lets the operator override
    /// preset / skybox / view path before clicking Render Now.
    ///
    /// Resolution / Compression / FPS are HARDCODED (Full HD / Maximum / 60).
    /// Output filename is auto-derived: "<project> VIDEO_<id>.mp4" inside
    /// the project folder (parent of rvt_path).
    /// </summary>
    public class TicketDialog : Form
    {
        private RenderJob _job;
        private string _projectFolder;

        // Buttons
        private Button _btnOpenVS;
        private Button _btnRender;
        private Button _btnCancel;

        // Outputs
        public string SelectedPreset       { get; private set; }
        public string SelectedSkyboxPath   { get; private set; }
        public string SelectedViewPathFile { get; private set; }
        public string OutputAbsolutePath   { get; private set; }
        public bool   OpenVisualSettingsRequested { get; private set; }

        public TicketDialog(RenderJob job)
        {
            _job = job;
            _projectFolder = Path.GetDirectoryName(job.RvtPath) ?? "";

            BuildUI();
        }

        // ============================================================
        // UI construction
        // ============================================================
        private void BuildUI()
        {
            this.Text            = "qbiq Render Dispatcher - Confirm Render";
            this.Size            = new Size(720, 640);
            this.StartPosition   = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MinimizeBox     = false;
            this.MaximizeBox     = false;
            this.BackColor       = Color.White;

            int leftCol  = 20;
            int rightCol = 180;
            int width    = 480;
            int rowH     = 28;
            int y        = 20;

            // ----- Project info (read-only) -----
            AddSection("Job", ref y);
            AddInfoRow("Project:", _job.Manifest.project ?? "(not specified)", leftCol, rightCol, width, ref y, rowH);
            AddInfoRow("File:",    Path.GetFileName(_job.RvtPath),             leftCol, rightCol, width, ref y, rowH);
            AddInfoRow("View:",    _job.Manifest.revit_view ?? "(default)",    leftCol, rightCol, width, ref y, rowH);

            y += 8;

            // ----- Visual Settings (read-only from ticket) -----
            // These values were chosen at ticket-creation time. They're displayed
            // here as a final review, not as editable fields, so the operator
            // can't accidentally diverge from what the ticket says.
            AddSection("Visual Settings (from ticket)", ref y);

            string presetDisplay   = string.IsNullOrEmpty(_job.Manifest.enscape_preset)
                                        ? "(use current preset)"
                                        : _job.Manifest.enscape_preset;
            string skyboxDisplay   = string.IsNullOrEmpty(_job.Manifest.skybox_file)
                                        ? "(keep current)"
                                        : _job.Manifest.skybox_file;
            string viewPathDisplay = string.IsNullOrEmpty(_job.Manifest.view_path_xml)
                                        ? "(none - render will fail!)"
                                        : _job.Manifest.view_path_xml;

            AddInfoRow("Preset:",    presetDisplay,   leftCol, rightCol, width, ref y, rowH);
            AddInfoRow("Skybox:",    skyboxDisplay,   leftCol, rightCol, width, ref y, rowH);
            AddInfoRow("View Path:", viewPathDisplay, leftCol, rightCol, width, ref y, rowH);

            y += 8;

            // ----- Locked export settings -----
            AddSection("Video Export (locked)", ref y);
            AddInfoRow("Resolution:",  "Full HD (1920 x 1080)", leftCol, rightCol, width, ref y, rowH);
            AddInfoRow("Compression:", "Maximum",               leftCol, rightCol, width, ref y, rowH);
            AddInfoRow("FPS:",         "60",                    leftCol, rightCol, width, ref y, rowH);

            y += 8;

            // ----- Output -----
            AddSection("Output", ref y);

            string outputName = DeriveOutputFilename(_job.RvtPath);
            string outputPath = Path.Combine(_projectFolder, outputName);
            OutputAbsolutePath = outputPath;

            AddInfoRow("Filename:", outputName,      leftCol, rightCol, width, ref y, rowH);
            AddInfoRow("Folder:",   _projectFolder,  leftCol, rightCol, width, ref y, rowH);

            // ----- Buttons -----
            int btnY = this.ClientSize.Height - 60;

            _btnOpenVS = new Button {
                Text     = "Open Visual Settings",
                Location = new Point(leftCol, btnY),
                Size     = new Size(180, 32),
                FlatStyle = FlatStyle.System
            };
            _btnOpenVS.Click += (s, e) => {
                OpenVisualSettingsRequested = true;
                this.DialogResult = DialogResult.Retry;
                this.Close();
            };
            this.Controls.Add(_btnOpenVS);

            _btnCancel = new Button {
                Text         = "Cancel",
                Location     = new Point(this.ClientSize.Width - 220, btnY),
                Size         = new Size(90, 32),
                DialogResult = DialogResult.Cancel,
                FlatStyle    = FlatStyle.System
            };
            this.Controls.Add(_btnCancel);

            _btnRender = new Button {
                Text         = "Render Now",
                Location     = new Point(this.ClientSize.Width - 120, btnY),
                Size         = new Size(100, 32),
                FlatStyle    = FlatStyle.Flat,
                BackColor    = Color.FromArgb(0, 120, 215),
                ForeColor    = Color.White,
                Font         = new Font(this.Font, FontStyle.Bold)
            };
            _btnRender.FlatAppearance.BorderSize = 0;
            _btnRender.Click += OnRenderNow;
            this.Controls.Add(_btnRender);

            this.AcceptButton = _btnRender;
            this.CancelButton = _btnCancel;
        }

        private void AddSection(string title, ref int y)
        {
            Label l = new Label {
                Text     = title.ToUpper(),
                Location = new Point(20, y),
                Size     = new Size(660, 18),
                Font     = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(80, 80, 80)
            };
            this.Controls.Add(l);

            Panel separator = new Panel {
                Location = new Point(20, y + 19),
                Size     = new Size(660, 1),
                BackColor = Color.FromArgb(220, 220, 220)
            };
            this.Controls.Add(separator);

            y += 26;
        }

        private void AddInfoRow(string label, string value, int leftCol, int rightCol, int width, ref int y, int rowH)
        {
            Label lbl = new Label {
                Text     = label,
                Location = new Point(leftCol, y + 4),
                Size     = new Size(rightCol - leftCol, rowH),
                ForeColor = Color.FromArgb(100, 100, 100)
            };
            this.Controls.Add(lbl);

            Label val = new Label {
                Text     = value,
                Location = new Point(rightCol, y + 4),
                Size     = new Size(width, rowH),
                Font     = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            this.Controls.Add(val);

            y += rowH;
        }

        // ============================================================
        // Submit
        // ============================================================
        private void OnRenderNow(object sender, EventArgs e)
        {
            // All values come straight from the ticket's manifest - the user
            // doesn't edit them in this dialog anymore (just reviews + confirms).
            RenderManifest m = _job.Manifest;

            SelectedPreset = (m.enscape_preset ?? "").Trim();

            // Skybox: project folder first, then SkyboxLibraryRoot
            string skybox = (m.skybox_file ?? "").Trim();
            if (string.IsNullOrEmpty(skybox))
                SelectedSkyboxPath = null;
            else if (Path.IsPathRooted(skybox))
                SelectedSkyboxPath = skybox;
            else
            {
                string p = Path.Combine(_projectFolder, skybox);
                SelectedSkyboxPath = File.Exists(p)
                    ? p
                    : Path.Combine(Config.SkyboxLibraryRoot, skybox);
            }

            // View Path XML: project folder only
            string xml = (m.view_path_xml ?? "").Trim();
            if (string.IsNullOrEmpty(xml))
                SelectedViewPathFile = null;
            else if (Path.IsPathRooted(xml))
                SelectedViewPathFile = xml;
            else
                SelectedViewPathFile = Path.Combine(_projectFolder, xml);

            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        // ============================================================
        // Static helper used elsewhere
        // ============================================================
        public static string DeriveOutputFilename(string rvtPath)
        {
            string baseName = Path.GetFileNameWithoutExtension(rvtPath);
            int lastUnderscore = baseName.LastIndexOf('_');
            if (lastUnderscore > 0 && lastUnderscore < baseName.Length - 1)
            {
                string projectName = baseName.Substring(0, lastUnderscore);
                string idPart      = baseName.Substring(lastUnderscore); // includes underscore
                return projectName + " VIDEO" + idPart + ".mp4";
            }
            return baseName + " VIDEO.mp4";
        }
    }
}
'@
Set-Content -Path (Join-Path $ProjectRoot "TicketDialog.cs") -Value $ticketDlgCs -Encoding UTF8
Write-OK "TicketDialog.cs"

# --- ProcessNextCommand.cs ---
$cmdCs = @'
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
                        "1. Click the 'Enscape™' tab in the Revit ribbon\n" +
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
'@
Set-Content -Path (Join-Path $ProjectRoot "ProcessNextCommand.cs") -Value $cmdCs -Encoding UTF8
Write-OK "ProcessNextCommand.cs"

# --- RenderCoordinator.cs ---
$renderCoordinatorCs = @'
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using System;
using System.IO;
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
        public string PresetStatus    = "(pending)";
        public string SkyboxStatus    = "(pending)";
        public string ViewPathStatus  = "(pending)";
        public string ExportStatus    = "(pending)";
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

        // Ceiling on how long the whole automation can take from
        // 'Render Now' to ticket finalization. Generous because Enscape
        // first-load + skybox apply can already eat ~60s.
        private const int MaxTotalSeconds = 600;  // 10 minutes

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

                // Bail out on overall timeout
                double elapsed = (DateTime.Now - ctx.StartedAt).TotalSeconds;
                if (elapsed > MaxTotalSeconds)
                {
                    Logger.Error("Coordinator",
                        "TIMEOUT after " + MaxTotalSeconds + "s. Aborting render.");
                    Finalize(uiApp, false, "Timeout: Enscape never became fully responsive within "
                        + MaxTotalSeconds + "s");
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

                if (!RunPresetSwitch(ctx))   { Finalize(uiApp, false, "Preset switch: "  + ctx.PresetStatus);   return; }
                if (!RunSkyboxSwitch(ctx))   { Finalize(uiApp, false, "Skybox switch: "  + ctx.SkyboxStatus);   return; }
                if (!RunViewPathLoad(ctx))   { Finalize(uiApp, false, "View Path XML: "  + ctx.ViewPathStatus); return; }
                if (!RunVideoExport(ctx))    { Finalize(uiApp, false, "Video Export: "   + ctx.ExportStatus);   return; }

                Finalize(uiApp, true, "");
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
                    "preset=" + Trunc(ctx.PresetStatus,   30) + " | " +
                    "skybox=" + Trunc(ctx.SkyboxStatus,   30) + " | " +
                    "export=" + Trunc(ctx.ExportStatus,   50));

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
                _ctx = null;
                if (uiApp != null) uiApp.Idling -= OnIdling;
            }
        }

        private static string Trunc(string s, int n)
        {
            if (s == null) return "";
            return s.Length <= n ? s : s.Substring(0, n);
        }
    }
}
'@
Set-Content -Path (Join-Path $ProjectRoot "RenderCoordinator.cs") -Value $renderCoordinatorCs -Encoding UTF8
Write-OK "RenderCoordinator.cs"

# --- SlackNotifier.cs ---
$slackNotifierCs = @'
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace QbiqRenderDispatcher
{
    /// <summary>
    /// Posts simple text messages to a Slack incoming webhook.
    /// Non-blocking: fires the request on a background thread so the render
    /// flow does not wait on network I/O. All errors are swallowed - a
    /// failed Slack post must never break the render pipeline.
    /// </summary>
    public static class SlackNotifier
    {
        /// <summary>
        /// Sends "Render started: <project>".
        /// </summary>
        public static void NotifyStarted(string project, string ticketName)
        {
            string text = ":hourglass_flowing_sand: *Render started* on the qbiq render server\n" +
                          "*Project:* " + Safe(project) + "\n" +
                          "*Ticket:* `" + Safe(ticketName) + "`";
            Send(text);
        }

        /// <summary>
        /// Sends a success message with output path. Server is now free.
        /// </summary>
        public static void NotifyCompleted(string project, string outputName, string ticketName)
        {
            string text = ":white_check_mark: *Render completed* - server is now free for the next job\n" +
                          "*Project:* " + Safe(project) + "\n" +
                          "*Output:* `" + Safe(outputName) + "`\n" +
                          "*Ticket:* `" + Safe(ticketName) + "`";
            Send(text);
        }

        /// <summary>
        /// Sends a failure message naming the step that failed. Server is free.
        /// </summary>
        public static void NotifyFailed(string project, string ticketName, string failureReason)
        {
            string text = ":x: *Render failed* - server is now free for the next job\n" +
                          "*Project:* " + Safe(project) + "\n" +
                          "*Ticket:* `" + Safe(ticketName) + "`\n" +
                          "*Reason:* " + Safe(failureReason);
            Send(text);
        }

        // -------------------------------------------------------------
        private static void Send(string text)
        {
            string url = Config.SlackWebhookUrl;
            if (string.IsNullOrEmpty(url)) return;   // Slack disabled

            // Fire-and-forget: don't block the render flow on network I/O
            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    string payload = "{\"text\":\"" + JsonEscape(text) + "\"}";
                    byte[] bytes   = Encoding.UTF8.GetBytes(payload);

                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method        = "POST";
                    req.ContentType   = "application/json";
                    req.ContentLength = bytes.Length;
                    req.Timeout       = 10000;

                    // TLS 1.2 (Slack requires it; .NET 4.8 default may pick lower)
                    try
                    {
                        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    }
                    catch { /* ignore on platforms that don't support it */ }

                    using (Stream s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    {
                        // Slack returns 200 + "ok" body when accepted; no need to read it
                    }
                }
                catch
                {
                    // Swallow all errors - Slack notification failure must never break render
                }
            });
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        private static string Safe(string s)
        {
            return string.IsNullOrEmpty(s) ? "(unknown)" : s;
        }
    }
}
'@
Set-Content -Path (Join-Path $ProjectRoot "SlackNotifier.cs") -Value $slackNotifierCs -Encoding UTF8
Write-OK "SlackNotifier.cs"

# --- Logger.cs ---
$loggerCs = @'
using System;
using System.IO;
using System.Threading;

namespace QbiqRenderDispatcher
{
    /// <summary>
    /// Verbose logger that appends timestamped lines to logs\dispatch.log.
    /// Used by the entire render flow to leave a paper trail of every step
    /// taken so we can debug failures after the fact.
    ///
    /// Non-blocking: all writes happen on the calling thread but errors are
    /// swallowed so a logging failure can never break the render.
    /// </summary>
    public static class Logger
    {
        private static readonly object _lock = new object();

        public static string GetLogPath()
        {
            return Path.Combine(Config.LogsDir, "dispatch.log");
        }

        /// <summary>
        /// Writes a section header to make scanning the log easier.
        /// </summary>
        public static void Section(string title)
        {
            AppendRaw("");
            AppendRaw("================================================================");
            AppendRaw(" " + title + "  -  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            AppendRaw("================================================================");
        }

        public static void Info(string step, string message)
        {
            Append("INFO ", step, message);
        }

        public static void Warn(string step, string message)
        {
            Append("WARN ", step, message);
        }

        public static void Error(string step, string message)
        {
            Append("ERROR", step, message);
        }

        public static void Step(string name)
        {
            AppendRaw("");
            AppendRaw("--- " + name + " ---");
        }

        // -----------------------------------------------------------
        private static void Append(string level, string step, string message)
        {
            string ts = DateTime.Now.ToString("HH:mm:ss.fff");
            int    tid = Thread.CurrentThread.ManagedThreadId;
            string line = ts + "  T" + tid + "  " + level + "  " + (step ?? "") + " | " + (message ?? "");
            AppendRaw(line);
        }

        private static void AppendRaw(string line)
        {
            try
            {
                lock (_lock)
                {
                    if (!Directory.Exists(Config.LogsDir))
                        Directory.CreateDirectory(Config.LogsDir);
                    File.AppendAllText(GetLogPath(), line + Environment.NewLine);
                }
            }
            catch
            {
                // swallow - log failure must never break the render flow
            }
        }
    }
}
'@
Set-Content -Path (Join-Path $ProjectRoot "Logger.cs") -Value $loggerCs -Encoding UTF8
Write-OK "Logger.cs"

# --- RibbonIcons.cs ---
$ribbonIconsCs = @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace QbiqRenderDispatcher
{
    /// <summary>
    /// Generates ribbon button icons at runtime using System.Drawing,
    /// so no external PNG files need to be shipped with the plugin.
    ///
    /// Three icons, each available at 32x32 (LargeImage) and 16x16 (Image):
    ///   - RenderServerIcon: green rounded-square with a white "play" triangle
    ///   - ViewLogIcon:      dark gray rounded-square with a white "document" + lines
    ///   - CreateTicketIcon: blue rounded-square with a white paper airplane
    /// </summary>
    public static class RibbonIcons
    {
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteObject(IntPtr hObject);

        // ===== Public factories =====================================

        public static BitmapSource RenderServer(int size = 32)
        {
            return Draw(size, (g, s) =>
            {
                FillRoundedBg(g, s, Color.FromArgb(30, 130, 60));   // green
                DrawPlayTriangle(g, s, Color.White);
            });
        }

        public static BitmapSource ViewLog(int size = 32)
        {
            return Draw(size, (g, s) =>
            {
                FillRoundedBg(g, s, Color.FromArgb(70, 70, 75));    // dark gray
                DrawDocument(g, s, Color.White, Color.FromArgb(70, 70, 75));
            });
        }

        public static BitmapSource CreateTicket(int size = 32)
        {
            return Draw(size, (g, s) =>
            {
                FillRoundedBg(g, s, Color.FromArgb(0, 120, 215));   // qbiq blue
                DrawPaperAirplane(g, s, Color.White);
            });
        }

        // ===== Drawing primitives ===================================

        private static BitmapSource Draw(int size, Action<Graphics, int> paint)
        {
            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode     = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    paint(g, size);
                }
                return BitmapToSource(bmp);
            }
        }

        private static void FillRoundedBg(Graphics g, int size, Color color)
        {
            using (var brush = new SolidBrush(color))
            using (var path = RoundedRectPath(0, 0, size, size, Math.Max(2f, size / 6f)))
            {
                g.FillPath(brush, path);
            }
        }

        private static void DrawPlayTriangle(Graphics g, int size, Color color)
        {
            float pad = size * 0.27f;
            var pts = new PointF[]
            {
                new PointF(pad,            pad),
                new PointF(pad,            size - pad),
                new PointF(size - pad,     size * 0.5f)
            };
            using (var brush = new SolidBrush(color))
            {
                g.FillPolygon(brush, pts);
            }
        }

        private static void DrawDocument(Graphics g, int size, Color paper, Color lineColor)
        {
            float marginX = size * 0.22f;
            float marginY = size * 0.18f;
            float paperW  = size - 2 * marginX;
            float paperH  = size - 2 * marginY;
            float foldSz  = size * 0.18f;

            // Paper (rounded rect with one folded corner) - draw as path
            using (var path = new GraphicsPath())
            {
                path.AddLine(marginX,                     marginY,
                             marginX + paperW - foldSz,   marginY);
                path.AddLine(marginX + paperW - foldSz,   marginY,
                             marginX + paperW,            marginY + foldSz);
                path.AddLine(marginX + paperW,            marginY + foldSz,
                             marginX + paperW,            marginY + paperH);
                path.AddLine(marginX + paperW,            marginY + paperH,
                             marginX,                     marginY + paperH);
                path.AddLine(marginX,                     marginY + paperH,
                             marginX,                     marginY);
                path.CloseFigure();

                using (var fill = new SolidBrush(paper))
                {
                    g.FillPath(fill, path);
                }
            }

            // Horizontal lines (mock text)
            float lineY0     = marginY + paperH * 0.30f;
            float lineSpace  = paperH * 0.18f;
            float lineX0     = marginX + paperW * 0.12f;
            float lineXEnd   = marginX + paperW * 0.85f;
            using (var pen = new Pen(lineColor, Math.Max(1f, size / 18f)))
            {
                for (int i = 0; i < 3; i++)
                {
                    float y    = lineY0 + i * lineSpace;
                    float xEnd = (i == 2) ? marginX + paperW * 0.55f : lineXEnd;
                    g.DrawLine(pen, lineX0, y, xEnd, y);
                }
            }
        }

        private static void DrawPaperAirplane(Graphics g, int size, Color color)
        {
            // Two-triangle paper-airplane silhouette pointing right.
            // Body
            var body = new PointF[]
            {
                new PointF(size * 0.15f, size * 0.50f),  // tail tip
                new PointF(size * 0.85f, size * 0.22f),  // nose top
                new PointF(size * 0.50f, size * 0.58f)   // fold (back)
            };
            // Wing fold (under body)
            var wing = new PointF[]
            {
                new PointF(size * 0.50f, size * 0.58f),
                new PointF(size * 0.85f, size * 0.22f),
                new PointF(size * 0.62f, size * 0.80f)
            };
            using (var brush     = new SolidBrush(color))
            using (var shadowBrh = new SolidBrush(Color.FromArgb(180, 0, 90, 170)))
            {
                g.FillPolygon(brush,     body);
                g.FillPolygon(shadowBrh, wing);
            }
        }

        private static GraphicsPath RoundedRectPath(float x, float y, float w, float h, float r)
        {
            float d = r * 2f;
            var path = new GraphicsPath();
            path.AddArc(x,             y,             d, d, 180, 90);
            path.AddArc(x + w - d,     y,             d, d, 270, 90);
            path.AddArc(x + w - d,     y + h - d,     d, d,   0, 90);
            path.AddArc(x,             y + h - d,     d, d,  90, 90);
            path.CloseFigure();
            return path;
        }

        private static BitmapSource BitmapToSource(Bitmap bmp)
        {
            IntPtr hBmp = bmp.GetHbitmap();
            try
            {
                BitmapSource src = Imaging.CreateBitmapSourceFromHBitmap(
                    hBmp,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally
            {
                DeleteObject(hBmp);
            }
        }
    }
}
'@
Set-Content -Path (Join-Path $ProjectRoot "RibbonIcons.cs") -Value $ribbonIconsCs -Encoding UTF8
Write-OK "RibbonIcons.cs"

# --- ViewLogCommand.cs ---
$viewLogCommandCs = @'
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Diagnostics;
using System.IO;

namespace QbiqRenderDispatcher
{
    /// <summary>
    /// Opens logs\dispatch.log in Notepad so the user can inspect the
    /// trace of recent renders.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ViewLogCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            string folderError;
            if (!JobQueue.EnsureFolders(out folderError))
            {
                ShowError("Cannot access queue folders", folderError);
                return Result.Cancelled;
            }

            string path = Logger.GetLogPath();

            if (!File.Exists(path))
            {
                ShowInfo("Log file not yet created",
                    "No render has run yet, so " + path + " does not exist.\n\n" +
                    "Run \"Process Next Render\" once to generate log entries.");
                return Result.Succeeded;
            }

            try
            {
                Process.Start("notepad.exe", "\"" + path + "\"");
            }
            catch (System.Exception ex)
            {
                ShowError("Could not open log file",
                    "Path: " + path + "\n\nError: " + ex.Message);
                return Result.Failed;
            }

            return Result.Succeeded;
        }

        private static void ShowInfo(string title, string content)
        {
            TaskDialog dlg = new TaskDialog("Render Dispatcher")
            {
                MainInstruction = title, MainContent = content,
                CommonButtons = TaskDialogCommonButtons.Ok
            };
            dlg.Show();
        }

        private static void ShowError(string title, string content)
        {
            TaskDialog dlg = new TaskDialog("Render Dispatcher")
            {
                MainInstruction = title, MainContent = content,
                CommonButtons = TaskDialogCommonButtons.Ok,
                MainIcon = TaskDialogIcon.TaskDialogIconError
            };
            dlg.Show();
        }
    }
}
'@
Set-Content -Path (Join-Path $ProjectRoot "ViewLogCommand.cs") -Value $viewLogCommandCs -Encoding UTF8
Write-OK "ViewLogCommand.cs"

# --- CreateTicketDialog.cs ---
$createTicketDialogCs = @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace QbiqRenderDispatcher
{
    /// <summary>
    /// Dialog used by CreateTicketCommand to capture manifest fields.
    /// The .rvt path is fixed (active document) and not editable - that's the
    /// whole point: the user cannot mistype it.
    /// </summary>
    public class CreateTicketDialog : Form
    {
        public string Project          { get; private set; }
        public string EnscapePreset    { get; private set; }
        public string SkyboxFileName   { get; private set; }
        public int    SkyboxRotation   { get; private set; }
        public string ViewPathXmlName  { get; private set; }
        public string RevitView        { get; private set; }
        public string Notes            { get; private set; }

        private readonly string _rvtPath;
        private readonly string _projectFolder;
        private readonly List<string> _viewNames;

        private TextBox        _txtProject;
        private TextBox        _txtPreset;
        private ComboBox       _cboSkybox;
        private NumericUpDown  _numRotation;
        private ComboBox       _cboViewPath;
        private ComboBox       _cboRevitView;
        private TextBox        _txtNotes;

        public CreateTicketDialog(string rvtPath, List<string> revitViewNames, string activeViewName)
        {
            _rvtPath       = rvtPath;
            _projectFolder = Path.GetDirectoryName(rvtPath) ?? "";
            _viewNames     = revitViewNames ?? new List<string>();

            BuildUi(activeViewName);
        }

        private void BuildUi(string activeViewName)
        {
            this.Text            = "Create Render Ticket";
            this.StartPosition   = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MinimizeBox     = false;
            this.MaximizeBox     = false;
            this.Width           = 720;
            this.Height          = 600;
            this.Font            = new Font("Segoe UI", 9F);

            int leftCol  = 20;
            int rightCol = 200;
            int width    = 480;
            int rowH     = 26;
            int y        = 20;

            AddLabel("RVT path:", leftCol, y);
            AddInfo(_rvtPath, rightCol, y, width);
            y += rowH;

            AddLabel("Project folder:", leftCol, y);
            AddInfo(_projectFolder, rightCol, y, width);
            y += rowH + 8;

            AddLabel("Project name:", leftCol, y);
            _txtProject = new TextBox { Left = rightCol, Top = y - 3, Width = width, Text = Path.GetFileName(_projectFolder) };
            this.Controls.Add(_txtProject);
            y += rowH;

            AddLabel("Enscape preset:", leftCol, y);
            _txtPreset = new TextBox { Left = rightCol, Top = y - 3, Width = width, Text = "Qbiq preset" };
            this.Controls.Add(_txtPreset);
            y += rowH;

            AddLabel("Skybox (optional):", leftCol, y);
            _cboSkybox = new ComboBox { Left = rightCol, Top = y - 3, Width = width, DropDownStyle = ComboBoxStyle.DropDownList };
            _cboSkybox.Items.Add("(none - keep current)");
            foreach (string s in DiscoverSkyboxes()) _cboSkybox.Items.Add(s);
            _cboSkybox.SelectedIndex = 0;
            this.Controls.Add(_cboSkybox);
            y += rowH;

            AddLabel("Skybox rotation (°):", leftCol, y);
            _numRotation = new NumericUpDown
            {
                Left = rightCol, Top = y - 3, Width = 80,
                Minimum = 0, Maximum = 360, Value = 0, DecimalPlaces = 0
            };
            this.Controls.Add(_numRotation);
            y += rowH;

            AddLabel("View Path XML:", leftCol, y);
            _cboViewPath = new ComboBox { Left = rightCol, Top = y - 3, Width = width, DropDownStyle = ComboBoxStyle.DropDownList };
            _cboViewPath.Items.Add("(none - render will fail)");
            foreach (string xml in DiscoverViewPathXmls()) _cboViewPath.Items.Add(xml);
            _cboViewPath.SelectedIndex = _cboViewPath.Items.Count > 1 ? 1 : 0;
            this.Controls.Add(_cboViewPath);
            y += rowH;

            AddLabel("Revit view (locked):", leftCol, y);
            // Revit view is always "3D Enscape" - that's the standardized view used for renders.
            // Keep the field as a disabled ComboBox so the manifest schema stays the same and the
            // OnSaveClicked code that reads .Text still works.
            _cboRevitView = new ComboBox { Left = rightCol, Top = y - 3, Width = width, DropDownStyle = ComboBoxStyle.DropDownList };
            _cboRevitView.Items.Add("3D Enscape");
            _cboRevitView.SelectedIndex = 0;
            _cboRevitView.Enabled = false;
            this.Controls.Add(_cboRevitView);
            y += rowH + 8;

            AddLabel("Notes (optional):", leftCol, y);
            _txtNotes = new TextBox { Left = rightCol, Top = y - 3, Width = width, Height = 60, Multiline = true };
            this.Controls.Add(_txtNotes);
            y += 70;

            AddLabel("Locked:", leftCol, y);
            AddInfo("Resolution: Full HD (1920x1080)  -  FPS: 60  -  Quality: Maximum", rightCol, y, width);
            y += rowH + 16;

            Button btnCancel = new Button { Text = "Cancel", Left = rightCol + width - 200, Top = y, Width = 90, Height = 30 };
            btnCancel.Click += (s, e) => { this.DialogResult = DialogResult.Cancel; this.Close(); };
            this.Controls.Add(btnCancel);

            Button btnSave = new Button { Text = "Save Ticket", Left = rightCol + width - 100, Top = y, Width = 100, Height = 30 };
            btnSave.Click += OnSaveClicked;
            this.AcceptButton = btnSave;
            this.Controls.Add(btnSave);
        }

        private void AddLabel(string text, int x, int y)
        {
            Label lbl = new Label { Text = text, Left = x, Top = y, Width = 170, Font = new Font("Segoe UI", 9F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
            this.Controls.Add(lbl);
        }

        private void AddInfo(string text, int x, int y, int width)
        {
            Label lbl = new Label { Text = text, Left = x, Top = y, Width = width, Height = 22, ForeColor = Color.DarkSlateGray, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            this.Controls.Add(lbl);
        }


        private List<string> DiscoverSkyboxes()
        {
            List<string> result = new List<string>();
            string[] exts = new[] { ".hdr", ".exr", ".jpg", ".jpeg", ".png" };

            if (Directory.Exists(_projectFolder))
            {
                foreach (string f in Directory.GetFiles(_projectFolder))
                {
                    string ext = Path.GetExtension(f).ToLower();
                    if (Array.IndexOf(exts, ext) >= 0)
                        result.Add(Path.GetFileName(f) + "  [project folder]");
                }
            }
            if (Directory.Exists(Config.SkyboxLibraryRoot))
            {
                foreach (string f in Directory.GetFiles(Config.SkyboxLibraryRoot, "*.*", SearchOption.AllDirectories))
                {
                    string ext = Path.GetExtension(f).ToLower();
                    if (Array.IndexOf(exts, ext) >= 0)
                    {
                        string rel = f.Substring(Config.SkyboxLibraryRoot.Length).TrimStart('\\', '/');
                        result.Add(rel + "  [library]");
                    }
                }
            }
            return result;
        }

        private List<string> DiscoverViewPathXmls()
        {
            List<string> result = new List<string>();
            if (Directory.Exists(_projectFolder))
                foreach (string f in Directory.GetFiles(_projectFolder, "*.xml"))
                    result.Add(Path.GetFileName(f));
            return result;
        }

        private void OnSaveClicked(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_txtProject.Text))
            {
                MessageBox.Show(this, "Project name is required.", "Missing field", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_cboViewPath.SelectedIndex <= 0)
            {
                DialogResult ans = MessageBox.Show(this,
                    "No View Path XML selected - render will fail without one.\n\nSave anyway?",
                    "Missing View Path", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (ans != DialogResult.Yes) return;
            }

            Project       = _txtProject.Text.Trim();
            EnscapePreset = _txtPreset.Text.Trim();

            string skyRaw = _cboSkybox.SelectedIndex > 0 ? (string)_cboSkybox.SelectedItem : "";
            SkyboxFileName = StripTag(skyRaw);
            SkyboxRotation = (int)_numRotation.Value;

            string vpRaw = _cboViewPath.SelectedIndex > 0 ? (string)_cboViewPath.SelectedItem : "";
            ViewPathXmlName = StripTag(vpRaw);

            RevitView = _cboRevitView.Text.Trim();
            Notes     = _txtNotes.Text.Trim();

            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private static string StripTag(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int idx = s.IndexOf("  [");
            return idx >= 0 ? s.Substring(0, idx) : s;
        }
    }
}
'@
Set-Content -Path (Join-Path $ProjectRoot "CreateTicketDialog.cs") -Value $createTicketDialogCs -Encoding UTF8
Write-OK "CreateTicketDialog.cs"

# --- CreateTicketCommand.cs ---
$createTicketCommandCs = @'
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace QbiqRenderDispatcher
{
    /// <summary>
    /// Generates a render ticket (.json) for the currently open .rvt and
    /// drops it into pending\. Always uses Document.PathName, so rvt_path
    /// is guaranteed correct.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CreateTicketCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument    uiDoc = uiApp.ActiveUIDocument;

            if (uiDoc == null || uiDoc.Document == null)
            {
                ShowError("No active document",
                    "Open the .rvt you want to render before clicking Create Ticket.");
                return Result.Cancelled;
            }

            Document doc = uiDoc.Document;
            string   rvt = doc.PathName;

            if (string.IsNullOrEmpty(rvt))
            {
                ShowError("Document not saved",
                    "The active document has no path on disk. Save it first (File -> Save As).");
                return Result.Cancelled;
            }

            if (!File.Exists(rvt))
            {
                ShowError("Document path invalid",
                    "Document.PathName reports:\n   " + rvt + "\n\n...but that file does not exist. Save the document first.");
                return Result.Cancelled;
            }

            string folderError;
            if (!JobQueue.EnsureFolders(out folderError))
            {
                ShowError("Cannot access queue folders", folderError);
                return Result.Cancelled;
            }

            List<string> viewNames = new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate &&
                            (v.ViewType == ViewType.ThreeD ||
                             v.ViewType == ViewType.FloorPlan ||
                             v.ViewType == ViewType.CeilingPlan ||
                             v.ViewType == ViewType.Elevation ||
                             v.ViewType == ViewType.Section))
                .Select(v => v.Name).Distinct().OrderBy(n => n).ToList();

            string activeViewName = uiDoc.ActiveView != null ? uiDoc.ActiveView.Name : "";

            CreateTicketDialog dlg = new CreateTicketDialog(rvt, viewNames, activeViewName);
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return Result.Cancelled;

            string projectFolder = Path.GetDirectoryName(rvt) ?? "";
            string ticketId      = DateTime.Now.ToString("yyyyMMddHHmmssfff");
            string projectClean  = string.IsNullOrEmpty(dlg.Project)
                ? Path.GetFileNameWithoutExtension(rvt)
                : dlg.Project;
            string outputName    = projectClean + " VIDEO_" + ticketId + ".mp4";

            string json = BuildJson(rvt, projectClean, outputName, dlg.RevitView,
                dlg.EnscapePreset, dlg.SkyboxFileName, dlg.SkyboxRotation, dlg.ViewPathXmlName, dlg.Notes);

            string ticketBaseName = SafeForFileName(projectClean) + "_" + ticketId + ".json";
            string ticketPath     = Path.Combine(Config.PendingDir, ticketBaseName);

            try
            {
                File.WriteAllText(ticketPath, json);
            }
            catch (Exception ex)
            {
                ShowError("Could not write ticket",
                    "Path: " + ticketPath + "\n\nError: " + ex.GetType().Name + "\n" + ex.Message);
                return Result.Failed;
            }

            ShowInfo("Ticket created",
                "Ticket saved to:\n   " + ticketPath + "\n\n" +
                "RVT path:       " + rvt + "\n" +
                "Project:        " + projectClean + "\n" +
                "Preset:         " + NotEmpty(dlg.EnscapePreset, "(none)") + "\n" +
                "Skybox:         " + NotEmpty(dlg.SkyboxFileName, "(none)") + "\n" +
                "View Path XML:  " + NotEmpty(dlg.ViewPathXmlName, "(none)") + "\n" +
                "Revit view:     " + NotEmpty(dlg.RevitView, "(active)") + "\n" +
                "Output target:  " + outputName + "\n\n" +
                "Click \"Process Next Render\" to render it.");

            return Result.Succeeded;
        }

        private static string BuildJson(string rvt, string project, string outputName, string revitView,
            string preset, string skyboxFile, int skyboxRotation, string viewPathXml, string notes)
        {
            // Sender info for auto-junction on the render PC
            string senderUser    = System.Environment.UserName;
            string senderDropbox = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Config.DropboxRoot, "..", ".."));

            StringBuilder sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"rvt_path\":           \"").Append(JsonEscape(rvt)).Append("\",\n");
            sb.Append("  \"project\":            \"").Append(JsonEscape(project)).Append("\",\n");
            sb.Append("  \"output_name\":        \"").Append(JsonEscape(outputName)).Append("\",\n");
            sb.Append("  \"revit_view\":         \"").Append(JsonEscape(revitView ?? "")).Append("\",\n");
            sb.Append("  \"enscape_preset\":     \"").Append(JsonEscape(preset ?? "")).Append("\",\n");
            sb.Append("  \"skybox_file\":        \"").Append(JsonEscape(skyboxFile ?? "")).Append("\",\n");
            sb.Append("  \"skybox_rotation\":    ").Append(skyboxRotation).Append(",\n");
            sb.Append("  \"view_path_xml\":      \"").Append(JsonEscape(viewPathXml ?? "")).Append("\",\n");
            sb.Append("  \"resolution\":         \"Full HD (1920x1080)\",\n");
            sb.Append("  \"fps\":                60,\n");
            sb.Append("  \"quality\":            \"Maximum\",\n");
            sb.Append("  \"notes\":              \"").Append(JsonEscape(notes ?? "")).Append("\",\n");
            sb.Append("  \"sender_username\":    \"").Append(JsonEscape(senderUser)).Append("\",\n");
            sb.Append("  \"sender_dropbox_root\":\"").Append(JsonEscape(senderDropbox)).Append("\"\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        private static string SafeForFileName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "ticket";
            char[] invalid = Path.GetInvalidFileNameChars();
            char[] arr     = s.ToCharArray();
            for (int i = 0; i < arr.Length; i++)
                if (Array.IndexOf(invalid, arr[i]) >= 0) arr[i] = '_';
            return new string(arr);
        }

        private static string NotEmpty(string v, string fb) { return string.IsNullOrEmpty(v) ? fb : v; }

        private static void ShowInfo(string title, string content)
        {
            TaskDialog dlg = new TaskDialog("Render Dispatcher")
            {
                MainInstruction = title, MainContent = content,
                CommonButtons = TaskDialogCommonButtons.Ok
            };
            dlg.Show();
        }

        private static void ShowError(string title, string content)
        {
            TaskDialog dlg = new TaskDialog("Render Dispatcher")
            {
                MainInstruction = title, MainContent = content,
                CommonButtons = TaskDialogCommonButtons.Ok,
                MainIcon = TaskDialogIcon.TaskDialogIconError
            };
            dlg.Show();
        }
    }
}
'@
Set-Content -Path (Join-Path $ProjectRoot "CreateTicketCommand.cs") -Value $createTicketCommandCs -Encoding UTF8
Write-OK "CreateTicketCommand.cs"

# =====================================================================
# STEP 4 - BUILD
# =====================================================================
Write-Step "Building the add-in"

Push-Location $ProjectRoot
try {
    Write-Info "Running: dotnet build -c Release"
    Write-Host ""

    $buildOutput = & dotnet build -c Release --nologo --verbosity minimal 2>&1
    $exitCode = $LASTEXITCODE

    $buildOutput | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }

    if ($exitCode -ne 0) {
        Write-Host ""
        Write-Err "Build FAILED with exit code $exitCode"
        Write-Info "Copy the output above and send it to Claude."
        exit 1
    }
}
finally {
    Pop-Location
}

$builtDll = Join-Path $ProjectRoot "bin\Release\$ProjectName.dll"
if (-not (Test-Path $builtDll)) {
    Write-Err "Build reported success but DLL not found at $builtDll"
    exit 1
}

$dllInfo = Get-Item $builtDll
Write-OK "Built $ProjectName.dll ($([Math]::Round($dllInfo.Length / 1KB, 1)) KB) at $builtDll"

# =====================================================================
# STEP 5 - DEPLOY .ADDIN MANIFEST
# =====================================================================
Write-Step "Deploying .addin manifest"

if (-not (Test-Path $AddinDir)) {
    New-Item -ItemType Directory -Path $AddinDir -Force | Out-Null
    Write-OK "Created Revit addins folder"
}

$addinXml = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>qbiq Render Dispatcher</Name>
    <Assembly>$builtDll</Assembly>
    <FullClassName>QbiqRenderDispatcher.App</FullClassName>
    <ClientId>a1b2c3d4-e5f6-7890-abcd-ef1234567890</ClientId>
    <VendorId>QBIQ</VendorId>
    <VendorDescription>qbiq.ai</VendorDescription>
  </AddIn>
</RevitAddIns>
"@

$addinFile = Join-Path $AddinDir "QbiqRenderDispatcher.addin"
Set-Content -Path $addinFile -Value $addinXml -Encoding UTF8
Write-OK "Wrote $addinFile"

# =====================================================================
# DONE
# =====================================================================
Write-Host ""
Write-Host "================================================================" -ForegroundColor Green
Write-Host "  Setup completed successfully (v1.0.1-sender)" -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Green
Write-Host ""
Write-Host "  DLL:      $builtDll" -ForegroundColor Gray
Write-Host "  Manifest: $addinFile" -ForegroundColor Gray
Write-Host ""
Write-Host "  Open Revit and look for the 'Render Server' tab." -ForegroundColor Cyan
Write-Host ""
