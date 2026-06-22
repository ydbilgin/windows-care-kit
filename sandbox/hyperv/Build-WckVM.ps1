#requires -Version 7.0
<#
.SYNOPSIS
    WCK Uninstall E2E - ONE-TIME Hyper-V VM builder (elevated).

.DESCRIPTION
    Builds a disposable, network-ISOLATED Windows 11 25H2 Enterprise Evaluation VM
    that the per-run harness (Invoke-WckUninstallRun.ps1) drives over PowerShell
    Direct. Run this ONCE, in an ELEVATED (Administrator) PowerShell. After it
    finishes you have a VM named 'WCK-E2E' with a clean 'baseline-clean' checkpoint.

    Steps:
      1. Verify elevation + Hyper-V availability + the eval ISO on disk.
      2. (optional, -AddHyperVAdmin) add the current/Member user to the local
         "Hyper-V Administrators" group so the *per-run* harness can drive Hyper-V
         from a NON-elevated session. NOTE: group membership only takes effect in a
         NEW logon session -> sign out/in afterwards.
      3. Build a tiny FAT32 "answer disk" carrying autounattend.xml.
      4. Create a Gen2 VM (vTPM + Secure Boot so Win11 needs no bypass), NO network
         adapter (forces a clean offline local-account OOBE), attach the eval ISO +
         answer disk, boot.
      5. Wait (polling PowerShell Direct) until the guest reaches the desktop and
         C:\wck-ready.txt exists.
      6. Take the 'baseline-clean' checkpoint and leave the VM at rest.

    HOST SAFETY: every disk/format/VM operation here targets ONLY VM artifacts under
    -BaseDir and a VM literally named -VMName. It never touches the host's real
    programs, registry, or profile. It performs NO reboot.

.NOTES
    The eval ISO must already be at -IsoPath (default F:\WCK-VM\Win11-Ent-Eval-25H2-x64.iso).
#>
[CmdletBinding()]
param(
    [string] $VMName        = 'WCK-E2E',
    [string] $BaseDir       = 'F:\WCK-VM',
    [string] $IsoPath       = 'F:\WCK-VM\Win11-Ent-Eval-25H2-x64.iso',
    [string] $AutounattendPath = (Join-Path $PSScriptRoot 'autounattend.xml'),
    [int]    $MemoryStartupGB = 4,
    [int]    $MemoryMaxGB     = 8,
    [int]    $CpuCount        = 4,
    [int]    $OsDiskGB        = 64,
    [int]    $ReadyTimeoutMin = 45,
    [switch] $AddHyperVAdmin,
    [string] $HyperVAdminMember = $env:USERNAME
)

$ErrorActionPreference = 'Stop'
function Step([string]$m){ Write-Host "==> $m" -ForegroundColor Cyan }
function Info([string]$m){ Write-Host "    $m" -ForegroundColor DarkGray }
function Warn([string]$m){ Write-Host "    $m" -ForegroundColor Yellow }

# --- 1. preflight -----------------------------------------------------------
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Build-WckVM.ps1 must run ELEVATED (Administrator). It creates a VM, formats an answer disk, and provisions a vTPM."
}
if (-not (Get-Command New-VM -ErrorAction SilentlyContinue)) {
    throw "Hyper-V PowerShell module not available. Enable 'Microsoft-Hyper-V-All' and reboot first."
}
if (-not (Test-Path $IsoPath))         { throw "Eval ISO not found: $IsoPath" }
if (-not (Test-Path $AutounattendPath)){ throw "autounattend.xml not found: $AutounattendPath" }
Step "Preflight OK (elevated, Hyper-V present, ISO + answer file found)."

New-Item -ItemType Directory -Force -Path $BaseDir | Out-Null
$OsVhdx    = Join-Path $BaseDir "$VMName-os.vhdx"
$AnswerIso = Join-Path $BaseDir "$VMName-answer.iso"

# --- 2. optional: grant Hyper-V Administrators (for non-elevated per-run) ----
if ($AddHyperVAdmin) {
    Step "Granting '$HyperVAdminMember' Hyper-V Administrators membership (per-run autonomy)..."
    try {
        $grp = (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-578')).Translate([System.Security.Principal.NTAccount]).Value.Split('\')[-1]
        if (-not (Get-LocalGroupMember -Group $grp -ErrorAction SilentlyContinue | Where-Object { $_.Name -match [regex]::Escape($HyperVAdminMember) + '$' })) {
            Add-LocalGroupMember -Group $grp -Member $HyperVAdminMember
            Warn "Added to '$grp'. This takes effect in a NEW logon session -> sign out/in before the per-run harness will work."
        } else {
            Info "'$HyperVAdminMember' is already a member of '$grp'."
        }
    } catch { Warn "Could not modify Hyper-V Administrators group: $($_.Exception.Message)" }
}

# --- 3. tear down any prior VM of this exact name (disposable) ---------------
$existing = Get-VM -Name $VMName -ErrorAction SilentlyContinue
if ($existing) {
    Step "Removing prior VM '$VMName' (disposable rebuild)..."
    if ($existing.State -ne 'Off') { Stop-VM -Name $VMName -TurnOff -Force }
    Remove-VM -Name $VMName -Force
}
if (Test-Path $OsVhdx)    { Remove-Item $OsVhdx -Force }
if (Test-Path $AnswerIso) { Remove-Item $AnswerIso -Force }

# --- 4. build the answer ISO carrying autounattend.xml ----------------------
# The redesigned Win11 (24H2/25H2) setup scans REMOVABLE media roots (DVD/USB) for
# autounattend.xml but NOT secondary FIXED disks — so we ship the answer file on a
# tiny ISO mounted as a 2nd DVD, built via IMAPI2 (no Windows ADK / oscdimg needed).
Step "Building answer ISO ($AnswerIso) with autounattend.xml..."
try {
    Add-Type -TypeDefinition @'
using System; using System.IO; using System.Runtime.InteropServices; using System.Runtime.InteropServices.ComTypes;
public static class WckISOWriter {
  public static void Create(string path, object stream, int blockSize, int totalBlocks) {
    var i = stream as IStream; var o = File.OpenWrite(path); byte[] buf = new byte[blockSize];
    IntPtr cnt = Marshal.AllocHGlobal(4);
    try { while (totalBlocks-- > 0) { i.Read(buf, blockSize, cnt); o.Write(buf, 0, Marshal.ReadInt32(cnt)); } o.Flush(); }
    finally { o.Close(); Marshal.FreeHGlobal(cnt); }
  }
}
'@
} catch { }  # type may already be loaded on a re-run
$isoStage = Join-Path $BaseDir '_answeriso'
Remove-Item $isoStage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $isoStage | Out-Null
Copy-Item $AutounattendPath (Join-Path $isoStage 'autounattend.xml') -Force
$fsi = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
$fsi.FileSystemsToCreate = 3   # ISO9660 + Joliet
$fsi.VolumeName = 'ANSWER'
$fsi.Root.AddTree($isoStage, $false)   # $false = contents at root, not the folder
$img = $fsi.CreateResultImage()
if (Test-Path $AnswerIso) { Remove-Item $AnswerIso -Force }
[WckISOWriter]::Create($AnswerIso, $img.ImageStream, $img.BlockSize, $img.TotalBlocks)
Info "answer ISO built ($([math]::Round((Get-Item $AnswerIso).Length/1KB,1)) KB)."

# --- 5. OS disk + VM ---------------------------------------------------------
Step "Creating $OsDiskGB GB dynamic OS disk + Gen2 VM '$VMName'..."
New-VHD -Path $OsVhdx -SizeBytes ($OsDiskGB * 1GB) -Dynamic | Out-Null
New-VM -Name $VMName -Generation 2 -MemoryStartupBytes ($MemoryStartupGB * 1GB) `
       -VHDPath $OsVhdx -Path $BaseDir | Out-Null

Set-VMMemory  -VMName $VMName -DynamicMemoryEnabled $true -MinimumBytes 2GB -MaximumBytes ($MemoryMaxGB * 1GB)
Set-VMProcessor -VMName $VMName -Count $CpuCount
Set-VM -Name $VMName -CheckpointType Production -AutomaticCheckpointsEnabled $false

# Network-ISOLATED: remove the default adapter so OOBE cannot engage the MSA flow.
Get-VMNetworkAdapter -VMName $VMName | Remove-VMNetworkAdapter
Info "Network adapter removed (offline, isolated guest)."

# Attach eval ISO (DVD) + answer ISO (2nd DVD; removable -> scanned by new setup).
Add-VMDvdDrive -VMName $VMName -Path $IsoPath
Add-VMDvdDrive -VMName $VMName -Path $AnswerIso
Info "Attached eval ISO + answer ISO (both DVD)."

# Secure Boot (Windows template) + vTPM so Win11 requirements are met natively.
$dvd = Get-VMDvdDrive -VMName $VMName | Where-Object { $_.Path -eq $IsoPath }   # the INSTALL DVD, not the answer ISO
Set-VMFirmware -VMName $VMName -EnableSecureBoot On -SecureBootTemplate 'MicrosoftWindows' -FirstBootDevice $dvd

Step "Provisioning vTPM (untrusted local guardian)..."
$guard = Get-HgsGuardian -Name 'WckUntrustedGuardian' -ErrorAction SilentlyContinue
if (-not $guard) { $guard = New-HgsGuardian -Name 'WckUntrustedGuardian' -GenerateCertificates }
$kp = New-HgsKeyProtector -Owner $guard -AllowUntrustedRoot
Set-VMKeyProtector -VMName $VMName -KeyProtector $kp.RawData
Enable-VMTPM -VMName $VMName

# Guest Service Interface (optional Copy-VMFile path; PSDirect copy works without it too).
Enable-VMIntegrationService -VMName $VMName -Name 'Guest Service Interface' -ErrorAction SilentlyContinue

# --- 6. boot + wait for the guest to reach the desktop ----------------------
Step "Starting VM; Windows will install unattended (no human interaction)..."
Start-VM -Name $VMName

# Gen2 boots the install DVD behind a "Press any key to boot from CD/DVD" prompt; with no key it
# falls through to the empty disk and parks at the UEFI boot-summary forever. Inject Enter over the
# VMBus (no GUI) during the FIRST-boot window only.
#
# CRITICAL: the prompt is on screen only briefly (~3-8 s after start, later under heavy host I/O).
# Pumping Enter for a fixed 28 s overran the prompt and drove Windows Setup's Cancel button into a
# "Are you sure you want to quit?" modal that froze the install at ~11% (observed when this VM was
# built concurrently with another). So press Enter at most ~1/s but STOP the instant the OS disk
# starts growing (install began → prompt is long past → any further keystroke can only land in
# Setup's UI and do harm). The 15-press cap is a fallback if the disk never grows (prompt missed →
# parks at UEFI → readiness poll then times out, a detectable failure, not a silent stuck install).
Info "Injecting Enter to pass ONLY the first-boot 'press any key to boot from DVD' prompt..."
$kbVm = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_ComputerSystem -Filter "ElementName='$VMName'"
$kb   = Get-CimAssociatedInstance -InputObject $kbVm -ResultClassName Msvm_Keyboard -Association Msvm_SystemDevice
$osBase = (Get-Item $OsVhdx).Length
for ($k = 0; $k -lt 15; $k++) {
    try { Invoke-CimMethod -InputObject $kb -MethodName TypeKey -Arguments @{ keyCode = [uint32]0x0D } | Out-Null } catch { }
    Start-Sleep -Seconds 1
    if (((Get-Item $OsVhdx).Length - $osBase) -gt 300MB) {
        Info "OS disk is being written (install started) after $($k + 1) Enter press(es); stopping injection."
        break
    }
}

$sec  = ConvertTo-SecureString 'WckE2E!2026' -AsPlainText -Force
$cred = New-Object System.Management.Automation.PSCredential('wck', $sec)
$deadline = (Get-Date).AddMinutes($ReadyTimeoutMin)
$ready = $false
Step "Waiting up to $ReadyTimeoutMin min for PowerShell Direct + C:\wck-ready.txt..."
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 20
    try {
        $probe = Invoke-Command -VMName $VMName -Credential $cred -ErrorAction Stop -ScriptBlock {
            Test-Path 'C:\wck-ready.txt'
        }
        if ($probe) { $ready = $true; break }
        Info "PowerShell Direct up; waiting for first-logon marker..."
    } catch {
        Info "[$((Get-Date).ToString('HH:mm:ss'))] guest not reachable yet (installing/booting)..."
    }
}
if (-not $ready) {
    throw "Guest did not become ready within $ReadyTimeoutMin min. Inspect with: vmconnect.exe localhost $VMName  (or Watch-VMConsole.ps1)."
}
Step "Guest is READY (desktop reached, PowerShell Direct authenticated)."

# --- 7. baseline checkpoint --------------------------------------------------
# Eject install media so the clean baseline has no DVDs attached (per-run restores this).
Get-VMDvdDrive -VMName $VMName | ForEach-Object {
    Set-VMDvdDrive -VMName $VMName -ControllerNumber $_.ControllerNumber -ControllerLocation $_.ControllerLocation -Path $null
}
Get-VMCheckpoint -VMName $VMName -Name 'baseline-clean' -ErrorAction SilentlyContinue | Remove-VMCheckpoint -Confirm:$false
Step "Taking 'baseline-clean' checkpoint..."
Checkpoint-VM -Name $VMName -SnapshotName 'baseline-clean'

Write-Host ''
Write-Host '================================================================' -ForegroundColor Green
Write-Host " VM '$VMName' built and ready." -ForegroundColor Green
Write-Host "   checkpoint : baseline-clean" -ForegroundColor Green
Write-Host "   guest user : wck / (disposable VM-only password)" -ForegroundColor Green
Write-Host '----------------------------------------------------------------' -ForegroundColor Green
Write-Host ' NEXT (autonomous, Hyper-V-Admin session): Invoke-WckUninstallRun.ps1' -ForegroundColor Green
Write-Host '================================================================' -ForegroundColor Green
