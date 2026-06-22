#requires -Version 7.0
<#
.SYNOPSIS
  Install the resident capture agent into the WCK-E2E guest (one-time; bake into baseline).
.DESCRIPTION
  Pushes Wck.CaptureAgent.ps1 into the guest over PowerShell Direct, registers it as an
  ONLOGON, INTERACTIVE (/IT), '.\wck', Limited scheduled task so it auto-starts in the
  autologon Session 1 each boot, and starts it now (no re-logon needed). After this,
  re-checkpoint 'baseline-clean' so the agent is permanent. Needs Hyper-V access (elevated
  or Hyper-V Administrators).
#>
[CmdletBinding()]
param(
    [string] $VMName = 'WCK-E2E',
    [string] $GuestUser = 'wck',
    # Throwaway autologon credential for the disposable, network-isolated WCK-E2E guest — reverted on
    # every checkpoint, no network-exposed service, no value outside the VM. NOT a real secret. Override with -GuestPass.
    [string] $GuestPass = 'WckE2E!2026',
    [switch] $Recheckpoint   # also refresh baseline-clean so the agent persists
)
$ErrorActionPreference = 'Stop'
function Step($m){ Write-Host "==> $m" -ForegroundColor Cyan }
function Info($m){ Write-Host "    $m" -ForegroundColor DarkGray }

$sec  = ConvertTo-SecureString $GuestPass -AsPlainText -Force
$cred = New-Object System.Management.Automation.PSCredential(".\$GuestUser", $sec)
$agentSrc = Join-Path $PSScriptRoot 'guest-agent\Wck.CaptureAgent.ps1'
if (-not (Test-Path $agentSrc)) { throw "agent script not found: $agentSrc" }

if ((Get-VM $VMName).State -ne 'Running') { Step "Starting VM..."; Start-VM $VMName }
Step "Waiting for PowerShell Direct..."
$deadline=(Get-Date).AddMinutes(8); $ok=$false
while((Get-Date) -lt $deadline){ Start-Sleep 8; try{ if(Invoke-Command -VMName $VMName -Credential $cred -ScriptBlock { Test-Path 'C:\wck-ready.txt' } -EA Stop){ $ok=$true;break } }catch{} }
if(-not $ok){ throw "guest not reachable" }

# Reviewer guard: confirm exactly one interactive console session for the guest user.
Step "Verifying single interactive '$GuestUser' console session..."
$sessLines = Invoke-Command -VMName $VMName -Credential $cred -ScriptBlock { (quser) 2>$null }
Info ("quser:`n      " + (($sessLines | Where-Object {$_ -match $GuestUser}) -join "`n      "))
$active = $sessLines | Where-Object { $_ -match "(?i)\b$GuestUser\b" -and $_ -match '(?i)\bActive\b' }
if (($active | Measure-Object).Count -ne 1) {
    Write-Host "    WARNING: expected exactly one ACTIVE '$GuestUser' session; got $(($active|Measure-Object).Count). Proceeding but capture may target the wrong desktop." -ForegroundColor Yellow
}

$session = New-PSSession -VMName $VMName -Credential $cred
try {
    Step "Pushing agent + creating queue dirs..."
    Invoke-Command -Session $session -ScriptBlock {
        New-Item -ItemType Directory -Force -Path 'C:\WCK-Cap','C:\WCK-Cap\requests','C:\WCK-Cap\out','C:\WCK-Cap\results' | Out-Null
    }
    Copy-Item -Path $agentSrc -Destination 'C:\WCK-Cap\Wck.CaptureAgent.ps1' -ToSession $session -Force

    Step "Registering ONLOGON interactive task + starting agent now..."
    Invoke-Command -Session $session -ArgumentList $GuestUser -ScriptBlock {
        param($gu)
        $tn='\WCK\CaptureAgent'
        $ru="$env:COMPUTERNAME\$gu"   # canonical local-account form; '.\user' is not resolved by schtasks
        $tr='powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "C:\WCK-Cap\Wck.CaptureAgent.ps1"'
        # kill any previous agent so a redeploy never leaves two agents racing the same queue
        Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object { $_.CommandLine -like '*Wck.CaptureAgent*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        schtasks /Delete /TN $tn /F *>$null
        # ONLOGON + /IT (interactive) + Limited integrity = renders in the autologon Session 1
        schtasks /Create /TN $tn /SC ONLOGON /RU $ru /IT /RL LIMITED /TR $tr /F | Out-Null
        if($LASTEXITCODE -ne 0){ throw "schtasks /Create failed (exit $LASTEXITCODE)" }
        schtasks /Run /TN $tn | Out-Null
        if($LASTEXITCODE -ne 0){ throw "schtasks /Run failed (exit $LASTEXITCODE)" }
    }

    Step "Waiting for a FRESH agent heartbeat..."
    $hb=$false; $d2=(Get-Date).AddSeconds(40)
    while((Get-Date) -lt $d2){
        Start-Sleep 3
        $alive = Invoke-Command -Session $session -ScriptBlock { if(Test-Path 'C:\WCK-Cap\agent-alive.txt'){ (Get-Item 'C:\WCK-Cap\agent-alive.txt').LastWriteTimeUtc.ToString('o') } }
        if($alive){
            $age = ((Get-Date).ToUniversalTime() - [datetime]::Parse($alive).ToUniversalTime()).TotalSeconds
            if($age -lt 15 -and $age -gt -5){ Info "agent alive (heartbeat $([int]$age)s old)"; $hb=$true; break }   # RECENT (and not future/clock-skewed)
        }
    }
    if(-not $hb){ throw "agent did not report a fresh heartbeat (check C:\WCK-Cap\agent.log in guest)" }
} finally { Remove-PSSession $session -EA SilentlyContinue }

Step "Capture agent INSTALLED and running."
if ($Recheckpoint) {
    Step "Refreshing 'baseline-clean' (create-new-then-replace so a failure never loses the baseline)..."
    Get-VMCheckpoint -VMName $VMName -Name 'baseline-clean-new' -EA SilentlyContinue | Remove-VMCheckpoint -Confirm:$false
    Checkpoint-VM -Name $VMName -SnapshotName 'baseline-clean-new'
    if(-not (Get-VMCheckpoint -VMName $VMName -Name 'baseline-clean-new' -EA SilentlyContinue)){ throw "new checkpoint creation failed; existing 'baseline-clean' left intact." }
    Get-VMCheckpoint -VMName $VMName -Name 'baseline-clean' -EA SilentlyContinue | Remove-VMCheckpoint -Confirm:$false
    Rename-VMCheckpoint -VMName $VMName -Name 'baseline-clean-new' -NewName 'baseline-clean'
    Step "baseline-clean refreshed (now includes the agent)."
}
