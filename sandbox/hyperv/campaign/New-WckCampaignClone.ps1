#requires -Version 7.0
<#
.SYNOPSIS
    Creates a guarded VM2 campaign clone from baseline-campaign.

.DESCRIPTION
    Every force operation and every campaign-root path is guarded by the shared C-16
    disposable VM/path assertions. The target VM is marked with WCK-CAMPAIGN:<guid>,
    has no NIC attached, and receives a baseline-campaign checkpoint.
#>
[CmdletBinding()]
param(
    [string] $SourceVM = 'baseline-campaign',
    [string] $VM2,
    [string] $CampaignRoot = 'F:\WCK-VM\campaign',
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\Guard-WckDisposable.ps1"

function Assert-WckCampaignCloneTarget {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $VMName,
        [Parameter(Mandatory)] [string] $TargetDir,
        [Parameter(Mandatory)] [string] $CampaignRoot
    )

    $null = Assert-WckPathUnderRoot -Path $TargetDir -Root $CampaignRoot
    if (Test-Path -LiteralPath $TargetDir) {
        $existing = Get-VM -Name $VMName -ErrorAction SilentlyContinue
        $marker = if ($existing) { Get-WckCampaignMarker -Notes ([string]$existing.Notes) } else { $null }
        if (-not $marker) {
            throw "target '$TargetDir' already exists but VM '$VMName' has no WCK-CAMPAIGN marker; refusing overwrite"
        }
    }
}

if ($MyInvocation.InvocationName -eq '.') { return }

if (-not (Get-Command Get-VM -ErrorAction SilentlyContinue)) {
    throw "Hyper-V module unavailable."
}

$source = Get-VM -Name $SourceVM -ErrorAction Stop
$null = Assert-WckDisposableVM -VMName $SourceVM -VMObject $source

if ([string]::IsNullOrWhiteSpace($VM2)) { throw "-VM2 is required" }
$targetDir = Join-Path $CampaignRoot $VM2
$null = Assert-WckPathUnderRoot -Path $targetDir -Root $CampaignRoot
Assert-WckCampaignCloneTarget -VMName $VM2 -TargetDir $targetDir -CampaignRoot $CampaignRoot

if ((Get-VM -Name $VM2 -ErrorAction SilentlyContinue) -and $Force) {
    $null = Assert-WckDisposableVM -VMName $VM2
    Stop-VM -Name $VM2 -TurnOff -Force -ErrorAction SilentlyContinue
    Remove-VM -Name $VM2 -Force
}
elseif (Get-VM -Name $VM2 -ErrorAction SilentlyContinue) {
    throw "target VM '$VM2' already exists; pass -Force only for a marker-bearing campaign VM"
}

if ((Test-Path -LiteralPath $targetDir) -and $Force) {
    $null = Assert-WckPathUnderRoot -Path $targetDir -Root $CampaignRoot
    Remove-Item -LiteralPath $targetDir -Recurse -Force
}
elseif (Test-Path -LiteralPath $targetDir) {
    throw "target path exists: $targetDir"
}

New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
$exportDir = Join-Path $targetDir 'export'
$null = Assert-WckPathUnderRoot -Path $exportDir -Root $CampaignRoot
New-Item -ItemType Directory -Force -Path $exportDir | Out-Null

Export-VM -Name $SourceVM -Path $exportDir
$imported = Import-VM -Path (Get-ChildItem -LiteralPath $exportDir -Recurse -Filter '*.vmcx' | Select-Object -First 1).FullName `
    -Copy -GenerateNewId -VirtualMachinePath $targetDir -VhdDestinationPath (Join-Path $targetDir 'Virtual Hard Disks') -SnapshotFilePath (Join-Path $targetDir 'Snapshots')
Rename-VM -VM $imported -NewName $VM2

Get-VMNetworkAdapter -VMName $VM2 -ErrorAction SilentlyContinue | Remove-VMNetworkAdapter

$guid = [guid]::NewGuid().ToString()
Set-VM -Name $VM2 -Notes "WCK-CAMPAIGN:$guid"
$null = Assert-WckDisposableVM -VMName $VM2

Checkpoint-VM -Name $VM2 -SnapshotName 'baseline-campaign'

[pscustomobject]@{
    VMName = $VM2
    SourceVM = $SourceVM
    CampaignGuid = $guid
    Path = $targetDir
    Checkpoint = 'baseline-campaign'
}
