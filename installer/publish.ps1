<#
    publish.ps1 - builds a self-contained single-file cccxa.exe that bundles the
    .NET runtime and all dependencies. Nothing needs to be installed on the target PC.

    Usage:
        powershell -ExecutionPolicy Bypass -File .\publish.ps1
        powershell -ExecutionPolicy Bypass -File .\publish.ps1 -OutDir C:\build\cccxa
#>
param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\dist')
)

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot '..\cccxa.csproj'

Write-Host "Building self-contained single-file build for win-x64 ..." -ForegroundColor Cyan

$pubArgs = @(
    'publish', $proj,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=none',
    '-p:DebugSymbols=false',
    '-o', $OutDir
)
& dotnet @pubArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

# Ensure appsettings.json is present in the output (needed by install.ps1 -Source).
$srcCfg = Join-Path $PSScriptRoot '..\appsettings.json'
if (Test-Path $srcCfg) { Copy-Item $srcCfg -Destination $OutDir -Force }

Write-Host ""
Write-Host "Done. Output:" -ForegroundColor Green
Get-ChildItem $OutDir | Format-Table Name, Length -AutoSize
Write-Host ("Now install with:  .\install.ps1 -Source " + '"' + $OutDir + '"') -ForegroundColor Yellow
