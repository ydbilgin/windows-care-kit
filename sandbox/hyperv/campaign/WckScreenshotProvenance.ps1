#requires -Version 7.0

Set-StrictMode -Version Latest

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
