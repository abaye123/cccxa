<#
    uninstall.ps1 - full removal of cccxa (run as Administrator).

    By default the collected data is kept. To remove it too:
        powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -RemoveData
#>
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'cccxa'),
    [string] $StorageDir = (Join-Path $env:ProgramData 'cccxa'),
    [switch] $RemoveData,
    [switch] $Quiet
)

$ErrorActionPreference = 'SilentlyContinue'
function Say($m, [string]$c = 'Gray') { if (-not $Quiet) { Write-Host $m -ForegroundColor $c } }

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
         ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { Write-Error "Please run as Administrator."; exit 1 }

Say "Removing cccxa ..." 'Cyan'

try { Stop-ScheduledTask -TaskName 'cccxa' } catch { }
try { Unregister-ScheduledTask -TaskName 'cccxa' -Confirm:$false } catch { }
try { Get-Process -Name 'cccxa' -ErrorAction SilentlyContinue | Stop-Process -Force } catch { }
Start-Sleep -Milliseconds 500

$lnkName = 'cccxa - Activity Dashboard.lnk'
Remove-Item (Join-Path ([Environment]::GetFolderPath('Desktop')) $lnkName) -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'cccxa') -Recurse -Force -ErrorAction SilentlyContinue

Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue

if ($RemoveData) {
    Remove-Item $StorageDir -Recurse -Force -ErrorAction SilentlyContinue
    Say "Data removed." 'Yellow'
} else {
    Say "Data kept under: $StorageDir" 'Yellow'
}

Say "Uninstall complete." 'Green'
