#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs ExileCore2 prerequisites and fixes known Hyper-V port conflicts.
.DESCRIPTION
    Run this once on the machine that will run ExileCore2 + PoE2.
    Must be run as Administrator.
#>

param(
    [switch]$FixHyperVPorts,
    [switch]$SkipDotNet,
    [switch]$SkipVCRedist
)

$ErrorActionPreference = "Stop"

function Write-Step { param($msg) Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok   { param($msg) Write-Host "    [OK] $msg" -ForegroundColor Green }
function Write-Warn { param($msg) Write-Host "    [!!] $msg" -ForegroundColor Yellow }
function Write-Fail { param($msg) Write-Host "    [XX] $msg" -ForegroundColor Red }

# ── 1. Check Windows version ──────────────────────────────────────────────────
Write-Step "Checking Windows version..."
$os = [System.Environment]::OSVersion.Version
if ($os.Major -lt 10) {
    Write-Fail "Windows 10 or newer required. Found: $($os)"
    exit 1
}
Write-Ok "Windows $($os.Major).$($os.Minor) detected."

# ── 2. Check / Install .NET 8 Desktop Runtime ─────────────────────────────────
if (-not $SkipDotNet) {
    Write-Step "Checking .NET 8.0 Desktop Runtime..."

    $net8Installed = Get-ChildItem "HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App" `
                     -ErrorAction SilentlyContinue |
                     Where-Object { $_.PSChildName -like "8.*" }

    if ($net8Installed) {
        Write-Ok ".NET 8 Desktop Runtime already installed: $($net8Installed.PSChildName)"
    } else {
        Write-Warn ".NET 8 Desktop Runtime not found. Downloading..."
        $dotnetUrl = "https://download.visualstudio.microsoft.com/download/pr/windowsdesktop-runtime-8.0-win-x64.exe"
        $installer = "$env:TEMP\dotnet8-desktop-runtime.exe"
        Invoke-WebRequest -Uri $dotnetUrl -OutFile $installer -UseBasicParsing
        Write-Host "    Installing .NET 8 Desktop Runtime (silent)..."
        Start-Process -FilePath $installer -ArgumentList "/install /quiet /norestart" -Wait
        Write-Ok ".NET 8 Desktop Runtime installed."
        Remove-Item $installer -Force
    }
}

# ── 3. Check / Install VC++ 2015 Redistributable ──────────────────────────────
if (-not $SkipVCRedist) {
    Write-Step "Checking VC++ 2015-2022 Redistributable (x64)..."

    $vcInstalled = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64" `
                   -ErrorAction SilentlyContinue

    if ($vcInstalled -and $vcInstalled.Installed -eq 1) {
        Write-Ok "VC++ Redistributable already installed (version $($vcInstalled.Version))."
    } else {
        Write-Warn "VC++ Redistributable not found. Downloading..."
        $vcUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe"
        $vcInstaller = "$env:TEMP\vc_redist.x64.exe"
        Invoke-WebRequest -Uri $vcUrl -OutFile $vcInstaller -UseBasicParsing
        Write-Host "    Installing VC++ Redistributable (silent)..."
        Start-Process -FilePath $vcInstaller -ArgumentList "/install /quiet /norestart" -Wait
        Write-Ok "VC++ Redistributable installed."
        Remove-Item $vcInstaller -Force
    }
}

# ── 4. Check DirectX 11 ───────────────────────────────────────────────────────
Write-Step "Checking DirectX 11..."
$dxDiag = "$env:SystemRoot\System32\d3d11.dll"
if (Test-Path $dxDiag) {
    Write-Ok "DirectX 11 (d3d11.dll) present."
} else {
    Write-Warn "d3d11.dll not found — run Windows Update and ensure your GPU drivers are installed."
}

# ── 5. Fix Hyper-V port reservation conflict ──────────────────────────────────
Write-Step "Checking Hyper-V port reservations for ports 50006 and 50007..."

$reservations = netsh int ipv4 show excludedportrange protocol=tcp
$port50006Reserved = $reservations | Select-String "5000[67]"

if ($port50006Reserved -or $FixHyperVPorts) {
    if ($port50006Reserved) {
        Write-Warn "Hyper-V has reserved ports in the 50006-50007 range. Fixing..."
    } else {
        Write-Host "    Adding port exclusions proactively (recommended if Hyper-V is enabled)..."
    }

    # Exclude 50006 and 50007 from Hyper-V's dynamic port range
    netsh int ipv4 add excludedportrange protocol=tcp startport=50005 numberofports=3 | Out-Null
    Write-Ok "Ports 50005-50007 excluded from Hyper-V dynamic range."
    Write-Warn "A reboot may be required for this change to take effect."
} else {
    Write-Ok "No Hyper-V port conflicts detected on 50006/50007."
}

# ── 6. Windows Firewall — prompt to add exceptions ────────────────────────────
Write-Step "Firewall — checking inbound rules for ExileCore2 ports..."

$rule50006 = Get-NetFirewallRule -DisplayName "ExileCore2-ShareData" -ErrorAction SilentlyContinue

if (-not $rule50006) {
    Write-Warn "No firewall rule found for ports 50005-50007."
    $addRule = Read-Host "    Add inbound allow rules for ExileCore2 ports 50005-50007? (y/n)"
    if ($addRule -eq 'y') {
        New-NetFirewallRule -DisplayName "ExileCore2-ShareData" `
            -Direction Inbound -Protocol TCP `
            -LocalPort 50005,50006,50007 `
            -Action Allow -Profile Any | Out-Null
        Write-Ok "Firewall rules added for ports 50005-50007."
    }
} else {
    Write-Ok "Firewall rule 'ExileCore2-ShareData' already exists."
}

# ── 7. Summary ────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  Prerequisites check complete." -ForegroundColor Cyan
Write-Host ""
Write-Host "  Next steps:" -ForegroundColor White
Write-Host "  1. Join Discord: https://discord.gg/eMmeSyqEA3" -ForegroundColor White
Write-Host "  2. Download the current ExileCore2 build from #downloads" -ForegroundColor White
Write-Host "  3. Extract ExileCore2 to a folder (e.g. C:\ExileCore2)" -ForegroundColor White
Write-Host "  4. Run Loader.exe as Administrator" -ForegroundColor White
Write-Host "  5. Drop plugin Source folders into Plugins\Source\" -ForegroundColor White
Write-Host "════════════════════════════════════════════════════" -ForegroundColor Cyan
