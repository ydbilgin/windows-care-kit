#requires -Version 7.0
<#
.SYNOPSIS
  Launch a GUI app in the WCK-E2E guest and capture a 24-bit PNG of its window (headless).
.DESCRIPTION
  Requires the resident capture agent (Install-CaptureAgent.ps1). Writes a request into the
  in-guest queue over PowerShell Direct; the agent (running in the autologon Session 1)
  launches the app so it renders, captures JUST the window (no desktop/eval watermark), and
  writes a result the host polls for. The PNG is pulled back to -OutPng on the host.
.EXAMPLE
  pwsh -File Show-InGuestApp.ps1 -Exe C:\Windows\System32\notepad.exe -OutPng F:\WCK-VM\shots\notepad.png
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Exe,
    [string[]] $AppArgs = @(),
    [string] $WorkDir,
    [Parameter(Mandatory)] [string] $OutPng,
    [ValidateSet('launchcapture','launch')] [string] $Op = 'launchcapture',   # capture/close ops exist in the agent; wrapper exposure deferred to T3
    [int] $TimeoutSec = 40,
    [int] $SettleMs = 600,   # post-window settle before capture; raise for apps with async content load (e.g. WCK's LoadAsync)
    [string] $VMName = 'WCK-E2E',
    [string] $GuestUser = 'wck',
    # Throwaway autologon credential for the disposable, network-isolated WCK-E2E guest — NOT a real secret. Override with -GuestPass.
    [string] $GuestPass = 'WckE2E!2026',
    [switch] $KeepOpen   # leave the app running (default closes it after capture)
)
$ErrorActionPreference = 'Stop'
function Step($m){ Write-Host "==> $m" -ForegroundColor Cyan }

$sec  = ConvertTo-SecureString $GuestPass -AsPlainText -Force
$cred = New-Object System.Management.Automation.PSCredential(".\$GuestUser", $sec)
$id   = 'cap-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')

$session = New-PSSession -VMName $VMName -Credential $cred
try {
    # agent alive AND fresh? (a dead agent leaves a stale heartbeat file; existence alone is not enough)
    $alive = Invoke-Command -Session $session -ScriptBlock {
        if(Test-Path 'C:\WCK-Cap\agent-alive.txt'){ (Get-Item 'C:\WCK-Cap\agent-alive.txt').LastWriteTimeUtc.ToString('o') }
    }
    if(-not $alive){ throw "capture agent not running in guest. Run Install-CaptureAgent.ps1 first." }
    $ageSec = ((Get-Date).ToUniversalTime() - [datetime]::Parse($alive).ToUniversalTime()).TotalSeconds
    if($ageSec -gt 15 -or $ageSec -lt -5){ throw "capture agent heartbeat not fresh ($([int]$ageSec)s; stale or clock-skewed) — re-run Install-CaptureAgent.ps1." }

    Step "Queuing $Op '$Exe' (id=$id)..."
    Invoke-Command -Session $session -ArgumentList $id,$Op,$Exe,$AppArgs,$WorkDir,$TimeoutSec,$SettleMs -ScriptBlock {
        param($id,$op,$exe,$appArgs,$workdir,$to,$settleMs)
        $obj = [ordered]@{ id=$id; op=$op; exe=$exe; timeoutSec=$to; settleMs=$settleMs }
        if($appArgs){ $obj.args=@($appArgs) }; if($workdir){ $obj.workdir=$workdir }   # array end-to-end (multi-arg safe)
        $json = $obj | ConvertTo-Json -Compress
        $tmp = "C:\WCK-Cap\requests\$id.tmp"; $fin = "C:\WCK-Cap\requests\$id.req.json"
        Set-Content -LiteralPath $tmp -Value $json -Encoding utf8
        Move-Item -LiteralPath $tmp -Destination $fin -Force   # atomic: agent only sees the final name
    }

    Step "Waiting for result..."
    $deadline=(Get-Date).AddSeconds($TimeoutSec+25); $result=$null
    while((Get-Date) -lt $deadline){
        Start-Sleep -Milliseconds 600
        $j = Invoke-Command -Session $session -ArgumentList $id -ScriptBlock { param($id) $p="C:\WCK-Cap\results\$id.json"; if(Test-Path $p){ Get-Content $p -Raw } }
        if($j){ $result = $j | ConvertFrom-Json; break }
    }
    if(-not $result){ throw "no result within timeout (check C:\WCK-Cap\agent.log in guest)" }
    if(-not $result.ok){ throw "agent reported failure: $($result.message)" }

    if($Op -in 'launchcapture','capture'){
        $dir = Split-Path $OutPng -Parent; if($dir -and -not (Test-Path $dir)){ New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        Copy-Item -Path "C:\WCK-Cap\out\$id.png" -Destination $OutPng -FromSession $session -Force
        Step "Captured $($result.width)x$($result.height) -> $OutPng"
        if(-not $KeepOpen -and $result.pid){
            Invoke-Command -Session $session -ArgumentList $result.pid -ScriptBlock { param($p) Stop-Process -Id $p -Force -EA SilentlyContinue }
        }
    }
    $result
} finally { Remove-PSSession $session -EA SilentlyContinue }
