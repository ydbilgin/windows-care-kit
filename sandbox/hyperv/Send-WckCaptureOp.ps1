#requires -Version 7.0
<#
.SYNOPSIS
  Low-level: send ONE request to the resident WCK capture agent's queue (over an existing
  PowerShell Direct session) and wait for its result.
.DESCRIPTION
  Generalizes the request/poll plumbing in Show-InGuestApp.ps1 to also drive the agent's
  UI-automation ops (settext / invoke), added so a screenshot can be taken of the app AFTER a
  simulated user interaction — several WCK modules (Backup, Clean, Install) only populate their
  preview lists after an explicit button click (they are not auto-scanning like Migration/
  Uninstall), so a plain launch+capture always shows the pre-click EMPTY state.

  This script does NOT reimplement screenshot capture: the PrintWindow/PNG logic lives entirely
  in the resident agent (guest-agent\Wck.CaptureAgent.ps1), reused verbatim; this is only a
  thinner, op-agnostic version of the request-write / result-poll loop that Show-InGuestApp.ps1
  already uses for 'launch'/'launchcapture', extended to also expose the agent's 'capture' (by
  pid), 'settext', 'invoke', and 'close' ops that Show-InGuestApp.ps1 does not surface.

  Show-InGuestApp.ps1 itself is left untouched (its existing callers — Capture-ReadmeShots.ps1,
  Capture-ThemeShots.ps1 — keep their exact current contract).
.EXAMPLE
  $s = New-PSSession -VMName WCK-E2E -Credential $cred
  $launch = & .\Send-WckCaptureOp.ps1 -Session $s -Op launch -Exe $exe -AppArgs @('--lang','en','--screen','backup')
  & .\Send-WckCaptureOp.ps1 -Session $s -Op settext -TargetPid $launch.pid -Text 'C:\WCK-BackupOut'
  & .\Send-WckCaptureOp.ps1 -Session $s -Op invoke  -TargetPid $launch.pid -ButtonName 'Build backup plan'
  & .\Send-WckCaptureOp.ps1 -Session $s -Op capture -TargetPid $launch.pid -OutPng 'F:\WCK-VM\shots\populated\03-backup.png'
  & .\Send-WckCaptureOp.ps1 -Session $s -Op close   -TargetPid $launch.pid
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [System.Management.Automation.Runspaces.PSSession] $Session,
    [Parameter(Mandatory)] [ValidateSet('launch', 'capture', 'settext', 'invoke', 'close')] [string] $Op,
    [string] $Exe,
    [string[]] $AppArgs = @(),
    [string] $WorkDir,
    [int] $TargetPid,
    [string] $Text,
    [string] $ButtonName,
    [string] $OutPng,
    [int] $TimeoutSec = 40,
    [int] $SettleMs = 900
)
$ErrorActionPreference = 'Stop'

if ($Op -eq 'launch' -and [string]::IsNullOrWhiteSpace($Exe)) { throw "Send-WckCaptureOp: -Exe is required for -Op launch." }
if ($Op -in 'capture', 'settext', 'invoke', 'close' -and $TargetPid -le 0) { throw "Send-WckCaptureOp: -TargetPid is required for -Op $Op." }
if ($Op -eq 'settext' -and [string]::IsNullOrEmpty($Text)) { throw "Send-WckCaptureOp: -Text is required for -Op settext." }
if ($Op -eq 'invoke' -and [string]::IsNullOrWhiteSpace($ButtonName)) { throw "Send-WckCaptureOp: -ButtonName is required for -Op invoke." }
if ($Op -eq 'capture' -and [string]::IsNullOrWhiteSpace($OutPng)) { throw "Send-WckCaptureOp: -OutPng is required for -Op capture." }

# agent alive AND fresh (same contract Show-InGuestApp.ps1 enforces).
$alive = Invoke-Command -Session $Session -ScriptBlock {
    if (Test-Path 'C:\WCK-Cap\agent-alive.txt') { (Get-Item 'C:\WCK-Cap\agent-alive.txt').LastWriteTimeUtc.ToString('o') }
}
if (-not $alive) { throw "Send-WckCaptureOp: capture agent not running in guest. Run Install-CaptureAgent.ps1 first." }
$ageSec = ((Get-Date).ToUniversalTime() - [datetime]::Parse($alive).ToUniversalTime()).TotalSeconds
if ($ageSec -gt 15 -or $ageSec -lt -5) { throw "Send-WckCaptureOp: capture agent heartbeat not fresh (${ageSec}s) — re-run Install-CaptureAgent.ps1." }

$id = 'op-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + $Op
Invoke-Command -Session $Session -ArgumentList $id, $Op, $Exe, $AppArgs, $WorkDir, $TargetPid, $Text, $ButtonName, $TimeoutSec, $SettleMs -ScriptBlock {
    param($id, $op, $exe, $appArgs, $workdir, $targetPid, $text, $buttonName, $to, $settleMs)
    $obj = [ordered]@{ id = $id; op = $op; timeoutSec = $to; settleMs = $settleMs }
    if ($exe) { $obj.exe = $exe }
    if ($appArgs) { $obj.args = @($appArgs) }        # array end-to-end (multi-arg safe)
    if ($workdir) { $obj.workdir = $workdir }
    if ($targetPid -gt 0) { $obj.pid = $targetPid }
    if ($null -ne $text) { $obj.text = $text }
    if ($buttonName) { $obj.name = $buttonName }
    $json = $obj | ConvertTo-Json -Compress
    $tmp = "C:\WCK-Cap\requests\$id.tmp"; $fin = "C:\WCK-Cap\requests\$id.req.json"
    Set-Content -LiteralPath $tmp -Value $json -Encoding utf8
    Move-Item -LiteralPath $tmp -Destination $fin -Force   # atomic: agent only sees the final name
} | Out-Null

$deadline = (Get-Date).AddSeconds($TimeoutSec + 25); $result = $null
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 600
    $j = Invoke-Command -Session $Session -ArgumentList $id -ScriptBlock {
        param($id) $p = "C:\WCK-Cap\results\$id.json"; if (Test-Path $p) { Get-Content -LiteralPath $p -Raw }
    }
    if ($j) { $result = $j | ConvertFrom-Json; break }
}
if (-not $result) { throw "Send-WckCaptureOp: no result within timeout for op '$Op' (id=$id; check C:\WCK-Cap\agent.log in guest)." }
if (-not $result.ok) { throw "Send-WckCaptureOp: agent reported failure for op '$Op' (id=$id): $($result.message)" }

if ($Op -eq 'capture' -and $OutPng) {
    $dir = Split-Path $OutPng -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Copy-Item -Path "C:\WCK-Cap\out\$id.png" -Destination $OutPng -FromSession $Session -Force
}

$result
