#requires -Version 7.0
<#
.SYNOPSIS
  Capture POPULATED-state screenshots of the Backup, Clean, and Reinstall (Install) modules,
  in the disposable 'WCK-E2E' guest, for the README/docs.

.DESCRIPTION
  Today Capture-ReadmeShots.ps1's Backup/Clean/Install shots show EMPTY state: those three
  view-models never auto-scan on navigation (unlike Uninstall/Migration) — each one only
  populates its preview list after an explicit button click ("Scan for junk" / "Build backup
  plan" / "Load reinstall list" + "Build restore plan"), and Backup additionally needs a
  payload-folder path typed into a TextBox before its button is enabled. A plain
  `--screen <module>` deep-link + capture, with nobody ever clicking, therefore always shows
  the pre-click empty state regardless of how much content exists on disk.

  This harness:
    1. Guarded (Guard-WckDisposable.ps1): restores 'baseline-campaign', boots with a
       host-headroom-fit dynamic-memory startup size, waits for PowerShell Direct.
    2. Pushes a FRESH resident capture agent (Install-CaptureAgent.ps1) — the agent now also
       knows the 'settext' (set a TextBox value) and 'invoke' (click a button by its visible
       name) UI-Automation ops, added to guest-agent\Wck.CaptureAgent.ps1 for this harness.
    3. Seeds the guest (populated-seed-guest-run.ps1, 5.1-safe): installs git/Notepad++/VS
       Code/Chrome from the cached offline installers, writes their profile config files (the
       same source paths the bundled backup manifest already declares), and creates
       synthetic, personal-data-free junk (TEMP files, a Chrome-shaped cache dir, a few
       Recycle-Bin items) so Clean has real content to show.
    4. Publishes + deploys the WPF app into the guest (mirrors Capture-ReadmeShots.ps1).
    5. For each of Clean/Backup/Install: launches the app deep-linked to that screen, drives
       the module's own "populate" click(s) via the new UI-Automation ops (Send-WckCaptureOp.ps1
       against the SAME resident agent/queue Show-InGuestApp.ps1 uses — no new capture
       mechanism), then captures the now-populated window.
    6. finally: guarded reset to 'baseline-campaign' (Assert-WckDisposableVM before any
       force-op), so the guest is never left dirty.

  HOST SAFETY: touches only the VM named -VMName and writes PNGs under -OutDir (host-state,
  not repo content). The only VM credential is the documented throwaway autologon
  'WckE2E!2026' (disposable, network-isolated guest, reverted on every checkpoint restore —
  same value already used by every other sandbox\hyperv harness; not a real secret).

.EXAMPLE
  pwsh -File Capture-PopulatedShots.ps1
#>
[CmdletBinding()]
param(
    [string] $VMName = 'WCK-E2E',
    [string] $Checkpoint = 'baseline-campaign',
    [string] $GuestUser = 'wck',
    [string] $InstallersDir = 'F:\WCK-VM\installers',
    [string] $GuestApp = 'C:\WCK-App',
    [string] $OutDir = 'F:\WCK-VM\shots\populated',
    [int]    $ReadyTimeoutMin = 10,
    [int]    $GuestStartupMB = 2048,
    [int]    $SettleMs = 2200,        # post-window / post-click settle: async manifest/junk scans need time
    [switch] $SkipPublish,
    [switch] $SkipInstalls,           # seed profile-config/junk only, skip the (slow) app installs
    [switch] $KeepDirtyState          # debug ONLY: skip the final checkpoint restore
)

$ErrorActionPreference = 'Stop'
function Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Info([string]$m) { Write-Host "    $m" -ForegroundColor DarkGray }

# Throwaway autologon credential for the disposable, network-isolated WCK-E2E guest — the same
# documented value every sandbox\hyperv harness uses (reverted on every checkpoint restore; no
# value outside the VM). NOT a real secret.
$GuestPass = 'WckE2E!2026'

. (Join-Path $PSScriptRoot 'campaign\Guard-WckDisposable.ps1')

if (-not (Get-Command Get-VM -ErrorAction SilentlyContinue)) { throw "Hyper-V module unavailable." }
$ready = Assert-WckCampaignReady -VMName $VMName -Checkpoint $Checkpoint   # THROWS unless marker+checkpoint present
Info "campaign guid: $($ready.CampaignGuid); checkpoint: $($ready.Checkpoint)"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$csproj = Join-Path $repoRoot 'src\Suite.App.Wpf\Suite.App.Wpf.csproj'
$publishDir = 'F:\WCK-VM\wck-app\publish'
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

# Screen key -> output filename + the UI-automation steps needed to populate it before capture.
# 'Text' (if present) is typed into the module's TextBox BEFORE any button is clicked.
$modules = [ordered]@{
    'clean'   = @{ File = '02-clean.png'; Text = ''; Buttons = @('Scan for junk') }
    'backup'  = @{ File = '03-backup.png'; Text = 'C:\WCK-BackupOut'; Buttons = @('Build backup plan') }
    'install' = @{ File = '05-reinstall.png'; Text = ''; Buttons = @('Load reinstall list', 'Build restore plan') }
}

$requiredInstallers = @('git.exe', 'npp.exe', 'vscode.exe')
foreach ($name in $requiredInstallers) {
    $path = Join-Path $InstallersDir $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required installer missing: $path" }
}
if (-not (Test-Path -LiteralPath (Join-Path $InstallersDir 'chrome.msi') -PathType Leaf)) {
    Info "chrome.msi missing; Chrome install will be skipped (synthetic cache dir is still seeded)."
}

if (-not $SkipPublish) {
    Step "Publishing WPF app (self-contained folder win-x64)..."
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    & $dotnet publish $csproj -c Release --runtime win-x64 --self-contained true `
        --output $publishDir 2>&1 | Select-Object -Last 3
    if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }
    Remove-Item (Join-Path $publishDir '*.pdb') -Force -ErrorAction SilentlyContinue
}
$exeHost = Join-Path $publishDir 'WindowsCareKit.exe'
if (-not (Test-Path $exeHost)) { throw "published exe not found: $exeHost (run without -SkipPublish)" }

# Output dir guard (mirrors Invoke-WckMigrationSelfTest.ps1 / Invoke-WckRealMigrationCampaign.ps1):
# only the default dir, or an already-empty one, may be cleared.
if (Test-Path -LiteralPath $OutDir) {
    $isDefault = ($OutDir -eq 'F:\WCK-VM\shots\populated')
    $isEmpty = -not (Get-ChildItem -LiteralPath $OutDir -ErrorAction SilentlyContinue | Select-Object -First 1)
    if (-not ($isDefault -or $isEmpty)) { throw "Output dir '$OutDir' is non-empty and not the default; refuse to clear." }
    Remove-Item -LiteralPath $OutDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$sec = ConvertTo-SecureString $GuestPass -AsPlainText -Force
$cred = [System.Management.Automation.PSCredential]::new(".\$GuestUser", $sec)

function Restore-BaselineGuarded {
    # Guarded: only a VM carrying the campaign marker may be force-stopped / checkpoint-restored.
    Assert-WckDisposableVM -VMName $VMName | Out-Null
    if ((Get-VM -Name $VMName).State -ne 'Off') { Stop-VM -Name $VMName -TurnOff -Force }
    Restore-VMCheckpoint -VMName $VMName -Name $Checkpoint -Confirm:$false
}

function Wait-GuestReady {
    # Fit startup RAM to host headroom (same rationale as Invoke-WckRealMigrationCampaign.ps1): a
    # large fixed startup size can fail to allocate under host memory pressure (0x800705AA); with
    # dynamic memory (min already set by the VM) the guest boots small and balloons up only if the
    # host has free RAM. Applied AFTER the checkpoint restore (which reverts VM config), while Off.
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
    } catch { Info "could not adjust startup RAM: $($_.Exception.Message)" }

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
        } catch { Info "not reachable yet..." }
    }
    throw "$VMName guest not ready within $ReadyTimeoutMin min."
}

$sendOp = Join-Path $PSScriptRoot 'Send-WckCaptureOp.ps1'

function Invoke-PopulatedCapture {
    param(
        [Parameter(Mandatory)] [System.Management.Automation.Runspaces.PSSession] $Session,
        [Parameter(Mandatory)] [string] $Exe,
        [Parameter(Mandatory)] [string] $ScreenKey,
        [string] $Text,
        [string[]] $Buttons = @(),
        [Parameter(Mandatory)] [string] $OutPng
    )
    Step "Capturing populated '$ScreenKey' -> $OutPng ..."
    $launch = & $sendOp -Session $Session -Op launch -Exe $Exe -AppArgs @('--lang', 'en', '--screen', $ScreenKey) -TimeoutSec 40
    try {
        Start-Sleep -Milliseconds $SettleMs
        if ($Text) {
            & $sendOp -Session $Session -Op settext -TargetPid $launch.pid -Text $Text | Out-Null
            Start-Sleep -Milliseconds 500
        }
        foreach ($btn in $Buttons) {
            Info "clicking '$btn'..."
            & $sendOp -Session $Session -Op invoke -TargetPid $launch.pid -ButtonName $btn | Out-Null
            Start-Sleep -Milliseconds $SettleMs   # let the async scan/build-plan command finish + databind
        }
        & $sendOp -Session $Session -Op capture -TargetPid $launch.pid -OutPng $OutPng -SettleMs 900 | Out-Null
    } finally {
        try { & $sendOp -Session $Session -Op close -TargetPid $launch.pid | Out-Null } catch { }
    }
    if (-not (Test-Path -LiteralPath $OutPng -PathType Leaf)) { throw "populated capture '$ScreenKey' produced no PNG at $OutPng" }
    Info "saved $OutPng"
}

$results = @()
try {
    Step "Restoring '$Checkpoint' checkpoint..."
    Restore-BaselineGuarded
    Wait-GuestReady

    Step "Ensuring FRESH capture agent in guest (pushes settext/invoke support)..."
    & (Join-Path $PSScriptRoot 'Install-CaptureAgent.ps1') -VMName $VMName -GuestUser $GuestUser -GuestPass $GuestPass

    $session = New-PSSession -VMName $VMName -Credential $cred
    try {
        Step "Pushing installers + seed script into the guest..."
        Invoke-Command -Session $session -ScriptBlock {
            Remove-Item 'C:\WCK-Input', 'C:\WCK-Output' -Recurse -Force -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Force -Path 'C:\WCK-Input\installers', 'C:\WCK-Output' | Out-Null
        }
        Copy-Item -Path (Join-Path $InstallersDir '*') -Destination 'C:\WCK-Input\installers' -ToSession $session -Recurse -Force
        Copy-Item -Path (Join-Path $PSScriptRoot 'populated-seed-guest-run.ps1') -Destination 'C:\WCK-Input\populated-seed-guest-run.ps1' -ToSession $session -Force

        Step "Running the guest seed (installs + profile config + junk + recycle bin)..."
        $seedResult = Invoke-Command -Session $session -ArgumentList $SkipInstalls.IsPresent -ScriptBlock {
            param([bool]$skipInstalls)
            Set-ExecutionPolicy -Scope Process Bypass -Force -ErrorAction SilentlyContinue
            & 'C:\WCK-Input\populated-seed-guest-run.ps1' -InstallersDir 'C:\WCK-Input\installers' -Output 'C:\WCK-Output' -SkipInstalls:$skipInstalls
        }
        Info "seed exit: $($seedResult.ExitCode)"
        if ($seedResult.ExitCode -ne 0) { throw "guest seed script failed with exit $($seedResult.ExitCode)" }
        Copy-Item -Path 'C:\WCK-Output\populated-seed.*' -Destination $OutDir -FromSession $session -Force -ErrorAction SilentlyContinue

        Step "Deploying fresh app build to guest $GuestApp ..."
        Invoke-Command -Session $session -ArgumentList $GuestApp -ScriptBlock {
            param($dst)
            if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
            New-Item -ItemType Directory -Force -Path $dst | Out-Null
        }
        Copy-Item -Path (Join-Path $publishDir '*') -Destination $GuestApp -ToSession $session -Recurse -Force
        $deployed = Invoke-Command -Session $session -ArgumentList $GuestApp -ScriptBlock {
            param($d) Test-Path (Join-Path $d 'WindowsCareKit.exe')
        }
        if (-not $deployed) { throw "deploy verification failed: WindowsCareKit.exe missing in guest" }
        $exeGuest = Join-Path $GuestApp 'WindowsCareKit.exe'

        foreach ($mod in $modules.Keys) {
            $spec = $modules[$mod]
            $out = Join-Path $OutDir $spec.File
            try {
                Invoke-PopulatedCapture -Session $session -Exe $exeGuest -ScreenKey $mod `
                    -Text $spec.Text -Buttons $spec.Buttons -OutPng $out
                $results += [pscustomobject]@{ Module = $mod; File = $spec.File; Ok = $true }
            } catch {
                $results += [pscustomobject]@{ Module = $mod; File = $spec.File; Ok = $false }
                Write-Host "    FAILED '$mod': $($_.Exception.Message)" -ForegroundColor Yellow
            }
        }
    } finally {
        Remove-PSSession $session -ErrorAction SilentlyContinue
    }

    Write-Host ''
    Step "Capture summary"
    $results | Format-Table -AutoSize
    $fail = @($results | Where-Object { -not $_.Ok }).Count
    if ($fail -gt 0) { Write-Host "$fail module(s) failed — see above." -ForegroundColor Yellow }
    else { Write-Host "All $($results.Count) populated-state shots captured to $OutDir" -ForegroundColor Green }
}
finally {
    if (-not $KeepDirtyState) {
        Step "Resetting VM to clean baseline (discard seeded content)..."
        try { Restore-BaselineGuarded }
        catch { Write-Host "    cleanup warning: $($_.Exception.Message)" -ForegroundColor Yellow }
    }
}

if (@($results | Where-Object { -not $_.Ok }).Count -gt 0) { exit 1 }
exit 0
