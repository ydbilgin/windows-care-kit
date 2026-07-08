#requires -Version 7.0
<#
.SYNOPSIS
  M5 VM gate: exercise the compiled Inno Setup component installer (installer/WindowsCareKit.iss)
  end-to-end in the disposable 'WCK-E2E' guest - the only place it can be run (the installer is
  elevated and writes Program Files/ARP; it must NEVER touch the host).
.DESCRIPTION
  Consumes the CI gate-build artifact (WindowsCareKit-Setup-v0.0.0-dev-win-x64.exe from a
  workflow_dispatch run of release.yml) and proves the component model end to end:

    0. Host free-RAM check (refuses to start the guest under host memory pressure) + capture-agent
       bootstrap (Install-CaptureAgent.ps1 - starts the VM if needed) + fit the VM's dynamic-memory
       startup size to 2048 MB (same host-headroom-fit rationale as Capture-PopulatedShots.ps1 /
       Invoke-WckRealMigrationCampaign.ps1) applied BEFORE the VM starts, while it is Off.
    1. Push -SetupExe into the guest as C:\WCK-Installer\Setup.exe; verify the PowerShell Direct
       session already carries a full (elevated) admin token - 'wck' is a local admin and PS Direct
       is not subject to the network UAC token-filter, so a PrivilegesRequired=admin installer runs
       silently with no UAC prompt (same rationale documented in guest-run.ps1).
    2. PARTIAL install: /COMPONENTS="uninstall,clean,backup,restore,install" (Migration UNCHECKED).
       Assert the exe exists, Modules\migration is absent with zero Suite.Module.Migration*.dll
       anywhere under the install dir, the other 5 module DLLs are present, all 10 manifests are
       laid out per their owning component, and the Start Menu shortcut exists.
    3. ACL assert (the M4-audit security core): Get-Acl on the install dir and Modules\ must show
       NO Allow ACE granting Write/Modify/FullControl/CreateFiles/AppendData to
       BUILTIN\Users / Authenticated Users / Everyone - Program-Files-inherited admin ACL only.
    4. Two shots via Show-InGuestApp.ps1 against the installed exe (6 nav items, no Migration tab).
    5. MODIFY (add later): re-run Setup.exe with all 6 components ticked (same AppId - Inno's
       Modify mode); assert Modules\migration now carries BOTH Suite.Module.Migration.dll and
       Suite.Module.Migration.Recipes.dll. One more shot (7 nav items, Migration present).
    6. UNINSTALL: read QuietUninstallString from the ARP key, run it, and poll (Inno's silent
       uninstaller relaunches itself from a temp copy, so -Wait alone is not trustworthy) until the
       install dir is gone; assert the install dir, the ARP key, and the Start Menu shortcut are
       all gone.
    7. Print a PASS/FAIL summary; exit non-zero if any assertion failed.

  finally { Stop-VM -Force } unconditionally turns the guest off, whether the run passed or failed.

  ALL guest-side script blocks are WinPS 5.1-safe (PowerShell Direct/Invoke-Command runs the
  guest's built-in Windows PowerShell, not pwsh 7): no ternary (?:), no null-conditional (?.), no
  PS7-only syntax anywhere inside an Invoke-Command -ScriptBlock in this file.

  HOST SAFETY: touches only the VM named -VMName and writes PNGs under -OutDir (host-state, not
  repo content) plus reads -SetupExe (never executed on the host). The only VM credential is the
  documented throwaway autologon 'WckE2E!2026' (disposable, network-isolated guest; not a real
  secret - same value every sandbox\hyperv harness uses).
.PARAMETER SetupExe
  Host path to the compiled WindowsCareKit-Setup-*.exe (from the CI gate-build artifact). Required.
.EXAMPLE
  pwsh -File Invoke-WckInstallerGate.ps1 -SetupExe F:\WCK-VM\gate-build\WindowsCareKit-Setup-v0.0.0-dev-win-x64.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SetupExe,
    [string] $VMName          = 'WCK-E2E',
    [string] $GuestUser       = 'wck',
    # Documented throwaway autologon credential for the disposable, network-isolated WCK-E2E guest
    # (reverted on every checkpoint; no value outside the VM). NOT a real secret.
    [string] $GuestPass       = 'WckE2E!2026',
    [string] $OutDir          = 'F:\WCK-VM\shots\installer-gate',
    [string] $GuestInstaller  = 'C:\WCK-Installer',
    [int]    $ReadyTimeoutMin = 10,
    [int]    $GuestStartupMB  = 2048,
    [int]    $SettleMs        = 2600,     # generous: async LoadAsync / module scan populate
    [int]    $MinHostFreeRamGB = 3,
    [int]    $UninstallTimeoutMin = 5
)
$ErrorActionPreference = 'Stop'
function Step($m){ Write-Host "==> $m" -ForegroundColor Cyan }
function Info($m){ Write-Host "    $m" -ForegroundColor DarkGray }

# Must match installer/WindowsCareKit.iss [Setup] AppId (without the leading '{').
$AppIdGuid = '5CDA29A9-74D6-48D3-A70E-806E22E4A47A'
$AppDir    = 'C:\Program Files\Windows Care Kit'
$ArpKey    = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{$AppIdGuid}_is1"
$LnkPath   = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Windows Care Kit.lnk'

$results = New-Object System.Collections.Generic.List[object]
function Record([string]$name, [bool]$ok, [string]$detail = ''){
    $results.Add([pscustomobject]@{ Check = $name; Ok = $ok; Detail = $detail })
    if ($ok) { Info "PASS  $name" } else { Write-Host "    FAIL  $name  $detail" -ForegroundColor Red }
}

if (-not (Test-Path -LiteralPath $SetupExe -PathType Leaf)) { throw "SetupExe not found: $SetupExe" }
if (-not (Get-Command Get-VM -ErrorAction SilentlyContinue)) { throw "Hyper-V module unavailable." }
try { Get-VM -Name $VMName -ErrorAction Stop | Out-Null }
catch { throw "VM '$VMName' not found / no Hyper-V access. Build it with Build-WckVM.ps1." }

# --- Step 0a: host free-RAM check --------------------------------------------
Step "Checking host free RAM..."
$os = Get-CimInstance Win32_OperatingSystem
$freeGB = [Math]::Round($os.FreePhysicalMemory / 1MB, 2)   # FreePhysicalMemory is reported in KB
Info "host free RAM: $freeGB GB (floor: $MinHostFreeRamGB GB)"
if ($freeGB -lt $MinHostFreeRamGB) {
    throw "Host free RAM ($freeGB GB) is below the $MinHostFreeRamGB GB floor - refusing to start the guest."
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$sec  = ConvertTo-SecureString $GuestPass -AsPlainText -Force
$cred = [System.Management.Automation.PSCredential]::new(".\$GuestUser", $sec)

# --- Step 0b: fit startup RAM to host headroom (must be applied while Off) --
# Same rationale as Invoke-WckRealMigrationCampaign.ps1 / Capture-PopulatedShots.ps1: a large fixed
# startup size can fail to allocate under host memory pressure (0x800705AA); dynamic memory (floor
# already set by Build-WckVM.ps1) boots small and balloons up only if the host has free RAM.
try {
    $vm = Get-VM -Name $VMName
    if ($vm.State -eq 'Off' -and $vm.DynamicMemoryEnabled) {
        $minMB  = [int]($vm.MemoryMinimum / 1MB)
        $target = [Math]::Max($minMB, $GuestStartupMB)
        if ([int]($vm.MemoryStartup / 1MB) -ne $target) {
            Set-VM -Name $VMName -MemoryStartupBytes ($target * 1MB)
            Info "$VMName startup RAM set to $target MB (host headroom fit)"
        }
    }
} catch { Info "could not adjust startup RAM: $($_.Exception.Message)" }

# --- Step 0c: capture-agent bootstrap (starts the VM if needed) -------------
Step "Ensuring capture agent in guest (starts VM if needed)..."
& (Join-Path $PSScriptRoot 'Install-CaptureAgent.ps1') -VMName $VMName -GuestUser $GuestUser -GuestPass $GuestPass

try {
    $session = New-PSSession -VMName $VMName -Credential $cred
    try {
        # --- Step 1: push the installer + elevation precheck --------------------
        Step "Pushing installer to guest $GuestInstaller ..."
        Invoke-Command -Session $session -ArgumentList $GuestInstaller -ScriptBlock {
            param($dst)
            if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
            New-Item -ItemType Directory -Force -Path $dst | Out-Null
        }
        $guestSetupExe = Join-Path $GuestInstaller 'Setup.exe'
        Copy-Item -Path $SetupExe -Destination $guestSetupExe -ToSession $session -Force

        $precheck = Invoke-Command -Session $session -ScriptBlock {
            # WinPS 5.1-safe: no ternary, no null-conditional.
            $wi = [Security.Principal.WindowsIdentity]::GetCurrent()
            $wp = New-Object Security.Principal.WindowsPrincipal($wi)
            $isAdmin = $wp.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
            New-Object PSObject -Property @{ IsAdmin = $isAdmin; UserName = $wi.Name }
        }
        Info "guest session user: $($precheck.UserName); elevated admin token: $($precheck.IsAdmin)"
        if (-not $precheck.IsAdmin) {
            throw "guest PowerShell Direct session is not carrying an elevated admin token - the PrivilegesRequired=admin installer would UAC-prompt and hang non-interactively."
        }

        # --- Step 2: PARTIAL install (Migration UNCHECKED) ----------------------
        Step "Partial install (uninstall,clean,backup,restore,install - Migration UNCHECKED)..."
        $partialLog = Join-Path $GuestInstaller 'partial.log'
        $partial = Invoke-Command -Session $session -ArgumentList $guestSetupExe, $partialLog -ScriptBlock {
            param($setup, $log)
            $argList = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /COMPONENTS="uninstall,clean,backup,restore,install" /LOG="' + $log + '"'
            $p = Start-Process -FilePath $setup -ArgumentList $argList -Wait -PassThru
            New-Object PSObject -Property @{ ExitCode = $p.ExitCode }
        }
        Info "partial install exit code: $($partial.ExitCode)"
        if ($partial.ExitCode -ne 0) { throw "partial install failed with exit code $($partial.ExitCode) (see guest $partialLog)" }

        $moduleDlls = New-Object PSObject -Property @{
            uninstall = 'Suite.Module.Uninstall.dll'
            clean     = 'Suite.Module.Clean.dll'
            backup    = 'Suite.Module.Backup.dll'
            restore   = 'Suite.Module.Restore.dll'
            install   = 'Suite.Module.Install.dll'
        }
        $backupManifests = @(
            '00-ai-tools.json','10-developer.json','20-browser.json','30-games.json','40-system.json',
            '50-notes.json','60-wsl.json','70-general-user.json','80-network-drive.json'
        )
        $assertPartial = Invoke-Command -Session $session -ArgumentList $AppDir, $LnkPath, $moduleDlls, $backupManifests -ScriptBlock {
            param($dir, $lnk, $dlls, $manifests)
            $exeOk = Test-Path (Join-Path $dir 'WindowsCareKit.exe')
            $migDirGone = -not (Test-Path (Join-Path $dir 'Modules\migration'))
            $migDllCount = (Get-ChildItem -Path $dir -Recurse -Filter 'Suite.Module.Migration*.dll' -ErrorAction SilentlyContinue | Measure-Object).Count
            $otherModulesOk = $true
            $otherModulesDetail = @()
            foreach ($id in @('uninstall','clean','backup','restore','install')) {
                $dllName = $dlls.$id
                $p = Join-Path $dir "Modules\$id\$dllName"
                if (-not (Test-Path $p)) { $otherModulesOk = $false; $otherModulesDetail += $p }
            }
            $manifestsOk = $true
            $manifestsDetail = @()
            foreach ($mf in $manifests) {
                $p = Join-Path $dir "manifests\$mf"
                if (-not (Test-Path $p)) { $manifestsOk = $false; $manifestsDetail += $p }
            }
            if (-not (Test-Path (Join-Path $dir 'manifests\90-install.json'))) { $manifestsOk = $false; $manifestsDetail += '90-install.json' }
            $shortcutOk = Test-Path $lnk
            New-Object PSObject -Property @{
                ExeOk = $exeOk; MigDirGone = $migDirGone; MigDllCount = $migDllCount
                OtherModulesOk = $otherModulesOk; OtherModulesDetail = ($otherModulesDetail -join '; ')
                ManifestsOk = $manifestsOk; ManifestsDetail = ($manifestsDetail -join '; ')
                ShortcutOk = $shortcutOk
            }
        }
        Record 'partial: base exe present' $assertPartial.ExeOk
        Record 'partial: Modules\migration absent' $assertPartial.MigDirGone
        Record 'partial: zero Suite.Module.Migration*.dll anywhere' ($assertPartial.MigDllCount -eq 0) "found $($assertPartial.MigDllCount)"
        Record 'partial: other 5 module DLLs present' $assertPartial.OtherModulesOk $assertPartial.OtherModulesDetail
        Record 'partial: 10 manifests laid out' $assertPartial.ManifestsOk $assertPartial.ManifestsDetail
        Record 'partial: Start Menu shortcut present' $assertPartial.ShortcutOk $LnkPath

        # --- Step 3: ACL assert (E-core security check) --------------------------
        Step "Asserting install-dir + Modules\ ACLs (no world-writable Allow ACE)..."
        $badPrincipals = @('BUILTIN\Users', 'NT AUTHORITY\Authenticated Users', 'Everyone', 'BUILTIN\Everyone')
        $acl = Invoke-Command -Session $session -ArgumentList $AppDir, $badPrincipals -ScriptBlock {
            param($dir, $bad)
            $writeMask = [System.Security.AccessControl.FileSystemRights]::Write -bor
                         [System.Security.AccessControl.FileSystemRights]::Modify -bor
                         [System.Security.AccessControl.FileSystemRights]::FullControl -bor
                         [System.Security.AccessControl.FileSystemRights]::CreateFiles -bor
                         [System.Security.AccessControl.FileSystemRights]::AppendData -bor
                         [System.Security.AccessControl.FileSystemRights]::WriteData
            $violations = @()
            foreach ($target in @($dir, (Join-Path $dir 'Modules'))) {
                if (-not (Test-Path $target)) { $violations += "$target : path missing"; continue }
                $acl = Get-Acl -Path $target
                foreach ($ace in $acl.Access) {
                    if ($ace.AccessControlType -ne 'Allow') { continue }
                    $idRef = $ace.IdentityReference.Value
                    $isBad = $false
                    foreach ($bp in $bad) { if ($idRef -ieq $bp) { $isBad = $true } }
                    if (-not $isBad) { continue }
                    if (([int]$ace.FileSystemRights -band [int]$writeMask) -ne 0) {
                        $violations += "$target : $idRef grants $($ace.FileSystemRights)"
                    }
                }
            }
            , $violations
        }
        Record 'ACL: no world-writable Allow ACE on install dir / Modules\' (@($acl).Count -eq 0) (($acl -join '; '))

        # --- Step 4: shots (partial state) ---------------------------------------
        Step "Capturing partial-install shots..."
        $show = Join-Path $PSScriptRoot 'Show-InGuestApp.ps1'
        $exeGuest = Join-Path $AppDir 'WindowsCareKit.exe'
        $shotSpecs = @(
            [pscustomobject]@{ Args = @('--lang','en');                    File = '01-partial-bare.png' }
            [pscustomobject]@{ Args = @('--lang','en','--screen','backup'); File = '02-partial-screen-backup.png' }
        )
        foreach ($spec in $shotSpecs) {
            $out = Join-Path $OutDir $spec.File
            try {
                & $show -Exe $exeGuest -AppArgs $spec.Args -OutPng $out -SettleMs $SettleMs `
                    -VMName $VMName -GuestUser $GuestUser -GuestPassword $sec -DisableProvenanceOverlay | Out-Null
                Record "shot: $($spec.File)" (Test-Path $out) $out
            } catch {
                Record "shot: $($spec.File)" $false $_.Exception.Message
            }
        }

        # --- Step 5: MODIFY (add Migration later, same AppId) --------------------
        Step "Modify install: adding Migration (all 6 components)..."
        $fullLog = Join-Path $GuestInstaller 'full.log'
        $full = Invoke-Command -Session $session -ArgumentList $guestSetupExe, $fullLog -ScriptBlock {
            param($setup, $log)
            $argList = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /COMPONENTS="uninstall,clean,backup,migration,restore,install" /LOG="' + $log + '"'
            $p = Start-Process -FilePath $setup -ArgumentList $argList -Wait -PassThru
            New-Object PSObject -Property @{ ExitCode = $p.ExitCode }
        }
        Info "modify install exit code: $($full.ExitCode)"
        if ($full.ExitCode -ne 0) { throw "modify install failed with exit code $($full.ExitCode) (see guest $fullLog)" }

        $assertFull = Invoke-Command -Session $session -ArgumentList $AppDir -ScriptBlock {
            param($dir)
            $migDir = Join-Path $dir 'Modules\migration'
            $hasCore = Test-Path (Join-Path $migDir 'Suite.Module.Migration.dll')
            $hasRecipes = Test-Path (Join-Path $migDir 'Suite.Module.Migration.Recipes.dll')
            New-Object PSObject -Property @{ HasCore = $hasCore; HasRecipes = $hasRecipes }
        }
        Record 'modify: Modules\migration has Suite.Module.Migration.dll' $assertFull.HasCore
        Record 'modify: Modules\migration has Suite.Module.Migration.Recipes.dll' $assertFull.HasRecipes

        $out3 = Join-Path $OutDir '03-full-bare.png'
        try {
            & $show -Exe $exeGuest -AppArgs @('--lang','en') -OutPng $out3 -SettleMs $SettleMs `
                -VMName $VMName -GuestUser $GuestUser -GuestPassword $sec -DisableProvenanceOverlay | Out-Null
            Record 'shot: 03-full-bare.png' (Test-Path $out3) $out3
        } catch {
            Record 'shot: 03-full-bare.png' $false $_.Exception.Message
        }

        # --- Step 6: UNINSTALL -----------------------------------------------------
        Step "Uninstalling (QuietUninstallString from the ARP key)..."
        Invoke-Command -Session $session -ArgumentList $ArpKey, $AppDir -ScriptBlock {
            param($regPath, $dir)
            if (-not (Test-Path $regPath)) { throw "ARP key not found before uninstall: $regPath" }
            $prop = Get-ItemProperty -Path $regPath -Name QuietUninstallString -ErrorAction Stop
            $quiet = $prop.QuietUninstallString
            if ([string]::IsNullOrWhiteSpace($quiet)) { throw "QuietUninstallString is empty at $regPath" }
            $exePath = $null
            $exeArgs = ''
            if ($quiet -match '^"([^"]+)"\s*(.*)$') {
                $exePath = $Matches[1]
                $exeArgs = $Matches[2]
            } else {
                $parts = $quiet -split ' ', 2
                $exePath = $parts[0]
                if ($parts.Count -gt 1) { $exeArgs = $parts[1] }
            }
            if ($exeArgs) { Start-Process -FilePath $exePath -ArgumentList $exeArgs -PassThru | Out-Null }
            else { Start-Process -FilePath $exePath -PassThru | Out-Null }
        }
        # Inno's silent uninstaller relaunches itself from a temp copy and the launcher process
        # exits early, so -Wait on the launch is not trustworthy - poll for the install dir to go.
        $deadline = (Get-Date).AddMinutes($UninstallTimeoutMin)
        $gone = $false
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 3
            $stillThere = Invoke-Command -Session $session -ArgumentList $AppDir -ScriptBlock { param($d) Test-Path $d }
            if (-not $stillThere) { $gone = $true; break }
        }
        if (-not $gone) { Info "install dir did not disappear within $UninstallTimeoutMin min; asserting current state anyway." }

        $assertUninstall = Invoke-Command -Session $session -ArgumentList $AppDir, $ArpKey, $LnkPath -ScriptBlock {
            param($dir, $regPath, $lnk)
            New-Object PSObject -Property @{
                DirGone = -not (Test-Path $dir)
                ArpGone = -not (Test-Path $regPath)
                ShortcutGone = -not (Test-Path $lnk)
            }
        }
        Record 'uninstall: install dir gone' $assertUninstall.DirGone $AppDir
        Record 'uninstall: ARP key gone' $assertUninstall.ArpGone $ArpKey
        Record 'uninstall: Start Menu shortcut gone' $assertUninstall.ShortcutGone $LnkPath

    } finally {
        Remove-PSSession $session -ErrorAction SilentlyContinue
    }

    Write-Host ''
    Step "Installer gate summary"
    $results | Format-Table -AutoSize
    $fail = @($results | Where-Object { -not $_.Ok }).Count
    if ($fail -gt 0) { Write-Host "$fail check(s) FAILED - see above." -ForegroundColor Red }
    else { Write-Host "All $($results.Count) checks PASSED." -ForegroundColor Green }
}
finally {
    Step "Stopping $VMName (VM -> Off)..."
    try {
        if ((Get-VM -Name $VMName).State -ne 'Off') { Stop-VM -Name $VMName -Force -TurnOff }
    } catch { Write-Host "    cleanup warning: $($_.Exception.Message)" -ForegroundColor Yellow }
}

if (@($results | Where-Object { -not $_.Ok }).Count -gt 0) { exit 1 }
exit 0
