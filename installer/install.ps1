<#
    install.ps1 - silent installer for cccxa (run as Administrator).

    Installs and configures everything silently:
      - Copies the self-contained build to Program Files.
      - Sets up a central storage folder under ProgramData, one subfolder per user,
        with write permissions for all users.
      - Creates a hidden scheduled task that runs in the background at every user
        logon, in that user's own session context.
      - Adds a dashboard shortcut on the installing admin's desktop.

    Examples:
      # basic silent install
      powershell -ExecutionPolicy Bypass -File .\install.ps1

      # record only specific users
      powershell -ExecutionPolicy Bypass -File .\install.ps1 -OnlyUsers alice,bob

      # never record the work user (sensitive company material)
      powershell -ExecutionPolicy Bypass -File .\install.ps1 -ExcludeUsers work

      # no desktop icon and no Start Menu folder
      powershell -ExecutionPolicy Bypass -File .\install.ps1 -NoDesktopIcon -NoStartMenu

      # configure-only (used by the Inno Setup installer, which copies files itself)
      powershell -ExecutionPolicy Bypass -File .\install.ps1 -ConfigureOnly -InstallDir "C:\Program Files\cccxa"
#>
param(
    [string]   $Source       = (Join-Path $PSScriptRoot '..\dist'),
    [string]   $InstallDir    = (Join-Path $env:ProgramFiles 'cccxa'),
    [string]   $StorageDir    = (Join-Path $env:ProgramData 'cccxa'),
    [string[]] $OnlyUsers      = @(),
    [string[]] $ExcludeUsers   = @(),
    # File-based lists (one username per line) - used by the Inno Setup installer so
    # that Hebrew names and names with spaces survive without command-line quoting issues.
    [string]   $OnlyUsersFile,
    [string]   $ExcludeUsersFile,
    [switch]   $NoDesktopIcon,
    [switch]   $NoStartMenu,
    [switch]   $ConfigureOnly,
    [switch]   $Quiet
)

$ErrorActionPreference = 'Stop'
function Say($m, [string]$c = 'Gray') { if (-not $Quiet) { Write-Host $m -ForegroundColor $c } }

# --- require admin ---
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
         ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { Write-Error "Please run the installer as Administrator."; exit 1 }

# File-based user lists override the array params (one username per line, UTF-8).
function Read-UserFile($path) {
    if ($path -and (Test-Path $path)) {
        return @(Get-Content $path -Encoding UTF8 |
                 ForEach-Object { $_.Trim() } | Where-Object { $_ })
    }
    return @()
}
$fromOnly    = Read-UserFile $OnlyUsersFile
$fromExclude = Read-UserFile $ExcludeUsersFile
if ($fromOnly.Count -gt 0)    { $OnlyUsers    = $fromOnly }
if ($fromExclude.Count -gt 0) { $ExcludeUsers = $fromExclude }

Say "Installing cccxa ..." 'Cyan'

# --- 1. copy files (skipped in ConfigureOnly mode) ---
if (-not $ConfigureOnly) {
    $exe = Join-Path $Source 'cccxa.exe'
    if (-not (Test-Path $exe)) { Write-Error "cccxa.exe not found in '$Source'. Run publish.ps1 first."; exit 1 }
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Copy-Item $exe -Destination $InstallDir -Force
    Get-ChildItem $Source -Filter '*.dll' -ErrorAction SilentlyContinue | Copy-Item -Destination $InstallDir -Force
    $srcCfgFile = Join-Path $Source 'appsettings.json'
    if (Test-Path $srcCfgFile) { Copy-Item $srcCfgFile -Destination $InstallDir -Force }  # defaults (read-only)
}

# --- 2. write the editable appsettings.json under ProgramData (the settings UI writes here) ---
# It lives in ProgramData (not Program Files) so the admin can save from the dashboard without
# elevation, while standard users get read-only and cannot tamper with the monitoring.
New-Item -ItemType Directory -Force -Path $StorageDir | Out-Null
$srcCfg = Join-Path $Source 'appsettings.json'
$insCfg = Join-Path $InstallDir 'appsettings.json'
$dstCfg = Join-Path $StorageDir 'appsettings.json'
if (Test-Path $dstCfg)      { $cfg = Get-Content $dstCfg -Raw -Encoding UTF8 | ConvertFrom-Json }
elseif (Test-Path $srcCfg)  { $cfg = Get-Content $srcCfg -Raw -Encoding UTF8 | ConvertFrom-Json }
elseif (Test-Path $insCfg)  { $cfg = Get-Content $insCfg -Raw -Encoding UTF8 | ConvertFrom-Json }
else                        { $cfg = [pscustomobject]@{ Cccxa = [pscustomobject]@{} } }
if (-not $cfg.Cccxa) { $cfg | Add-Member -NotePropertyName Cccxa -NotePropertyValue ([pscustomobject]@{}) -Force }

# central storage with one subfolder per user (%USERNAME% is expanded at runtime per user)
$storeRoot = Join-Path $StorageDir 'data\%USERNAME%'
$cfg.Cccxa | Add-Member -NotePropertyName StorageRoot  -NotePropertyValue $storeRoot       -Force
$cfg.Cccxa | Add-Member -NotePropertyName OnlyUsers    -NotePropertyValue @($OnlyUsers)    -Force
$cfg.Cccxa | Add-Member -NotePropertyName ExcludeUsers -NotePropertyValue @($ExcludeUsers) -Force

# PowerShell 5.1 serializes an empty array as null; fix that back to [].
$json = $cfg | ConvertTo-Json -Depth 15
$json = $json -replace '"OnlyUsers":\s*null', '"OnlyUsers": []' -replace '"ExcludeUsers":\s*null', '"ExcludeUsers": []'
Set-Content -Path $dstCfg -Value $json -Encoding UTF8

# grant the installing admin Modify on the config file so the dashboard can save it non-elevated.
try {
    $cfgAcl = Get-Acl $dstCfg
    $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $cfgAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($me, 'Modify', 'Allow')))
    $cfgAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('BUILTIN\Administrators', 'Modify', 'Allow')))
    Set-Acl -Path $dstCfg -AclObject $cfgAcl
} catch { }

# --- 3. storage folder + write permission for all users ---
$dataDir = Join-Path $StorageDir 'data'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
$acl  = Get-Acl $dataDir
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    'BUILTIN\Users', 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
$acl.AddAccessRule($rule)
Set-Acl -Path $dataDir -AclObject $acl

# --- 4. hidden scheduled task: background, every user logon, in user context ---
$taskName = 'cccxa'
$action = New-ScheduledTaskAction -Execute (Join-Path $InstallDir 'cccxa.exe') -WorkingDirectory $InstallDir

if ($OnlyUsers.Count -gt 0) {
    $triggers = @()
    foreach ($u in $OnlyUsers) { $triggers += New-ScheduledTaskTrigger -AtLogOn -User $u }
} else {
    $triggers = @(New-ScheduledTaskTrigger -AtLogOn)
}

# runs as the logged-on user (Users group), non-elevated, hidden
$principal = New-ScheduledTaskPrincipal -GroupId 'S-1-5-32-545' -RunLevel Limited
$settings  = New-ScheduledTaskSettingsSet -Hidden -AllowStartIfOnBatteries `
                -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew `
                -ExecutionTimeLimit ([TimeSpan]::Zero) `
                -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $triggers `
    -Principal $principal -Settings $settings -Force | Out-Null

try { Start-ScheduledTask -TaskName $taskName } catch { }

# --- 5. shortcuts (dashboard) ---
function New-Shortcut($path, $target, $arguments, $workdir, $icon) {
    $wsh = New-Object -ComObject WScript.Shell
    $lnk = $wsh.CreateShortcut($path)
    $lnk.TargetPath = $target
    $lnk.Arguments = $arguments
    $lnk.WorkingDirectory = $workdir
    if ($icon) { $lnk.IconLocation = $icon }
    $lnk.Save()
}

$exePath = Join-Path $InstallDir 'cccxa.exe'
$icon    = "$env:SystemRoot\System32\shell32.dll,171"
$lnkName = 'cccxa - Activity Dashboard.lnk'

if (-not $ConfigureOnly) {
    if (-not $NoDesktopIcon) {
        $desktop = [Environment]::GetFolderPath('Desktop')
        New-Shortcut (Join-Path $desktop $lnkName) $exePath 'serve' $InstallDir $icon
        Say "Added dashboard shortcut to the desktop." 'Green'
    }
    if (-not $NoStartMenu) {
        $progs = [Environment]::GetFolderPath('CommonPrograms')
        $folder = Join-Path $progs 'cccxa'
        New-Item -ItemType Directory -Force -Path $folder | Out-Null
        New-Shortcut (Join-Path $folder $lnkName) $exePath 'serve' $InstallDir $icon
    }
}

Say ""
Say "Install complete." 'Green'
Say "  Program files : $InstallDir"
Say "  Data          : $dataDir\<user>"
if ($OnlyUsers.Count -gt 0)    { Say ("  Records only  : " + ($OnlyUsers -join ', ')) 'Yellow' }
if ($ExcludeUsers.Count -gt 0) { Say ("  Excluded      : " + ($ExcludeUsers -join ', ')) 'Yellow' }
Say "  Dashboard     : run the desktop shortcut, or 'cccxa.exe serve' (live + settings)"
