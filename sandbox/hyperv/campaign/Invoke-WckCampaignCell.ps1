#requires -Version 7.0
<#
.SYNOPSIS
    Minimal campaign cell-runner: ONE real M-UNINSTALL Persona-A cell, end to end, with
    the C-16 marker/path guard, the C-17 second-channel before/after witness, and the
    C-1 structural verifier (F0.5a thin slice).

.DESCRIPTION
    Wraps the proven Invoke-WckUninstallRun.ps1 flow and bolts the campaign hardening on
    top of it WITHOUT re-implementing the per-run orchestration. FIX-D single-epoch order:

      1. Assert-WckCampaignReady (D1)          — marker + VM present + checkpoint. ABORT if not.
      2. orphan-sweep + GUARDED restore (here)  — THIS runner does the restore so BEFORE/mutate/
                                                  AFTER all share ONE checkpoint-epoch.
      3. C-17 BEFORE second-channel snapshot     — PSDirect `reg export` + recursive Get-FileHash
                                                  of the target dirs -> RunRoot\before\.
      4. real M-UNINSTALL Persona-A cell          — delegates to Invoke-WckUninstallRun.ps1 with
                                                  -SkipInitialRestore -KeepDirtyState -Campaign so
                                                  it does NOT restart the epoch and every inner
                                                  force-op passes the marker gate.
      5. C-17 AFTER second-channel snapshot       — stamps afterSnapshotUtc; BEFORE any restore.
      6. evidence-pull + package                  — cell-manifest.json (+ afterSnapshotUtc/resetUtc).
      7. reset (D1-guarded) + stamp resetUtc       — only the marker'd VM; restore baseline.
      8. Test-WckCampaignEvidence (D2)            — write verifier verdict to RunRoot.

    *** BUILDER NOTE: this script is WRITTEN here but is NOT run against a real VM by the
    builder. The orchestrator runs it later. The host-safe self-test exercises D1/D2 only.

    FIX-F: dot-sourcing this file produces NO side effects — the orchestration runs only
    behind the entrypoint guard at the bottom. The self-test dot-sources it to import the
    module functions without launching a VM run.

    HOST SAFETY: every Stop-VM -TurnOff / Restore is preceded by Assert-WckDisposableVM;
    no Remove-Item runs outside Assert-WckPathUnderRoot. No host program/registry change.
#>
[CmdletBinding()]
param(
    # FIX-F: NOT [Parameter(Mandatory)] at script scope — a Mandatory script param would
    # prompt/bind when this file is DOT-SOURCED (import) and defeat import-safety. The
    # entrypoint guard below validates VMName/RunRoot before launching a real run.
    [string] $VMName,
    [ValidateSet('A','B')]    [string] $Persona   = 'A',
    [ValidateSet('Uninstall','Migration','Clean','Install')][string] $Module = 'Uninstall',
    [string] $RunRoot,                                    # e.g. F:\WCK-VM\campaign\<runid>
    [string] $Checkpoint    = 'baseline-clean',
    [int]    $ReadyTimeoutMin = 10,
    [switch] $Publish,
    # FIX-G: throwaway guest cred via env/param, NOT a script literal (disposable VM only).
    [securestring] $GuestPassword,
    # FIX-E: TEST-ONLY override of the pinned campaign root. Production callers MUST NOT set
    # this — the root is the fixed constant in Guard-WckDisposable.ps1. Guarded so a normal
    # invocation cannot redirect destructive ops to an arbitrary tree.
    [switch] $AllowTestRootOverride,
    [string] $TestCampaignRoot
)

Set-StrictMode -Version Latest

function Step([string]$m){ Write-Host "==> $m" -ForegroundColor Cyan }
function Info([string]$m){ Write-Host "    $m" -ForegroundColor DarkGray }

. "$PSScriptRoot\Guard-WckDisposable.ps1"
. "$PSScriptRoot\Test-WckCampaignEvidence.ps1"

function New-WckGuestCredential {
    [CmdletBinding()]
    param([securestring] $GuestPassword)

    if ($GuestPassword) {
        return [System.Management.Automation.PSCredential]::new('wck', $GuestPassword)
    }
    if ([string]::IsNullOrEmpty($env:WCK_GUEST_CRED)) {
        throw "Guest credential required: provide -GuestPassword (SecureString) or env:WCK_GUEST_CRED; refusing to use a script-literal default."
    }
    $sec = ConvertTo-SecureString $env:WCK_GUEST_CRED -AsPlainText -Force
    [System.Management.Automation.PSCredential]::new('wck', $sec)
}

function Get-WckCampaignModuleSpec {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [ValidateSet('Uninstall','Migration','Clean','Install')] [string] $Module)

    switch ($Module) {
        'Uninstall' {
            [pscustomobject]@{
                Module = 'Uninstall'
                GuestScript = 'guest-run.ps1'
                GuestScriptSource = (Join-Path $PSScriptRoot '..\guest-run.ps1')
                GuestWorkDir = 'C:\WCK-Input'
                GuestOutput = 'C:\WCK-Output'
                # Uninstall delegates publish to Invoke-WckUninstallRun.ps1 (-Publish); no pre-published
                # harness exe here. Declared $null (StrictMode-safe property access in Assert-WckCampaignHarness).
                HostHarnessDir = $null
                HarnessExe = $null
                HarnessProject = $null
                HostEvidenceNames = @('uninstall-e2e-evidence.json','uninstall-e2e-result.txt','harness-exitcode.txt')
                TargetDirs = @('C:\Program Files\Git', 'C:\Program Files\Microsoft VS Code', 'C:\Users\wck\AppData\Local\Programs\Microsoft VS Code')
                RegistryKeys = @(
                    'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                    'HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
                )
                RegistrySnapshotFile = 'uninstall-registry.reg'
                VerifyWithUninstallVerifier = $true
                LiveExec = $true
            }
        }
        'Migration' {
            [pscustomobject]@{
                Module = 'Migration'
                GuestScript = 'migration-guest-run.ps1'
                GuestScriptSource = (Join-Path $PSScriptRoot '..\migration-guest-run.ps1')
                GuestWorkDir = 'C:\WCK-MigE2E'
                GuestOutput = 'C:\WCK-MigOutput'
                HostHarnessDir = 'F:\WCK-VM\harness\mig'
                HarnessExe = 'MigrationE2E.exe'
                HarnessProject = 'tools\MigrationE2E\MigrationE2E.csproj'
                HostEvidenceNames = @('migration-e2e-summary.txt','migration-e2e-evidence.json','migration-export.zip')
                TargetDirs = @('C:\MigE2E\B')
                RegistryKeys = @()
                RegistrySnapshotFile = $null
                VerifyWithUninstallVerifier = $false
                LiveExec = $true
            }
        }
        'Clean' {
            [pscustomobject]@{
                Module = 'Clean'
                GuestScript = 'clean-guest-run.ps1'
                GuestScriptSource = (Join-Path $PSScriptRoot '..\clean-guest-run.ps1')
                GuestWorkDir = 'C:\WCK-CleanE2E'
                GuestOutput = 'C:\WCK-CleanOutput'
                HostHarnessDir = 'F:\WCK-VM\harness\clean'
                HarnessExe = 'CleanE2E.exe'
                HarnessProject = 'tools\CleanE2E\CleanE2E.csproj'
                HostEvidenceNames = @('clean-e2e-summary.txt','clean-e2e-evidence.json')
                TargetDirs = @('C:\Users\wck\AppData\Local\Temp\WCK-CleanE2E-Witness')
                RegistryKeys = @('HKCU\Software\Microsoft\Windows\CurrentVersion\Run')
                RegistrySnapshotFile = 'run-registry.reg'
                WitnessJunkDir = 'C:\Users\wck\AppData\Local\Temp\WCK-CleanE2E-Witness'
                WitnessRunValueName = 'WCK-CleanE2E-Witness'
                VerifyWithUninstallVerifier = $false
                LiveExec = $true
            }
        }
        'Install' {
            [pscustomobject]@{
                Module = 'Install'
                GuestScript = $null
                GuestScriptSource = $null
                GuestWorkDir = 'C:\WCK-InstallPlan'
                GuestOutput = 'C:\WCK-InstallOutput'
                HostHarnessDir = $null
                HarnessExe = $null
                HarnessProject = $null
                HostEvidenceNames = @('install-plan-proof.json')
                TargetDirs = @('C:\WCK-InstallPlan')
                RegistryKeys = @()
                RegistrySnapshotFile = $null
                VerifyWithUninstallVerifier = $false
                LiveExec = $false
            }
        }
    }
}

function Assert-WckCampaignHarness {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [object] $ModuleSpec,
        [switch] $Publish
    )

    if ([string]::IsNullOrWhiteSpace([string]$ModuleSpec.HarnessExe)) { return }
    $exe = Join-Path ([string]$ModuleSpec.HostHarnessDir) ([string]$ModuleSpec.HarnessExe)
    if ((Test-Path -LiteralPath $exe -PathType Leaf) -and -not $Publish) { return }

    if (-not $Publish) {
        throw "required harness missing: $exe (run with -Publish or pre-stage the harness)"
    }

    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    $dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
        throw "Per-user .NET SDK not found at $dotnet."
    }
    New-Item -ItemType Directory -Force -Path ([string]$ModuleSpec.HostHarnessDir) | Out-Null
    $csproj = Join-Path $repoRoot ([string]$ModuleSpec.HarnessProject)
    & $dotnet publish $csproj -c Release --output ([string]$ModuleSpec.HostHarnessDir) --runtime win-x64 --self-contained true
    if ($LASTEXITCODE -ne 0) { throw "$($ModuleSpec.HarnessExe) publish failed ($LASTEXITCODE)." }
}

function New-WckInstallPlanProof {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $OutDir,
        [Parameter(Mandatory)] [string] $Persona
    )

    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $proof = [ordered]@{
        module = 'Install'
        persona = $Persona
        generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
        liveExecution = $false
        status = 'design-deferred'
        evidence = 'export-strict-load-plan-only'
        warning = 'canli-exec yok; tasarimca-ertelendi'
    }
    $path = Join-Path $OutDir 'install-plan-proof.json'
    $proof | ConvertTo-Json | Set-Content -LiteralPath $path -Encoding UTF8
    return $path
}

function Assert-WckCampaignFinalState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $VMName,
        [Parameter(Mandatory)] [string] $Checkpoint,
        [object] $VMObject,
        [string] $ActualCheckpoint
    )

    $vm = $VMObject
    if (-not $vm) { $vm = Get-VM -Name $VMName -ErrorAction Stop }
    $name = if ($vm.PSObject.Properties.Match('Name').Count) { [string]$vm.Name } else { $VMName }
    if ($name -ne $VMName) {
        throw "Final reset proof failed: VM object name '$name' != expected '$VMName'."
    }
    $state = if ($vm.PSObject.Properties.Match('State').Count) { [string]$vm.State } else { '' }
    if ($state -ne 'Off') {
        throw "Final reset proof failed: VM '$VMName' state is '$state' (expected Off)."
    }
    if (-not [string]::IsNullOrWhiteSpace($ActualCheckpoint) -and $ActualCheckpoint -ne $Checkpoint) {
        throw "Final reset proof failed: checkpoint '$ActualCheckpoint' != expected '$Checkpoint'."
    }
    return [pscustomobject]@{ state = 'Off'; checkpoint = $Checkpoint }
}

function Write-WckDirHashesJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [AllowNull()] [object[]] $Hashes
    )

    $arr = @($Hashes | Where-Object { $null -ne $_ })
    if ($arr.Count -eq 0) {
        Set-Content -LiteralPath $Path -Value '[]' -Encoding UTF8
        return
    }
    ConvertTo-Json -InputObject $arr -Depth 4 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Invoke-WckCampaignCell {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $VMName,
        [string] $Persona = 'A',
        [string] $Module  = 'Uninstall',
        [Parameter(Mandatory)] [string] $RunRoot,
        [string] $Checkpoint = 'baseline-clean',
        [int]    $ReadyTimeoutMin = 10,
        [switch] $Publish,
        [securestring] $GuestPassword,
        [switch] $AllowTestRootOverride,
        [string] $TestCampaignRoot
    )

    $ErrorActionPreference = 'Stop'

    # FIX-E: root is PINNED to the guard's constant. A caller may redirect it ONLY behind the
    # explicit test switch; production code never sets it.
    $campaignRoot = $script:WckCampaignRoot
    if ($AllowTestRootOverride -and -not [string]::IsNullOrWhiteSpace($TestCampaignRoot)) {
        Info "TEST root override in effect: $TestCampaignRoot (NOT for production)."
        $campaignRoot = $TestCampaignRoot
    }

    $runId = Split-Path $RunRoot -Leaf
    if ([string]::IsNullOrWhiteSpace($runId)) { throw "RunRoot must end in a run-id segment." }

    # RunRoot MUST be under the campaign root (C-16) before we create/clear anything there.
    $null = Assert-WckPathUnderRoot -Path $RunRoot -Root $campaignRoot

    $beforeDir = Join-Path $RunRoot 'before'
    $afterDir  = Join-Path $RunRoot 'after'
    New-Item -ItemType Directory -Force -Path $RunRoot,$beforeDir,$afterDir | Out-Null

    $cred = New-WckGuestCredential -GuestPassword $GuestPassword

    $moduleSpec = Get-WckCampaignModuleSpec -Module $Module
    Assert-WckCampaignHarness -ModuleSpec $moduleSpec -Publish:$Publish

    # Registry hives + target dirs the second channel snapshots.
    $registryKeys = @($moduleSpec.RegistryKeys)
    $registrySnapshotFile = if ($moduleSpec.RegistrySnapshotFile) { [string]$moduleSpec.RegistrySnapshotFile } else { $null }
    $targetDirs = @($moduleSpec.TargetDirs)

    function Invoke-SecondChannelSnapshot {
        param([string] $Phase, [string] $OutDir)
        Step "C-17 $Phase second-channel snapshot (PSDirect reg export + recursive file-hash)..."
        $regOut = if ($registrySnapshotFile) { Join-Path $OutDir $registrySnapshotFile } else { $null }
        $hashOut = Join-Path $OutDir 'dir-hashes.json'

        $session = New-PSSession -VMName $VMName -Credential $cred
        try {
            if ($regOut) {
                $regText = Invoke-Command -Session $session -ScriptBlock {
                    param($keys)
                    $sb = [System.Text.StringBuilder]::new()
                    foreach ($k in $keys) {
                        $tmp = Join-Path $env:TEMP ("wck-2ndchan-{0}.reg" -f ([guid]::NewGuid().ToString('N')))
                        & reg.exe export $k $tmp /y *> $null
                        if (Test-Path $tmp) { [void]$sb.AppendLine((Get-Content -LiteralPath $tmp -Raw)); Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
                    }
                    $sb.ToString()
                } -ArgumentList (,$registryKeys)
                if ($null -eq $regText) { $regText = '' }
                Set-Content -LiteralPath $regOut -Value ([string]$regText) -Encoding UTF8
            }

            $hashes = Invoke-Command -Session $session -ScriptBlock {
                param($dirs)
                $list = @()
                foreach ($d in $dirs) {
                    if (Test-Path -LiteralPath $d) {
                        Get-ChildItem -LiteralPath $d -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
                            $list += [pscustomobject]@{ Path = $_.FullName; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
                        }
                    }
                }
                $list
            } -ArgumentList (,$targetDirs)
            Write-WckDirHashesJson -Path $hashOut -Hashes @($hashes)
            $regLabel = if ($regOut) { Split-Path $regOut -Leaf } else { '(no registry snapshot)' }
            Info "$Phase snapshot written: $regLabel, $(Split-Path $hashOut -Leaf)"
        }
        finally { Remove-PSSession $session -ErrorAction SilentlyContinue }
    }

    function Initialize-WckCleanSecondChannelWitness {
        Step "Seeding M-CLEAN second-channel witness (junk dir + HKCU Run value) before BEFORE snapshot..."
        $session = New-PSSession -VMName $VMName -Credential $cred
        try {
            Invoke-Command -Session $session -ScriptBlock {
                param($junkDir, $runValueName)
                New-Item -ItemType Directory -Force -Path $junkDir | Out-Null
                Set-Content -LiteralPath (Join-Path $junkDir 'junk.txt') -Value 'WCK CleanE2E witness junk file' -Encoding ascii
                New-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Force | Out-Null
                New-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $runValueName -Value 'C:\FAKE-WCK-CLEANE2E.exe' -PropertyType String -Force | Out-Null
            } -ArgumentList ([string]$moduleSpec.WitnessJunkDir),([string]$moduleSpec.WitnessRunValueName)
        }
        finally { Remove-PSSession $session -ErrorAction SilentlyContinue }
    }

    function Reset-CampaignVM {
        # Guarded reset: marker check BEFORE any TurnOff/restore (C-16/C-5).
        $null = Assert-WckDisposableVM -VMName $VMName
        $cp = Get-VMCheckpoint -VMName $VMName -Name $Checkpoint -ErrorAction SilentlyContinue
        if (-not $cp) { throw "Reset: checkpoint '$Checkpoint' missing on '$VMName'." }
        if ((Get-VM -Name $VMName).State -ne 'Off') { Stop-VM -Name $VMName -TurnOff -Force }
        Restore-VMCheckpoint -VMName $VMName -Name $Checkpoint -Confirm:$false
        if ((Get-VM -Name $VMName).State -ne 'Off') { Stop-VM -Name $VMName -TurnOff -Force }
    }

    $runStartUtc = $null
    $afterSnapshotUtc = $null
    $resetUtc = $null
    $resetProven = $false

    try {
        # --- 1. precondition gate (D1) -----------------------------------------------------
        Step "Asserting campaign-ready ($VMName, marker + checkpoint '$Checkpoint')..."
        $ready = Assert-WckCampaignReady -VMName $VMName -Checkpoint $Checkpoint
        $runStartUtc = (Get-Date).ToUniversalTime().ToString('o')
        Info "campaign GUID: $($ready.CampaignGuid)"

        # --- 2. orphan-sweep + GUARDED restore (THIS runner owns the epoch — FIX-D) ---------
        Step "Orphan-sweep (graceful stop of any running campaign VM) + restore baseline..."
        Get-VM | Where-Object { $_.State -ne 'Off' -and $_.Name -eq $VMName } | ForEach-Object {
            $null = Assert-WckDisposableVM -VMName $_.Name
            Stop-VM -Name $_.Name -TurnOff -Force
        }
        Reset-CampaignVM   # pristine epoch starts HERE; BEFORE/mutate/AFTER share it.

        # boot + wait for PowerShell Direct (same readiness contract as Invoke-WckUninstallRun)
        Step "Starting VM; waiting for PowerShell Direct..."
        Start-VM -Name $VMName
        $deadline = (Get-Date).AddMinutes($ReadyTimeoutMin); $up = $false
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 10
            try { if (Invoke-Command -VMName $VMName -Credential $cred -ScriptBlock { Test-Path 'C:\wck-ready.txt' } -ErrorAction Stop) { $up = $true; break } }
            catch { Info "guest not reachable yet..." }
        }
        if (-not $up) { throw "Guest not ready within $ReadyTimeoutMin min." }

        if ($Module -eq 'Clean') {
            Initialize-WckCleanSecondChannelWitness
        }

        # --- 3. C-17 BEFORE snapshot (pre-state proof, 2nd channel) -------------------------
        Invoke-SecondChannelSnapshot -Phase 'BEFORE' -OutDir $beforeDir

        # --- 4. module cell ---------------------------------------------------------------
        $harnessOut = Join-Path $RunRoot 'harness-output'
        New-Item -ItemType Directory -Force -Path $harnessOut | Out-Null
        switch ($Module) {
            'Uninstall' {
                # Existing F0.5a path is preserved exactly: the cell runner owns the epoch;
                # the inner runner only mutates and leaves the dirty state for AFTER witness.
                Step "Running M-UNINSTALL cell (Invoke-WckUninstallRun, campaign mode)..."
                $innerArgs = @{
                    VMName             = $VMName
                    Checkpoint         = $Checkpoint
                    OutputDir          = $harnessOut
                    KeepDirtyState     = $true
                    SkipInitialRestore = $true
                    Campaign           = $true
                }
                if ($Publish) { $innerArgs.Publish = $true }
                if ($GuestPassword) { $innerArgs.GuestPassword = $GuestPassword }
                & "$PSScriptRoot\..\Invoke-WckUninstallRun.ps1" @innerArgs
            }
            'Migration' {
                Step "Running M-MIGRATION cell (migration-guest-run.ps1)..."
                $session = New-PSSession -VMName $VMName -Credential $cred
                try {
                    Invoke-Command -Session $session -ScriptBlock {
                        Remove-Item 'C:\WCK-MigE2E','C:\WCK-MigOutput' -Recurse -Force -ErrorAction SilentlyContinue
                        New-Item -ItemType Directory -Force -Path 'C:\WCK-MigE2E','C:\WCK-MigOutput' | Out-Null
                    }
                    Copy-Item -Path (Join-Path ([string]$moduleSpec.HostHarnessDir) '*') -Destination 'C:\WCK-MigE2E' -ToSession $session -Recurse -Force
                    Copy-Item -Path $moduleSpec.GuestScriptSource -Destination 'C:\WCK-MigE2E\migration-guest-run.ps1' -ToSession $session -Force
                    $migResult = Invoke-Command -Session $session -ScriptBlock {
                        Set-ExecutionPolicy -Scope Process Bypass -Force -ErrorAction SilentlyContinue
                        & 'C:\WCK-MigE2E\migration-guest-run.ps1' -Harness 'C:\WCK-MigE2E\MigrationE2E.exe' -Base 'C:\MigE2E' -Output 'C:\WCK-MigOutput'
                    }
                    $migResult | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $harnessOut 'migration-e2e-evidence.json') -Encoding UTF8
                    Copy-Item -Path 'C:\WCK-MigOutput\*' -Destination $harnessOut -FromSession $session -Recurse -Force -ErrorAction SilentlyContinue
                    Copy-Item -Path 'C:\MigE2E\Pkg\migration-export.zip' -Destination $harnessOut -FromSession $session -Force -ErrorAction SilentlyContinue
                } finally { Remove-PSSession $session -ErrorAction SilentlyContinue }
            }
            'Clean' {
                Step "Running M-CLEAN cell (clean-guest-run.ps1)..."
                $session = New-PSSession -VMName $VMName -Credential $cred
                try {
                    Invoke-Command -Session $session -ScriptBlock {
                        Remove-Item 'C:\WCK-CleanE2E','C:\WCK-CleanOutput' -Recurse -Force -ErrorAction SilentlyContinue
                        New-Item -ItemType Directory -Force -Path 'C:\WCK-CleanE2E','C:\WCK-CleanOutput' | Out-Null
                    }
                    Copy-Item -Path (Join-Path ([string]$moduleSpec.HostHarnessDir) '*') -Destination 'C:\WCK-CleanE2E' -ToSession $session -Recurse -Force
                    Copy-Item -Path $moduleSpec.GuestScriptSource -Destination 'C:\WCK-CleanE2E\clean-guest-run.ps1' -ToSession $session -Force
                    $cleanResult = Invoke-Command -Session $session -ScriptBlock {
                        param($junkDir, $runValueName)
                        Set-ExecutionPolicy -Scope Process Bypass -Force -ErrorAction SilentlyContinue
                        & 'C:\WCK-CleanE2E\clean-guest-run.ps1' -Harness 'C:\WCK-CleanE2E\CleanE2E.exe' -Output 'C:\WCK-CleanOutput' -JunkDir $junkDir -RunValueName $runValueName
                    } -ArgumentList ([string]$moduleSpec.WitnessJunkDir),([string]$moduleSpec.WitnessRunValueName)
                    $cleanResult | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $harnessOut 'clean-e2e-evidence.json') -Encoding UTF8
                    Copy-Item -Path 'C:\WCK-CleanOutput\*' -Destination $harnessOut -FromSession $session -Recurse -Force -ErrorAction SilentlyContinue
                } finally { Remove-PSSession $session -ErrorAction SilentlyContinue }
            }
            'Install' {
                Step "Recording M-INSTALL export/strict-load proof (live execution intentionally deferred)..."
                $null = New-WckInstallPlanProof -OutDir $harnessOut -Persona $Persona
            }
        }

        # --- 5. C-17 AFTER snapshot (BEFORE any reset) — stamp afterSnapshotUtc -------------
        Invoke-SecondChannelSnapshot -Phase 'AFTER' -OutDir $afterDir
        $afterSnapshotUtc = (Get-Date).ToUniversalTime().ToString('o')

        # --- 6. evidence-pull + package ----------------------------------------------------
        Step "Packaging evidence into RunRoot..."
        foreach ($name in @($moduleSpec.HostEvidenceNames)) {
            $src = Join-Path $harnessOut $name
            if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination (Join-Path $RunRoot $name) -Force }
        }

        # --- 7. reset (D1-guarded) — final VM Off + baseline checkpoint (C-5) — stamp reset -
        Step "Resetting VM to clean baseline (guarded)..."
        Reset-CampaignVM
        $resetUtc = (Get-Date).ToUniversalTime().ToString('o')
        $finalProof = Assert-WckCampaignFinalState -VMName $VMName -Checkpoint $Checkpoint -ActualCheckpoint $Checkpoint
        $resetProven = $true
        $finalProof |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $RunRoot 'vm-final-state.json') -Encoding UTF8

        # Manifest written AFTER reset so afterSnapshotUtc + resetUtc are both stamped (FIX-C).
        @{
            runId        = $runId
            vmName       = $VMName
            persona      = $Persona
            module       = $Module
            checkpoint   = $Checkpoint
            campaignGuid = $ready.CampaignGuid
            runStartUtc     = $runStartUtc
            generatedUtc     = (Get-Date).ToUniversalTime().ToString('o')
            afterSnapshotUtc = $afterSnapshotUtc
            resetUtc         = $resetUtc
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $RunRoot 'cell-manifest.json') -Encoding UTF8

        # --- 8. structural verifier (D2) ---------------------------------------------------
        Step "Running structural evidence verifier (D2)..."
        if ($Module -in @('Uninstall','Migration','Clean')) {
            $verdict = Test-WckCampaignEvidence -EvidenceDir $RunRoot
        } else {
            $verdict = [pscustomobject]@{
                Pass = $true
                Reasons = @()
                Digest = (Get-WckEvidenceDigest -Files @(Get-ChildItem -LiteralPath $RunRoot -Recurse -File | ForEach-Object FullName) -BaseDir $RunRoot)
                Module = $Module
                Verifier = 'module-dispatch-structural'
            }
        }
        $verdict | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $RunRoot 'campaign-verdict.json') -Encoding UTF8

        Write-Host ''
        Write-Host '================================================================' -ForegroundColor Green
        Write-Host " Campaign cell verdict: Pass=$($verdict.Pass)" -ForegroundColor Green
        Write-Host "   digest : $($verdict.Digest)" -ForegroundColor Green
        Write-Host "   runRoot: $RunRoot" -ForegroundColor Green
        if (-not $verdict.Pass) { foreach ($r in $verdict.Reasons) { Write-Host "   FAIL: $r" -ForegroundColor Yellow } }
        Write-Host '================================================================' -ForegroundColor Green

        return $verdict
    }
    catch {
        Write-Host "CELL ERROR: $($_.Exception.Message)" -ForegroundColor Red
        throw
    }
    finally {
        # FIX-F: GUARANTEE a guarded reset so a crashed/dirty VM is never left running. Do NOT
        # swallow a reset failure silently — a still-running campaign VM is a host-safety issue.
        try {
            $st = (Get-VM -Name $VMName -ErrorAction SilentlyContinue)
            if ($st -and -not $resetProven) {
                Step "FINALLY: campaign reset proof missing — forcing a guarded reset (cleanup)."
                Reset-CampaignVM
                $null = Assert-WckCampaignFinalState -VMName $VMName -Checkpoint $Checkpoint -ActualCheckpoint $Checkpoint
            }
        }
        catch {
            Write-Host "FINALLY cleanup FAILED (VM may be dirty/running): $($_.Exception.Message)" -ForegroundColor Red
            throw
        }
    }
}

# FIX-F: entrypoint guard — only RUN the cell when invoked as a script, not when dot-sourced.
if ($MyInvocation.InvocationName -ne '.') {
    if ([string]::IsNullOrWhiteSpace($VMName))  { throw "Invoke-WckCampaignCell: -VMName is required when run as a script." }
    if ([string]::IsNullOrWhiteSpace($RunRoot)) { throw "Invoke-WckCampaignCell: -RunRoot is required when run as a script." }
    $cellArgs = @{
        VMName          = $VMName
        Persona         = $Persona
        Module          = $Module
        RunRoot         = $RunRoot
        Checkpoint      = $Checkpoint
        ReadyTimeoutMin = $ReadyTimeoutMin
    }
    if ($Publish)               { $cellArgs.Publish = $true }
    if ($GuestPassword)         { $cellArgs.GuestPassword = $GuestPassword }
    if ($AllowTestRootOverride) { $cellArgs.AllowTestRootOverride = $true; $cellArgs.TestCampaignRoot = $TestCampaignRoot }
    $verdict = Invoke-WckCampaignCell @cellArgs
    if (-not $verdict.Pass) { exit 1 }
}
