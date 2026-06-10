#Requires -Version 5.1
<#
.SYNOPSIS
    Flask Manager installer for Exile-UI (AutoHotkey).
    Copies the module and patches Exile UI.ahk with two lines.
#>

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'

function Write-Step  { param($msg) Write-Host "  $msg" -ForegroundColor Cyan }
function Write-OK    { param($msg) Write-Host "  [OK]  $msg" -ForegroundColor Green }
function Write-Skip  { param($msg) Write-Host "  [--]  $msg" -ForegroundColor DarkGray }
function Write-Fail  { param($msg) Write-Host "  [!!]  $msg" -ForegroundColor Red }

Clear-Host
Write-Host ""
Write-Host "  Flask Manager  -  Exile-UI Installer" -ForegroundColor White
Write-Host "  ======================================" -ForegroundColor DarkGray
Write-Host ""

# ── Step 1: locate Exile-UI ──────────────────────────────────────────────────
Write-Step "Locating Exile-UI installation folder..."

# Try to auto-detect common locations first
$candidates = @(
    "$env:USERPROFILE\Desktop\Exile UI",
    "$env:USERPROFILE\Downloads\Exile UI",
    "$env:USERPROFILE\Documents\Exile UI",
    "C:\Exile UI",
    "C:\Games\Exile UI"
)

$autoFound = $candidates | Where-Object { Test-Path (Join-Path $_ "Exile UI.ahk") } | Select-Object -First 1

if ($autoFound) {
    Write-Host ""
    Write-Host "  Found: $autoFound" -ForegroundColor Yellow
    $choice = Read-Host "  Use this folder? [Y/n]"
    if ($choice -match '^[Nn]') { $autoFound = $null }
}

if (-not $autoFound) {
    Write-Host ""
    Write-Host "  Opening folder picker..." -ForegroundColor DarkGray
    $dialog                     = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description         = "Select your Exile-UI installation folder (the one containing 'Exile UI.ahk')"
    $dialog.ShowNewFolderButton = $false
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
        Write-Fail "Cancelled."
        Read-Host "`n  Press Enter to exit"
        exit 0
    }
    $exileDir = $dialog.SelectedPath
} else {
    $exileDir = $autoFound
}

# ── Step 2: validate ─────────────────────────────────────────────────────────
Write-Step "Validating Exile-UI directory..."

$mainScript = Join-Path $exileDir "Exile UI.ahk"
$modulesDir = Join-Path $exileDir "modules"

if (-not (Test-Path $mainScript)) {
    Write-Fail "'Exile UI.ahk' not found in: $exileDir"
    Write-Fail "Please run the installer again and select the correct folder."
    Read-Host "`n  Press Enter to exit"
    exit 1
}
if (-not (Test-Path $modulesDir)) {
    Write-Fail "'modules' folder not found in: $exileDir"
    Read-Host "`n  Press Enter to exit"
    exit 1
}

Write-OK "Exile-UI found at: $exileDir"

# ── Step 3: backup Exile UI.ahk ─────────────────────────────────────────────
Write-Step "Backing up Exile UI.ahk..."

$timestamp  = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupPath = "$mainScript.$timestamp.bak"
Copy-Item $mainScript $backupPath -Force
Write-OK "Backup saved: $(Split-Path $backupPath -Leaf)"

# ── Step 4: copy the module ──────────────────────────────────────────────────
Write-Step "Copying flask-manager.ahk to modules/..."

$moduleSrc  = Join-Path $PSScriptRoot "modules\flask-manager.ahk"
$moduleDest = Join-Path $modulesDir   "flask-manager.ahk"

if (-not (Test-Path $moduleSrc)) {
    Write-Fail "Cannot find modules\flask-manager.ahk next to this installer."
    Write-Fail "Make sure install.ps1 and the modules\ folder are in the same directory."
    Read-Host "`n  Press Enter to exit"
    exit 1
}

Copy-Item $moduleSrc $moduleDest -Force
Write-OK "Copied flask-manager.ahk to $modulesDir"

# ── Step 5: patch Exile UI.ahk ──────────────────────────────────────────────
Write-Step "Patching Exile UI.ahk..."

$content = [System.IO.File]::ReadAllText($mainScript, [System.Text.Encoding]::UTF8)

# Detect line ending style
$le = if ($content -match "\r\n") { "`r`n" } else { "`n" }

# --- 5a: #Include ---
$includeAnchor  = '#Include modules\statlas.ahk'
$includeNew     = '#Include modules\flask-manager.ahk'
$includePattern = [regex]::Escape($includeNew)

if ($content -match $includePattern) {
    Write-Skip "#Include already present – skipping"
} elseif ($content -match [regex]::Escape($includeAnchor)) {
    $content = $content.Replace($includeAnchor, $includeAnchor + $le + $includeNew)
    Write-OK  "Added: $includeNew"
} else {
    # Fallback: append after the last #Include modules\ line found
    $lastMatch = [regex]::Match($content, '(?m)^#Include modules\\[^\r\n]+')
    $allMatches = [regex]::Matches($content, '(?m)^#Include modules\\[^\r\n]+')
    if ($allMatches.Count -gt 0) {
        $last = $allMatches[$allMatches.Count - 1]
        $insertAt = $last.Index + $last.Length
        $content = $content.Insert($insertAt, $le + $includeNew)
        Write-OK  "Added: $includeNew  (appended after last #Include)"
    } else {
        Write-Fail "Could not locate a #Include anchor in Exile UI.ahk"
        Write-Fail "Add this line manually after the other #Include modules\ lines:"
        Write-Fail "  $includeNew"
    }
}

# --- 5b: Init call ---
$initNew     = 'Init_flaskmanager(), LLK_Log("initialized flask manager")'
$initPattern = [regex]::Escape('Init_flaskmanager()')

if ($content -match $initPattern) {
    Write-Skip "Init_flaskmanager() already present – skipping"
} else {
    # Insert after the Init_screenchecks() line
    $screenchecksPattern = '(?m)^(Init_screenchecks\(\)[^\r\n]*)'
    if ($content -match $screenchecksPattern) {
        $content = [regex]::Replace($content, $screenchecksPattern, '$1' + $le + $initNew)
        Write-OK "Added: $initNew"
    } else {
        # Fallback: insert before Init_hotkeys()
        $hotkeysPattern = '(?m)^(Init_hotkeys\(\)[^\r\n]*)'
        if ($content -match $hotkeysPattern) {
            $content = [regex]::Replace($content, $hotkeysPattern, $initNew + $le + '$1')
            Write-OK "Added: $initNew  (inserted before Init_hotkeys)"
        } else {
            Write-Fail "Could not locate Init_screenchecks() in Exile UI.ahk"
            Write-Fail "Add this line manually in the init sequence:"
            Write-Fail "  $initNew"
        }
    }
}

# ── Step 6: write patched file ───────────────────────────────────────────────
[System.IO.File]::WriteAllText($mainScript, $content, [System.Text.Encoding]::UTF8)
Write-OK "Exile UI.ahk saved"

# ── Done ─────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "  Installation complete!" -ForegroundColor Green
Write-Host ""
Write-Host "  HOW TO USE:" -ForegroundColor White
Write-Host "  1. Run Exile-UI as normal (double-click 'Exile UI.ahk')" -ForegroundColor DarkGray
Write-Host "  2. Right-click the AHK tray icon > 'Flask Manager Settings...'" -ForegroundColor DarkGray
Write-Host "     or press Ctrl+Shift+Alt+F" -ForegroundColor DarkGray
Write-Host "  3. Enable the feature and calibrate your life/mana orb thresholds" -ForegroundColor DarkGray
Write-Host ""
Write-Host "  If anything went wrong, restore from backup:" -ForegroundColor DarkGray
Write-Host "  $backupPath" -ForegroundColor DarkGray
Write-Host ""

Read-Host "  Press Enter to exit"
