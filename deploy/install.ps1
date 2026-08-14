<#
.SYNOPSIS
    Install Metamorphosis for a given Revit release by copying files. No admin rights,
    no MSI, no installer to run.

.DESCRIPTION
    A Revit add-in is just a .addin manifest plus a folder of assemblies. Revit hosts its
    own CLR, so there is no runtime to install, and System.Data.SQLite's native interop
    needs no registration - it only has to sit beside the managed assembly, which copying
    already achieves.

    This exists because the MSI takes an admin-only upgrade path that fails on a locked
    down machine. The per-user location below needs no elevation at all.

.PARAMETER RevitVersion
    Revit release to install for, e.g. 2024.

.PARAMETER Source
    Build output directory, e.g. bin\Release2024. Produced by:
        dotnet build src\MetamorphosisCore\MetamorphosisCore.csproj -c Release -p:RevitVersion=2024

.PARAMETER AllUsers
    Install for every user instead of just you. Needs elevation. Note that Revit 2027
    moved the all-user location out of ProgramData into Program Files; this script picks
    the right one for the release. The per-user path is unchanged and still supported.

.EXAMPLE
    .\deploy\install.ps1 -RevitVersion 2024 -Source .\bin\Release2024

.NOTES
    Revit must be CLOSED. It loads add-ins with Assembly.LoadFrom, which holds a file
    lock for the life of the process, so Metamorphosis.dll cannot be replaced while Revit
    is running. The script refuses rather than half-installing.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][int]$RevitVersion,
    [Parameter(Mandatory = $true)][string]$Source,
    [switch]$AllUsers
)

$ErrorActionPreference = 'Stop'

# --- work out where this release looks for add-ins -------------------------------
if ($AllUsers) {
    if ($RevitVersion -ge 2027) {
        # 2027 moved all-user add-ins out of ProgramData for security reasons.
        $root = Join-Path $env:ProgramFiles "Autodesk\Revit\Addins\$RevitVersion"
    } else {
        $root = Join-Path $env:ProgramData "Autodesk\Revit\Addins\$RevitVersion"
    }
} else {
    $root = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
}

$payload  = Join-Path $root 'Metamorphosis'
$manifest = Join-Path $root 'Metamorphosis.addin'

Write-Host "Target : $root"
Write-Host "Source : $Source"

# --- refuse to run against a live Revit ------------------------------------------
$revit = Get-Process -Name 'Revit' -ErrorAction SilentlyContinue
if ($revit) {
    throw ("Revit is running (PID $($revit.Id -join ', ')). It holds a file lock on " +
           "Metamorphosis.dll, so the install would fail halfway. Close Revit and retry.")
}

# --- sanity-check the source ------------------------------------------------------
$dll = Join-Path $Source 'Metamorphosis.dll'
if (-not (Test-Path $dll)) {
    throw "No Metamorphosis.dll in '$Source'. Build first, then point -Source at bin\Release$RevitVersion."
}
$built = (Get-Item $dll).VersionInfo.FileVersion
Write-Host "Version: $built"

# --- back up whatever is there now -------------------------------------------------
# Losing a working install to a failed upgrade is the one outcome worth engineering
# against, so the old one is kept aside rather than overwritten.
if (Test-Path $payload) {
    $stamp  = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backup = "$payload.bak-$stamp"
    Write-Host "Backup : $backup"
    Move-Item -Path $payload -Destination $backup
}

# --- copy ---------------------------------------------------------------------------
New-Item -ItemType Directory -Path $payload -Force | Out-Null
Copy-Item -Path (Join-Path $Source '*') -Destination $payload -Recurse -Force

# The manifest lives beside the folder, not inside it, and points at
# .\Metamorphosis\Metamorphosis.dll relative to itself.
Copy-Item -Path (Join-Path $PSScriptRoot 'Metamorphosis.addin') -Destination $manifest -Force

# --- verify --------------------------------------------------------------------------
$expected = @('Metamorphosis.dll', 'Newtonsoft.Json.dll', 'System.Data.SQLite.dll', 'Settings.xml')
$missing  = $expected | Where-Object { -not (Test-Path (Join-Path $payload $_)) }
if ($missing) {
    throw "Install incomplete - missing: $($missing -join ', ')"
}

# The native SQLite interop is architecture-specific and lives in these subfolders.
# Without them System.Data.SQLite loads but fails at the first query.
foreach ($arch in @('x64', 'x86')) {
    if (-not (Test-Path (Join-Path $payload $arch))) {
        Write-Warning "No $arch\ folder - SQLite's native interop may be missing."
    }
}

Write-Host ''
Write-Host 'Installed. Start Revit and look for the Metamorphosis panel on the Add-Ins tab.'
Write-Host "Manifest: $manifest"
Write-Host "Payload : $payload"
