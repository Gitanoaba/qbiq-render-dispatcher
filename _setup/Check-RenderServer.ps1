#requires -Version 5.0
<#
.SYNOPSIS
    qbiq Render Dispatcher - Inspeccion read-only de la PC render server.
.DESCRIPTION
    Para correr en la OTRA PC (la que va a hacer de render server, no en la
    laptop de Eitan).

    Reporta TODO lo que necesitamos para disenar el auto-wake / auto-launch:
      - identidad de la maquina, usuario, version de Windows
      - estado de Revit 2024, Enscape, Dropbox y la cola
      - Wake-on-LAN: capacidades de cada NIC, MACs, IPs, subnet
      - Power: planes activos, sleep/hibernate disponibles, Modern Standby
      - Auto-login: estado del registro Winlogon
      - Startup: items en Run/RunOnce y carpeta Startup
      - Task Scheduler: tareas relacionadas con Revit / Enscape / Render
      - Add-ins de Revit ya instalados

    No modifica nada. Genera un solo archivo de reporte en el escritorio.
    Mandalo de vuelta a Claude.
#>

$ErrorActionPreference = 'Continue'
$ProgressPreference    = 'SilentlyContinue'

$reportFile = Join-Path $env:USERPROFILE 'Desktop\render_server_inspect.txt'

function Out($text)        { Add-Content -Path $reportFile -Value $text }
function Header($title) {
    Out ""
    Out "================================================================"
    Out " $title"
    Out "================================================================"
}
function Mark($ok) { if ($ok) { "[OK]  " } else { "[MISS]" } }
function Line($label, $ok, $detail) { Out ("{0} {1,-40} {2}" -f (Mark $ok), $label, $detail) }

# Reset report
Set-Content -Path $reportFile -Value "qbiq Render Dispatcher - Render server inspection"
Out "Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Out ""

Write-Host ""
Write-Host "================================================================" -ForegroundColor Magenta
Write-Host "  qbiq Render Dispatcher - Render server inspection (read-only)" -ForegroundColor Magenta
Write-Host "================================================================" -ForegroundColor Magenta
Write-Host ""

# =====================================================================
# 0. IDENTIDAD DE LA MAQUINA
# =====================================================================
Header "0. IDENTIDAD"
Out ("Computer:     {0}" -f $env:COMPUTERNAME)
Out ("User:         {0}" -f $env:USERNAME)
Out ("UserHome:     {0}" -f $env:USERPROFILE)
Out ("PSVersion:    {0}" -f $PSVersionTable.PSVersion)
try {
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
    Out ("Windows:      {0} (build {1})" -f $os.Caption, $os.BuildNumber)
    Out ("Install date: {0}" -f $os.InstallDate)
    Out ("Last boot:    {0}" -f $os.LastBootUpTime)
} catch { }
try {
    $cs = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
    Out ("Manufacturer: {0}" -f $cs.Manufacturer)
    Out ("Model:        {0}" -f $cs.Model)
    Out ("Domain:       {0}" -f $cs.Domain)
    Out ("RAM (GB):     {0:N1}" -f ($cs.TotalPhysicalMemory / 1GB))
} catch { }

# =====================================================================
# 1. REVIT 2024
# =====================================================================
Header "1. REVIT 2024"

$revitDirs = @(
    "C:\Program Files\Autodesk\Revit 2024",
    "D:\Program Files\Autodesk\Revit 2024"
)
$revitDir = $null
foreach ($d in $revitDirs) { if (Test-Path $d) { $revitDir = $d; break } }

Line "Revit 2024 install" ($null -ne $revitDir) ($revitDir)
if ($revitDir) {
    foreach ($dll in @('RevitAPI.dll','RevitAPIUI.dll','AdWindows.dll','UIFramework.dll','Revit.exe')) {
        $p = Join-Path $revitDir $dll
        Line ("  $dll") (Test-Path $p) $p
    }
    $revitProcs = Get-Process -Name "Revit" -ErrorAction SilentlyContinue
    Line "Revit running" ($null -ne $revitProcs) ((($revitProcs.Id) -join ',') + " (cerrarlo si esta antes de re-deploy)")
}

# =====================================================================
# 2. ENSCAPE
# =====================================================================
Header "2. ENSCAPE"

$enscapeRoot = $null
foreach ($d in @("C:\Program Files\Enscape","C:\Program Files (x86)\Enscape")) {
    if (Test-Path $d) { $enscapeRoot = $d; break }
}
Line "Enscape install" ($null -ne $enscapeRoot) ($enscapeRoot)
$enscapeHost = "C:\Program Files\Enscape\RendererHost\Enscape.RendererHost.exe"
Line "  Enscape.RendererHost.exe" (Test-Path $enscapeHost) $enscapeHost
$enscapeAddin = "C:\ProgramData\Autodesk\Revit\Addins\2024\0_Enscape.addin"
Line "Enscape Revit 2024 add-in" (Test-Path $enscapeAddin) $enscapeAddin

$enscapeProcs = Get-Process -Name 'Enscape*' -ErrorAction SilentlyContinue
if ($enscapeProcs) {
    Out "Enscape procs corriendo:"
    foreach ($p in $enscapeProcs) { Out ("  PID {0,-6} {1}" -f $p.Id, $p.ProcessName) }
}

# =====================================================================
# 3. DROPBOX + COLA DE RENDER
# =====================================================================
Header "3. DROPBOX + COLA DE RENDER"

$dbxInfo = Join-Path $env:LOCALAPPDATA 'Dropbox\info.json'
Line "Dropbox info.json" (Test-Path $dbxInfo) $dbxInfo
$dropboxRoots = @()
if (Test-Path $dbxInfo) {
    try {
        $dbx = Get-Content -Raw -Path $dbxInfo | ConvertFrom-Json
        Out ""
        Out "Cuentas Dropbox configuradas:"
        foreach ($prop in $dbx.PSObject.Properties) {
            $acc = $prop.Value
            Out ("  {0,-10} path: {1}" -f $prop.Name, $acc.path)
            if ($acc.path) { $dropboxRoots += $acc.path }
        }

        # Tambien probamos el path "real" (un nivel arriba) para business namespaces
        $extraRoots = @()
        foreach ($p in $dropboxRoots) {
            $parent = Split-Path -Parent $p
            if ($parent -match 'Q Dropbox$') { $extraRoots += $parent }
        }
        $dropboxRoots = ($dropboxRoots + $extraRoots) | Select-Object -Unique

        Out ""
        Out "Buscando cola y skybox library en cada root..."
        foreach ($root in $dropboxRoots) {
            if (-not (Test-Path $root)) { Out "  [-] $root  (no existe)"; continue }
            Out ""
            Out "  Root: $root"
            $queue = Join-Path $root '3D Projects\#_RenderServer'
            if (Test-Path $queue) {
                Out ("    [render queue] $queue")
                foreach ($sub in @('pending','processed','failed','logs')) {
                    $s = Join-Path $queue $sub
                    $exists = Test-Path $s
                    $cnt = if ($exists) { (Get-ChildItem -Path $s -File -ErrorAction SilentlyContinue | Measure-Object).Count } else { 0 }
                    Out ("      {0,-10} exists={1}  files={2}" -f $sub, $exists, $cnt)
                }
            }
            $sky = Join-Path $root '3D Materials\Skybox Library\Skybox Library V2'
            if (Test-Path $sky) {
                $cnt = (Get-ChildItem -Path $sky -File -Recurse -ErrorAction SilentlyContinue | Measure-Object).Count
                Out ("    [skybox lib]   $sky  ($cnt files)")
            }
        }
    } catch {
        Out "Error parseando info.json: $_"
    }
}

$dbxProc = Get-Process -Name 'Dropbox' -ErrorAction SilentlyContinue
Line "Dropbox.exe corriendo" ($null -ne $dbxProc) (($dbxProc.Id) -join ',')

# =====================================================================
# 4. RED + WAKE-ON-LAN
# =====================================================================
Header "4. RED Y WAKE-ON-LAN"

try {
    $adapters = Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Up' -or $_.MediaConnectionState -eq 'Connected' }
    foreach ($a in $adapters) {
        Out ""
        Out ("Adapter: {0}" -f $a.Name)
        Out ("  Description:  {0}" -f $a.InterfaceDescription)
        Out ("  Status:       {0}" -f $a.Status)
        Out ("  MAC:          {0}" -f $a.MacAddress)
        Out ("  LinkSpeed:    {0}" -f $a.LinkSpeed)

        # IPs
        $ips = Get-NetIPAddress -InterfaceIndex $a.ifIndex -ErrorAction SilentlyContinue |
               Where-Object { $_.AddressFamily -eq 'IPv4' }
        foreach ($ip in $ips) {
            Out ("  IPv4:         {0}/{1}" -f $ip.IPAddress, $ip.PrefixLength)
        }

        # Capacidades WoL
        try {
            $pwr = Get-NetAdapterPowerManagement -Name $a.Name -ErrorAction Stop
            Out ("  WakeOnMagicPacket:  {0}" -f $pwr.WakeOnMagicPacket)
            Out ("  WakeOnPattern:      {0}" -f $pwr.WakeOnPattern)
            Out ("  AllowComputerToTurnOffDevice: {0}" -f $pwr.AllowComputerToTurnOffDevice)
            Out ("  DeviceSleepOnDisconnect:       {0}" -f $pwr.DeviceSleepOnDisconnect)
        } catch {
            Out "  (Get-NetAdapterPowerManagement no disponible)"
        }

        # Advanced properties (busca claves WOL/Wake/Magic)
        try {
            $adv = Get-NetAdapterAdvancedProperty -Name $a.Name -ErrorAction Stop |
                   Where-Object { $_.DisplayName -match 'Wake|Magic|WOL|Energy|Shutdown' }
            foreach ($p in $adv) {
                Out ("  [adv] {0,-45} = {1}" -f $p.DisplayName, $p.DisplayValue)
            }
        } catch { }
    }
} catch { Out "Error enumerando NICs: $_" }

# powercfg lastwake / wake_armed
Out ""
Out "powercfg /a (estados de power disponibles):"
try { (& powercfg /a) | ForEach-Object { Out "  $_" } } catch { Out "  (no se pudo correr powercfg /a)" }

Out ""
Out "powercfg /lastwake (que desperto a la PC la ultima vez):"
try { (& powercfg /lastwake) | ForEach-Object { Out "  $_" } } catch { }

Out ""
Out "powercfg /devicequery wake_armed (dispositivos armados para despertar):"
try { (& powercfg /devicequery wake_armed) | ForEach-Object { Out "  $_" } } catch { }

Out ""
Out "powercfg /devicequery wake_from_any (dispositivos con capacidad de wake):"
try { (& powercfg /devicequery wake_from_any) | ForEach-Object { Out "  $_" } } catch { }

# =====================================================================
# 5. POWER PLAN + SLEEP/HIBERNATE
# =====================================================================
Header "5. POWER PLAN + SLEEP / HIBERNATE"

try {
    Out "Active scheme:"
    (& powercfg /getactivescheme) | ForEach-Object { Out "  $_" }
} catch { }

# Hibernate file
$hibFile = Join-Path $env:SystemDrive 'hiberfil.sys'
Line "hiberfil.sys" (Test-Path $hibFile) $hibFile

# Modern Standby vs Legacy Sleep
try {
    $msReg = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Power' -Name PlatformAoAcOverride -ErrorAction SilentlyContinue
    if ($msReg -and $msReg.PlatformAoAcOverride -eq 0) {
        Out "Modern Standby: DISABLED via PlatformAoAcOverride=0 (legacy S3 sleep activo - bueno para WoL)"
    }
} catch { }

# =====================================================================
# 6. AUTO-LOGIN (Winlogon registry)
# =====================================================================
Header "6. AUTO-LOGIN"

try {
    $wl = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -ErrorAction Stop
    Line "AutoAdminLogon" ($wl.AutoAdminLogon -eq '1') ("value = '" + $wl.AutoAdminLogon + "'")
    Line "DefaultUserName" ([bool]$wl.DefaultUserName) ($wl.DefaultUserName)
    Line "DefaultDomainName" ([bool]$wl.DefaultDomainName) ($wl.DefaultDomainName)
    Line "DefaultPassword presente" ([bool]$wl.DefaultPassword) ("(no se imprime el valor)")
} catch {
    Out "No se pudo leer Winlogon: $_"
}

# =====================================================================
# 7. STARTUP / RUN
# =====================================================================
Header "7. STARTUP ITEMS"

$runKeys = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce',
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce'
)
foreach ($k in $runKeys) {
    Out ""
    Out "Registry: $k"
    try {
        $props = Get-ItemProperty -Path $k -ErrorAction Stop
        $hasAny = $false
        foreach ($p in $props.PSObject.Properties) {
            if ($p.Name -match '^PS|^PSPath|^PSParentPath|^PSChildName|^PSDrive|^PSProvider') { continue }
            Out ("  {0,-30} = {1}" -f $p.Name, $p.Value)
            $hasAny = $true
        }
        if (-not $hasAny) { Out "  (vacio)" }
    } catch {
        Out "  (clave no presente o sin permiso)"
    }
}

$startupDirs = @(
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup'),
    (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Startup')
)
foreach ($d in $startupDirs) {
    Out ""
    Out "Startup folder: $d"
    if (-not (Test-Path $d)) { Out "  (no existe)"; continue }
    $items = Get-ChildItem -Path $d -File -Force -ErrorAction SilentlyContinue
    if ($items.Count -eq 0) { Out "  (vacio)"; continue }
    foreach ($f in $items) { Out "  $($f.Name)" }
}

# =====================================================================
# 8. TASK SCHEDULER (tareas relevantes)
# =====================================================================
Header "8. TASK SCHEDULER"

try {
    $tasks = Get-ScheduledTask -ErrorAction SilentlyContinue |
             Where-Object {
                $_.TaskName -match 'Revit|Enscape|Render|Dispatch|qbiq|Wake|WoL' -or
                $_.TaskPath -match 'qbiq|Render'
             }
    if ($tasks.Count -eq 0) {
        Out "(no hay tareas que matcheen Revit|Enscape|Render|Dispatch|qbiq|Wake|WoL)"
    } else {
        foreach ($t in $tasks) {
            Out ""
            Out ("Task: {0}{1}" -f $t.TaskPath, $t.TaskName)
            Out ("  State:    {0}" -f $t.State)
            try {
                $info = Get-ScheduledTaskInfo -TaskPath $t.TaskPath -TaskName $t.TaskName -ErrorAction Stop
                Out ("  LastRun:  {0}" -f $info.LastRunTime)
                Out ("  LastResult: 0x{0:X}" -f $info.LastTaskResult)
                Out ("  NextRun:  {0}" -f $info.NextRunTime)
            } catch { }
            foreach ($a in $t.Actions) {
                Out ("  Action:   {0} {1}" -f $a.Execute, $a.Arguments)
            }
            foreach ($tr in $t.Triggers) {
                Out ("  Trigger:  {0}" -f ($tr.GetType().Name))
            }
        }
    }
} catch { Out "No se pudo enumerar Task Scheduler: $_" }

# =====================================================================
# 9. ADD-INS DE REVIT 2024
# =====================================================================
Header "9. ADD-INS DE REVIT 2024"

$addinDirs = @(
    (Join-Path $env:APPDATA   'Autodesk\Revit\Addins\2024'),
    (Join-Path $env:PROGRAMDATA 'Autodesk\Revit\Addins\2024')
)

foreach ($d in $addinDirs) {
    Out ""
    Out "Folder: $d"
    if (-not (Test-Path $d)) { Out "  (no existe)"; continue }
    $items = Get-ChildItem -Path $d -File -Force -ErrorAction SilentlyContinue | Sort-Object Name
    if ($items.Count -eq 0) { Out "  (vacio)"; continue }
    foreach ($f in $items) {
        $size =
            if ($f.Length -lt 1KB) { "$($f.Length) B" }
            elseif ($f.Length -lt 1MB) { "{0:N1} KB" -f ($f.Length/1KB) }
            else { "{0:N1} MB" -f ($f.Length/1MB) }
        Out ("  {0,-40} {1,8}  {2}" -f $f.Name, $size, $f.LastWriteTime.ToString('yyyy-MM-dd HH:mm'))
    }
    foreach ($f in ($items | Where-Object { $_.Extension -eq '.addin' })) {
        Out ""
        Out "----- $($f.Name) -----"
        try {
            $c = Get-Content -Raw -Path $f.FullName -ErrorAction Stop
            if ($c.Length -gt 4000) { $c = $c.Substring(0,4000) + "`n...[TRUNCATED]" }
            Out $c
        } catch { Out "(no se pudo leer: $_)" }
    }
}

# =====================================================================
# 10. .NET SDK / VS (igual que en la laptop, util saber)
# =====================================================================
Header "10. .NET SDK / VS (informativo)"

$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
Line "dotnet CLI" ($null -ne $dotnetCmd) ($dotnetCmd.Source)
if ($dotnetCmd) {
    try { Out ("  --version: " + (& dotnet --version 2>$null)) } catch { }
    try { Out "  SDKs:";     (& dotnet --list-sdks 2>$null)     | ForEach-Object { Out "    $_" } } catch { }
}
$vswhere = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
Line "vswhere.exe" (Test-Path $vswhere) $vswhere

# =====================================================================
# 11. DISPATCH LOG (si existe)
# =====================================================================
Header "11. ULTIMAS LINEAS DEL DISPATCH.LOG"

$logFound = $false
foreach ($root in $dropboxRoots) {
    $log = Join-Path $root '3D Projects\#_RenderServer\logs\dispatch.log'
    if (Test-Path $log) {
        $logFound = $true
        Out "Log: $log"
        try {
            Get-Content -Path $log -Tail 80 -ErrorAction Stop | ForEach-Object { Out "  $_" }
        } catch { Out "(no se pudo leer: $_)" }
        break
    }
}
if (-not $logFound) { Out "(no se encontro dispatch.log)" }

# =====================================================================
# DONE
# =====================================================================
Header "DONE"
Out ""
Out "End of report."

Write-Host ""
Write-Host "================================================================" -ForegroundColor Green
Write-Host " Reporte: $reportFile" -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Green
Write-Host ""
try { Start-Process notepad.exe $reportFile } catch { }
