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
    [securestring] $GuestPassword,
    [string] $Persona = 'unknown',
    [string] $Scenario = 'manual',
    [string] $ProvenanceNonce,
    [switch] $DisableProvenanceOverlay,
    [switch] $KeepOpen   # leave the app running (default closes it after capture)
)
$ErrorActionPreference = 'Stop'
function Step($m){ Write-Host "==> $m" -ForegroundColor Cyan }

function New-WckShowGuestCredential {
    [CmdletBinding()]
    param([string] $GuestUser, [securestring] $GuestPassword)

    if ($GuestPassword) {
        return [System.Management.Automation.PSCredential]::new(".\$GuestUser", $GuestPassword)
    }
    if ([string]::IsNullOrEmpty($env:WCK_GUEST_CRED)) {
        throw "Guest credential required: provide -GuestPassword (SecureString) or env:WCK_GUEST_CRED; refusing to use a script-literal default."
    }
    $sec = ConvertTo-SecureString $env:WCK_GUEST_CRED -AsPlainText -Force
    return [System.Management.Automation.PSCredential]::new(".\$GuestUser", $sec)
}

function New-WckCaptureProvenanceText {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Hostname,
        [Parameter(Mandatory)] [string] $Persona,
        [Parameter(Mandatory)] [string] $Scenario,
        [Parameter(Mandatory)] [string] $Nonce
    )

    if ([string]::IsNullOrWhiteSpace($Nonce)) { throw "provenance nonce is required" }
    $utc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    return "$Hostname|$Persona|$Scenario|$utc|$Nonce"
}

function Add-WckPngTextOverlay {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Text
    )

    Add-Type -AssemblyName System.Drawing
    $bitmap = [System.Drawing.Bitmap]::FromFile($Path)
    try {
        $copy = [System.Drawing.Bitmap]::new($bitmap.Width, $bitmap.Height, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
        $g = [System.Drawing.Graphics]::FromImage($copy)
        try {
            $g.DrawImageUnscaled($bitmap, 0, 0)
            $font = [System.Drawing.Font]::new('Consolas', 12, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            $size = $g.MeasureString($Text, $font)
            $pad = 6
            $rect = [System.Drawing.RectangleF]::new(0, 0, [Math]::Min($copy.Width, $size.Width + ($pad * 2)), $size.Height + ($pad * 2))
            $brush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(220, 0, 0, 0))
            $textBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
            try {
                $g.FillRectangle($brush, $rect)
                $g.DrawString($Text, $font, $textBrush, $pad, $pad)
            }
            finally {
                $brush.Dispose()
                $textBrush.Dispose()
                $font.Dispose()
            }
        }
        finally {
            $g.Dispose()
            $bitmap.Dispose()
        }
        $tmp = "$Path.overlay.tmp"
        $copy.Save($tmp, [System.Drawing.Imaging.ImageFormat]::Png)
        $copy.Dispose()
        Move-Item -LiteralPath $tmp -Destination $Path -Force
        return [pscustomobject]@{ Path = $Path; OverlayText = $Text }
    }
    catch {
        if ($bitmap) { $bitmap.Dispose() }
        throw
    }
}

$cred = New-WckShowGuestCredential -GuestUser $GuestUser -GuestPassword $GuestPassword
$id   = 'cap-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
if ([string]::IsNullOrWhiteSpace($ProvenanceNonce)) { $ProvenanceNonce = [guid]::NewGuid().ToString('N') }

$session = New-PSSession -VMName $VMName -Credential $cred
try {
    if (@($session).Count -ne 1) { throw "session-count guard failed: expected exactly one PowerShell Direct session, got $(@($session).Count)" }

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
        if (-not $DisableProvenanceOverlay) {
            $overlayText = New-WckCaptureProvenanceText -Hostname $VMName -Persona $Persona -Scenario $Scenario -Nonce $ProvenanceNonce
            $null = Add-WckPngTextOverlay -Path $OutPng -Text $overlayText
            $result | Add-Member -NotePropertyName provenanceOverlay -NotePropertyValue $overlayText -Force
            $result | Add-Member -NotePropertyName provenanceNonce -NotePropertyValue $ProvenanceNonce -Force
        }
        Step "Captured $($result.width)x$($result.height) -> $OutPng"
        if(-not $KeepOpen -and $result.pid){
            Invoke-Command -Session $session -ArgumentList $result.pid -ScriptBlock { param($p) Stop-Process -Id $p -Force -EA SilentlyContinue }
        }
    }
    $result
} finally { Remove-PSSession $session -EA SilentlyContinue }
