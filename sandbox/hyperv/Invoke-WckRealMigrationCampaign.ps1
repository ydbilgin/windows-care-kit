#requires -Version 7.0
<#
.SYNOPSIS
    VM-native real-profile migration restore campaign orchestrator.

.DESCRIPTION
    Publishes the MigrationRealRestore harness, drives the source and destination disposable
    Hyper-V guests, pulls evidence, and resets both VMs in finally. This script is the owner-gated
    VM campaign entry point; it must not be run as part of host-safe verification.
#>
[CmdletBinding()]
param(
    [string] $SourceVM   = 'WCK-E2E',
    [string] $DestVM     = 'WCK-E2E-2',
    [string] $Checkpoint = 'baseline-campaign',
    [string] $InstallersDir = 'F:\WCK-VM\installers',
    [string] $HarnessDir = 'F:\WCK-VM\harness\realmig',
    [string] $OutputBase = 'F:\WCK-VM\realmig-output',
    [int]    $ReadyTimeoutMin = 10,
    [int]    $GuestStartupMB = 2048,
    [switch] $Publish,
    [switch] $CreateSourceCheckpoint,
    [switch] $KeepDirtyState
)

$ErrorActionPreference = 'Stop'
function Step([string]$m){ Write-Host "==> $m" -ForegroundColor Cyan }
function Info([string]$m){ Write-Host "    $m" -ForegroundColor DarkGray }

. (Join-Path $PSScriptRoot 'campaign\Guard-WckDisposable.ps1')

if (-not (Get-Command Get-VM -ErrorAction SilentlyContinue)) { throw "Hyper-V module unavailable." }

$sourceReady = Assert-WckCampaignReady -VMName $SourceVM -Checkpoint $Checkpoint
$destReady = Assert-WckCampaignReady -VMName $DestVM -Checkpoint $Checkpoint
Info "source campaign guid: $($sourceReady.CampaignGuid)"
Info "dest campaign guid: $($destReady.CampaignGuid)"

$requiredInstallers = @('git.exe', 'npp.exe', 'vscode.exe')
foreach ($name in $requiredInstallers) {
    $path = Join-Path $InstallersDir $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required installer missing: $path"
    }
}
$chromeMissing = -not (Test-Path -LiteralPath (Join-Path $InstallersDir 'chrome.msi') -PathType Leaf)
if ($chromeMissing) { Info "chrome.msi missing; source guest will use Chrome seed fallback." }

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
$exeName = 'MigrationRealRestore.exe'
if ($Publish -or -not (Test-Path (Join-Path $HarnessDir $exeName))) {
    Step "Publishing $exeName self-contained win-x64 -> $HarnessDir"
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw "Per-user .NET 10 SDK not found at $dotnet." }
    $csproj = Join-Path $repoRoot 'tools\MigrationRealRestore\MigrationRealRestore.csproj'
    & $dotnet publish $csproj -c Release --output $HarnessDir --runtime win-x64 --self-contained true
    if ($LASTEXITCODE -ne 0) { throw "$exeName publish failed ($LASTEXITCODE)." }
}
if (-not (Test-Path -LiteralPath (Join-Path $HarnessDir $exeName) -PathType Leaf)) {
    throw "Harness exe missing after publish: $HarnessDir\$exeName"
}

$out = Join-Path $OutputBase 'campaign'
if (Test-Path -LiteralPath $out) {
    $isDefault = ($OutputBase -eq 'F:\WCK-VM\realmig-output')
    $isEmpty = -not (Get-ChildItem -LiteralPath $out -ErrorAction SilentlyContinue | Select-Object -First 1)
    if (-not ($isDefault -or $isEmpty)) { throw "Output dir '$out' is non-empty and not the default; refuse to clear." }
    Remove-Item -LiteralPath $out -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $out | Out-Null

# Throwaway autologon credential for the disposable WCK campaign guests. This is the only permitted
# VM credential in the repo, and both VMs are reverted after every run.
$sec = ConvertTo-SecureString 'WckE2E!2026' -AsPlainText -Force
$cred = [System.Management.Automation.PSCredential]::new('wck', $sec)

function Restore-BaselineGuarded([string]$VMName) {
    Assert-WckDisposableVM -VMName $VMName | Out-Null
    if ((Get-VM -Name $VMName).State -ne 'Off') { Stop-VM -Name $VMName -TurnOff -Force }
    Restore-VMCheckpoint -VMName $VMName -Name $Checkpoint -Confirm:$false
}

function Wait-GuestReady([string]$VMName) {
    # Fit startup RAM to host headroom. A 4096 MB startup fails to allocate under host memory
    # pressure (0x800705AA). With dynamic memory (min 2048) the guest boots small and balloons
    # up only if the host has free RAM, so this never destabilizes the host. Applied AFTER the
    # checkpoint restore (which reverts VM config), while the VM is Off.
    try {
        $vm = Get-VM -Name $VMName
        if ($vm.DynamicMemoryEnabled) {
            $minMB = [int]($vm.MemoryMinimum / 1MB)
            $target = [Math]::Max($minMB, $GuestStartupMB)
            if ([int]($vm.MemoryStartup / 1MB) -ne $target) {
                Set-VM -Name $VMName -MemoryStartupBytes ($target * 1MB)
                Info "$VMName startup RAM set to ${target} MB (host headroom fit)"
            }
        }
    } catch { Info "could not adjust startup RAM for ${VMName}: $($_.Exception.Message)" }
    Step "Starting $VMName; waiting for PowerShell Direct..."
    Start-VM -Name $VMName
    $deadline = (Get-Date).AddMinutes($ReadyTimeoutMin)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 10
        try {
            if (Invoke-Command -VMName $VMName -Credential $cred -ScriptBlock { Test-Path 'C:\wck-ready.txt' } -ErrorAction Stop) {
                Step "$VMName guest READY."
                return
            }
        } catch {
            Info "$VMName not reachable yet..."
        }
    }
    throw "$VMName guest not ready within $ReadyTimeoutMin min."
}

function Set-GuestNonce([System.Management.Automation.Runspaces.PSSession]$Session, [string]$Guid) {
    Invoke-Command -Session $Session -ArgumentList $Guid -ScriptBlock {
        param([string]$g)
        New-Item -ItemType Directory -Force -Path 'C:\WCK-Campaign' | Out-Null
        "$g|$env:COMPUTERNAME" | Set-Content -LiteralPath 'C:\WCK-Campaign\guest-nonce.txt' -Encoding ascii
    }
}

function Assert-EvidencePass([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing evidence: $Path" }
    if ((Get-Item -LiteralPath $Path).Length -le 0) { throw "Empty evidence: $Path" }
    $json = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($json.pass -ne $true) { throw "Evidence pass flag is not true: $Path" }
}

$sourceExit = $null
$destExit = $null

try {
    $sourceOut = Join-Path $out 'source'
    $destOut = Join-Path $out 'dest'
    New-Item -ItemType Directory -Force -Path $sourceOut, $destOut | Out-Null

    Step "Restoring source VM '$SourceVM' to '$Checkpoint'"
    Restore-BaselineGuarded $SourceVM
    Wait-GuestReady $SourceVM

    $sourceSession = New-PSSession -VMName $SourceVM -Credential $cred
    try {
        $sourceGuid = Assert-WckDisposableVM -VMName $SourceVM
        Set-GuestNonce -Session $sourceSession -Guid $sourceGuid
        Step "Pushing source payload into $SourceVM"
        Invoke-Command -Session $sourceSession -ScriptBlock {
            Remove-Item 'C:\WCK-Input','C:\WCK-Output','C:\WCK-RealMig' -Recurse -Force -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Force -Path 'C:\WCK-Input\harness','C:\WCK-Input\installers','C:\WCK-Output' | Out-Null
        }
        Copy-Item -Path (Join-Path $HarnessDir '*') -Destination 'C:\WCK-Input\harness' -ToSession $sourceSession -Recurse -Force
        Copy-Item -Path (Join-Path $InstallersDir '*') -Destination 'C:\WCK-Input\installers' -ToSession $sourceSession -Recurse -Force
        Copy-Item -Path (Join-Path $PSScriptRoot 'realmig-source-guest-run.ps1') -Destination 'C:\WCK-Input\realmig-source-guest-run.ps1' -ToSession $sourceSession -Force

        Step "Running source P0/P1 script"
        $sourceResult = Invoke-Command -Session $sourceSession -ArgumentList $sourceGuid, $chromeMissing -ScriptBlock {
            param([string]$Nonce, [bool]$ChromeSeedFallback)
            Set-ExecutionPolicy -Scope Process Bypass -Force -ErrorAction SilentlyContinue
            & 'C:\WCK-Input\realmig-source-guest-run.ps1' -InputDir 'C:\WCK-Input' -Output 'C:\WCK-Output' -Nonce $Nonce -ChromeSeedFallback:$ChromeSeedFallback
        }
        $sourceExit = [int]$sourceResult.ExitCode
        Info "source exit: $sourceExit"

        Step "Pulling source evidence"
        Copy-Item -Path 'C:\WCK-Output\*' -Destination $sourceOut -FromSession $sourceSession -Recurse -Force

        if ($sourceExit -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $sourceOut 'realmig-backup-evidence.json'))) {
            throw "source script reported exit 0 but no backup evidence was pulled."
        }
        if ($sourceExit -eq 0 -and $CreateSourceCheckpoint) {
            Step "Creating optional source-seeded checkpoint"
            Checkpoint-VM -Name $SourceVM -SnapshotName 'source-seeded'
        }
    } finally {
        Remove-PSSession $sourceSession -ErrorAction SilentlyContinue
    }

    if (-not $KeepDirtyState) {
        Step "Resetting source VM after source phase"
        Restore-BaselineGuarded $SourceVM
    }
    if ($sourceExit -ne 0) { throw "source guest script failed with exit $sourceExit" }

    Step "Restoring destination VM '$DestVM' to '$Checkpoint'"
    Restore-BaselineGuarded $DestVM
    Wait-GuestReady $DestVM

    $destSession = New-PSSession -VMName $DestVM -Credential $cred
    try {
        $destGuid = Assert-WckDisposableVM -VMName $DestVM
        Set-GuestNonce -Session $destSession -Guid $destGuid
        Step "Pushing destination payload into $DestVM"
        Invoke-Command -Session $destSession -ScriptBlock {
            Remove-Item 'C:\WCK-Input','C:\WCK-Output','C:\WCK-RealMig' -Recurse -Force -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Force -Path 'C:\WCK-Input\harness','C:\WCK-Input\installers','C:\WCK-Output' | Out-Null
        }
        Copy-Item -Path (Join-Path $HarnessDir '*') -Destination 'C:\WCK-Input\harness' -ToSession $destSession -Recurse -Force
        Copy-Item -Path (Join-Path $InstallersDir '*') -Destination 'C:\WCK-Input\installers' -ToSession $destSession -Recurse -Force
        Copy-Item -Path (Join-Path $sourceOut 'package') -Destination 'C:\WCK-Input' -ToSession $destSession -Recurse -Force
        Copy-Item -Path (Join-Path $PSScriptRoot 'realmig-dest-guest-run.ps1') -Destination 'C:\WCK-Input\realmig-dest-guest-run.ps1' -ToSession $destSession -Force

        Step "Running destination P2/P3/P4/P5 script"
        $destResult = Invoke-Command -Session $destSession -ArgumentList $destGuid -ScriptBlock {
            param([string]$Nonce)
            Set-ExecutionPolicy -Scope Process Bypass -Force -ErrorAction SilentlyContinue
            & 'C:\WCK-Input\realmig-dest-guest-run.ps1' -InputDir 'C:\WCK-Input' -Output 'C:\WCK-Output' -Nonce $Nonce -ExpectGitEmail 'wck-source@wck-e2e.invalid'
        }
        $destExit = [int]$destResult.ExitCode
        Info "dest exit: $destExit"

        Step "Pulling destination evidence"
        Copy-Item -Path 'C:\WCK-Output\*' -Destination $destOut -FromSession $destSession -Recurse -Force
        if ($destExit -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $destOut 'realmig-verify-evidence.json'))) {
            throw "dest script reported exit 0 but no verify evidence was pulled."
        }
    } finally {
        Remove-PSSession $destSession -ErrorAction SilentlyContinue
    }

    if ($destExit -ne 0) { throw "destination guest script failed with exit $destExit" }

    Assert-EvidencePass (Join-Path $sourceOut 'sourceSeed.json')
    Assert-EvidencePass (Join-Path $sourceOut 'realmig-backup-evidence.json')
    Assert-EvidencePass (Join-Path $destOut 'realmig-fingerprint-evidence.json')
    Assert-EvidencePass (Join-Path $destOut 'realmig-restore-evidence.json')
    Assert-EvidencePass (Join-Path $destOut 'realmig-verify-evidence.json')
    Assert-EvidencePass (Join-Path $destOut 'realmig-undo-evidence.json')

    Write-Host ''
    Write-Host '================================================================' -ForegroundColor Green
    Write-Host ' WCK VM-native real-profile migration campaign: PASS' -ForegroundColor Green
    Write-Host "   evidence : $out" -ForegroundColor Green
    Write-Host '================================================================' -ForegroundColor Green
}
finally {
    if (-not $KeepDirtyState) {
        Step "Resetting both VMs to clean baseline"
        foreach ($vm in @($SourceVM, $DestVM)) {
            try { Restore-BaselineGuarded $vm }
            catch { Write-Host "    cleanup warning for ${vm}: $($_.Exception.Message)" -ForegroundColor Yellow }
        }
    }
}

exit 0
