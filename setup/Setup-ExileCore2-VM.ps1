#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Optional: Sets up a Hyper-V VM configured to run ExileCore2 + PoE2 (guest).
.DESCRIPTION
    Run this on your HOST machine if you want VM isolation.
    Creates a Generation 2 Hyper-V VM with GPU display and network config.

    If you are running ExileCore2 on the SAME machine as PoE2 (no VM),
    you do NOT need this script — just run Install-Prerequisites.ps1 instead.

.PARAMETER VMName
    Name of the VM to create. Default: "PoE2-ExileCore2"

.PARAMETER VHDSizeGB
    Virtual hard disk size in GB. Default: 100 (enough for Windows + PoE2)

.PARAMETER MemoryGB
    RAM to assign to VM in GB. Default: 8

.EXAMPLE
    .\Setup-ExileCore2-VM.ps1
    .\Setup-ExileCore2-VM.ps1 -VMName "MyPoE2VM" -VHDSizeGB 150 -MemoryGB 12
#>

param(
    [string]$VMName    = "PoE2-ExileCore2",
    [int]$VHDSizeGB    = 100,
    [int]$MemoryGB     = 8,
    [string]$VMPath    = "C:\VMs"
)

$ErrorActionPreference = "Stop"

function Write-Step { param($msg) Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok   { param($msg) Write-Host "    [OK] $msg" -ForegroundColor Green }
function Write-Warn { param($msg) Write-Host "    [!!] $msg" -ForegroundColor Yellow }
function Write-Fail { param($msg) Write-Host "    [XX] $msg" -ForegroundColor Red; exit 1 }

# ── Check Hyper-V is available ────────────────────────────────────────────────
Write-Step "Checking Hyper-V availability..."
$hvFeature = Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All -ErrorAction SilentlyContinue
if (-not $hvFeature -or $hvFeature.State -ne "Enabled") {
    Write-Warn "Hyper-V is not enabled."
    $enableHV = Read-Host "    Enable Hyper-V now? Requires reboot. (y/n)"
    if ($enableHV -eq 'y') {
        Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V -All -NoRestart
        Write-Warn "Hyper-V enabled. Please REBOOT and re-run this script."
        exit 0
    } else {
        Write-Fail "Hyper-V required for VM setup. Exiting."
    }
}
Write-Ok "Hyper-V is enabled."

# ── Check if VM already exists ────────────────────────────────────────────────
Write-Step "Checking for existing VM named '$VMName'..."
$existingVM = Get-VM -Name $VMName -ErrorAction SilentlyContinue
if ($existingVM) {
    Write-Warn "VM '$VMName' already exists. Skipping creation."
} else {

    # ── Create virtual switch (External, bridged to physical NIC) ─────────────
    Write-Step "Setting up virtual network switch..."
    $switch = Get-VMSwitch -Name "ExileCore2-External" -ErrorAction SilentlyContinue
    if (-not $switch) {
        # Find first physical adapter with internet access
        $netAdapter = Get-NetAdapter | Where-Object { $_.Status -eq "Up" -and $_.InterfaceDescription -notlike "*Hyper-V*" } |
                      Select-Object -First 1
        if (-not $netAdapter) {
            Write-Fail "No active physical network adapter found for external switch."
        }
        New-VMSwitch -Name "ExileCore2-External" -NetAdapterName $netAdapter.Name -AllowManagementOS $true | Out-Null
        Write-Ok "External virtual switch created on '$($netAdapter.Name)'."
    } else {
        Write-Ok "Virtual switch 'ExileCore2-External' already exists."
    }

    # ── Create VM directory ───────────────────────────────────────────────────
    if (-not (Test-Path $VMPath)) { New-Item -ItemType Directory -Path $VMPath | Out-Null }
    $vhdPath = Join-Path $VMPath "$VMName\$VMName.vhdx"

    # ── Create VHD ───────────────────────────────────────────────────────────
    Write-Step "Creating virtual hard disk ($VHDSizeGB GB)..."
    $vhdDir = Split-Path $vhdPath
    if (-not (Test-Path $vhdDir)) { New-Item -ItemType Directory -Path $vhdDir | Out-Null }
    New-VHD -Path $vhdPath -SizeBytes ([long]$VHDSizeGB * 1GB) -Dynamic | Out-Null
    Write-Ok "VHD created: $vhdPath"

    # ── Create VM ────────────────────────────────────────────────────────────
    Write-Step "Creating VM '$VMName'..."
    $vm = New-VM -Name $VMName `
                 -Generation 2 `
                 -MemoryStartupBytes ([long]$MemoryGB * 1GB) `
                 -VHDPath $vhdPath `
                 -SwitchName "ExileCore2-External" `
                 -Path $VMPath

    # ── Configure VM ─────────────────────────────────────────────────────────
    Set-VM -Name $VMName `
           -ProcessorCount 4 `
           -DynamicMemory:$false `
           -CheckpointType Disabled  # Checkpoints interfere with game overlays

    # Disable Secure Boot (needed for some GPU pass-through configs and unsigned drivers)
    Set-VMFirmware -VMName $VMName -EnableSecureBoot Off

    # Set resolution hint — ExileCore2 poe_bot expects 1024x768
    Set-VMVideo -VMName $VMName -HorizontalResolution 1024 -VerticalResolution 768

    Write-Ok "VM '$VMName' created with $MemoryGB GB RAM, 4 vCPUs."

    Write-Host ""
    Write-Warn "IMPORTANT: You still need to:"
    Write-Host "  1. Mount a Windows ISO to the VM and install Windows" -ForegroundColor White
    Write-Host "     Get a free Windows 11 ISO from: https://www.microsoft.com/software-download/windows11" -ForegroundColor White
    Write-Host "  2. After Windows is installed in the VM, run Install-Prerequisites.ps1 inside it" -ForegroundColor White
    Write-Host "  3. Install PoE2 in the VM (Steam or standalone)" -ForegroundColor White
    Write-Host "  4. Extract ExileCore2 into the VM" -ForegroundColor White
}

# ── Fix Hyper-V port conflict ────────────────────────────────────────────────
Write-Step "Excluding ExileCore2 ports from Hyper-V dynamic port range..."
netsh int ipv4 add excludedportrange protocol=tcp startport=50005 numberofports=3 2>$null
Write-Ok "Ports 50005-50007 reserved (fixes Hyper-V conflict with ShareData plugin)."

# ── Summary ──────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  VM setup complete." -ForegroundColor Cyan
Write-Host ""
Write-Host "  VM Name:  $VMName" -ForegroundColor White
Write-Host "  RAM:      $MemoryGB GB" -ForegroundColor White
Write-Host "  Disk:     $VHDSizeGB GB (dynamic VHDX)" -ForegroundColor White
Write-Host "  Network:  ExileCore2-External (bridged)" -ForegroundColor White
Write-Host ""
Write-Host "  Host/Guest communication ports:" -ForegroundColor White
Write-Host "    50005 — ExileCore2 HTTP API" -ForegroundColor White
Write-Host "    50006 — ExileCore2 TCP data stream" -ForegroundColor White
Write-Host "    50007 — Guest input command receiver" -ForegroundColor White
Write-Host ""
Write-Host "  Launch VM:  Start-VM -Name '$VMName'" -ForegroundColor White
Write-Host "  Connect:    vmconnect.exe localhost '$VMName'" -ForegroundColor White
Write-Host "════════════════════════════════════════════════════" -ForegroundColor Cyan
