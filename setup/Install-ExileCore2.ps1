<#
.SYNOPSIS
    Downloads and sets up ExileCore2 with your forked plugins ready to go.
.DESCRIPTION
    Run this AFTER you have downloaded ExileCore2 from the Discord.
    Place the downloaded ExileCore2 zip in the same folder as this script
    OR pass -ZipPath to point to it.

    This script:
      1. Extracts ExileCore2 to C:\ExileCore2 (configurable)
      2. Copies your plugin Source folders into Plugins\Source\
      3. Sets the exileCore2Package environment variable
      4. Creates a desktop shortcut for Loader.exe

.PARAMETER ZipPath
    Path to the ExileCore2 zip you downloaded from Discord.
    If not provided, script will look for *.zip in the current folder.

.PARAMETER InstallPath
    Where to install ExileCore2. Default: C:\ExileCore2

.PARAMETER PluginSourceRoot
    Path to your forked plugin sources (the Plugins\Source folder from this repo).
    Default: auto-detected relative to script location.
#>

param(
    [string]$ZipPath        = "",
    [string]$InstallPath    = "C:\ExileCore2",
    [string]$PluginSourceRoot = ""
)

$ErrorActionPreference = "Stop"

function Write-Step { param($msg) Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok   { param($msg) Write-Host "    [OK] $msg" -ForegroundColor Green }
function Write-Warn { param($msg) Write-Host "    [!!] $msg" -ForegroundColor Yellow }
function Write-Fail { param($msg) Write-Host "    [XX] $msg" -ForegroundColor Red; exit 1 }

# ── Find zip ─────────────────────────────────────────────────────────────────
Write-Step "Locating ExileCore2 zip..."
if (-not $ZipPath) {
    $zips = Get-ChildItem -Path $PSScriptRoot -Filter "*.zip" | Sort-Object LastWriteTime -Descending
    if ($zips.Count -eq 0) {
        Write-Fail "No zip found in $PSScriptRoot. Download ExileCore2 from Discord and place it here, or use -ZipPath."
    }
    $ZipPath = $zips[0].FullName
    Write-Warn "Auto-detected zip: $ZipPath"
}

if (-not (Test-Path $ZipPath)) {
    Write-Fail "Zip not found at: $ZipPath"
}
Write-Ok "Using zip: $ZipPath"

# ── Extract ───────────────────────────────────────────────────────────────────
Write-Step "Extracting ExileCore2 to $InstallPath..."
if (Test-Path $InstallPath) {
    Write-Warn "$InstallPath already exists. Contents will be overwritten."
    $confirm = Read-Host "    Continue? (y/n)"
    if ($confirm -ne 'y') { exit 0 }
}
Expand-Archive -Path $ZipPath -DestinationPath $InstallPath -Force

# Find Loader.exe (might be in a subdirectory inside the zip)
$loader = Get-ChildItem -Path $InstallPath -Recurse -Filter "Loader.exe" | Select-Object -First 1
if (-not $loader) {
    Write-Fail "Loader.exe not found after extraction. Check the zip contents."
}
$exileCoreRoot = $loader.DirectoryName
Write-Ok "ExileCore2 root: $exileCoreRoot"

# ── Set environment variable ───────────────────────────────────────────────────
Write-Step "Setting exileCore2Package environment variable..."
[System.Environment]::SetEnvironmentVariable("exileCore2Package", $exileCoreRoot, "Machine")
$env:exileCore2Package = $exileCoreRoot
Write-Ok "exileCore2Package = $exileCoreRoot (system-wide, permanent)"

# ── Copy plugin sources ───────────────────────────────────────────────────────
Write-Step "Copying plugin Source folders..."

# Auto-detect PluginSourceRoot (look for Plugins\Source relative to this script)
if (-not $PluginSourceRoot) {
    # Script is in setup\, Plugins\Source\ is one level up
    $repoRoot = Split-Path $PSScriptRoot -Parent
    $PluginSourceRoot = Join-Path $repoRoot "Plugins\Source"
}

if (-not (Test-Path $PluginSourceRoot)) {
    Write-Warn "Plugin source folder not found at: $PluginSourceRoot"
    Write-Warn "Skipping plugin copy — copy Plugins\Source\* manually later."
} else {
    $destPluginSource = Join-Path $exileCoreRoot "Plugins\Source"
    if (-not (Test-Path $destPluginSource)) {
        New-Item -ItemType Directory -Path $destPluginSource | Out-Null
    }

    $pluginDirs = Get-ChildItem -Path $PluginSourceRoot -Directory
    foreach ($dir in $pluginDirs) {
        $dest = Join-Path $destPluginSource $dir.Name
        Copy-Item -Path $dir.FullName -Destination $dest -Recurse -Force
        Write-Ok "Copied plugin: $($dir.Name)"
    }
}

# ── Create desktop shortcut ───────────────────────────────────────────────────
Write-Step "Creating desktop shortcut for ExileCore2..."
$shortcutPath = Join-Path ([Environment]::GetFolderPath("Desktop")) "ExileCore2.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath  = $loader.FullName
$shortcut.WorkingDirectory = $exileCoreRoot
$shortcut.Description = "ExileCore2 Overlay"
$shortcut.Save()
Write-Ok "Shortcut created: $shortcutPath"

# ── Verify structure ──────────────────────────────────────────────────────────
Write-Step "Verifying installation..."
$checks = @(
    @{ Path = $loader.FullName;                          Label = "Loader.exe" },
    @{ Path = (Join-Path $exileCoreRoot "ExileCore2.dll"); Label = "ExileCore2.dll" },
    @{ Path = (Join-Path $exileCoreRoot "Plugins");       Label = "Plugins folder" }
)
foreach ($check in $checks) {
    if (Test-Path $check.Path) { Write-Ok $check.Label }
    else                       { Write-Warn "$($check.Label) — NOT FOUND at $($check.Path)" }
}

# ── Final instructions ────────────────────────────────────────────────────────
Write-Host ""
Write-Host "════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  ExileCore2 installed at: $exileCoreRoot" -ForegroundColor Cyan
Write-Host ""
Write-Host "  To launch:" -ForegroundColor White
Write-Host "  1. Start Path of Exile 2" -ForegroundColor White
Write-Host "  2. Double-click the ExileCore2 shortcut on your desktop" -ForegroundColor White
Write-Host "     (or run Loader.exe as Administrator if memory access fails)" -ForegroundColor White
Write-Host ""
Write-Host "  Your plugins are in:" -ForegroundColor White
Write-Host "  $destPluginSource" -ForegroundColor White
Write-Host "  ExileCore2 will compile them automatically on first launch." -ForegroundColor White
Write-Host ""
Write-Host "  If you see compilation errors on first launch:" -ForegroundColor White
Write-Host "  Check the ExileCore2 log window for missing references." -ForegroundColor White
Write-Host "════════════════════════════════════════════════════" -ForegroundColor Cyan
