#requires -Version 7.0
<#
.SYNOPSIS
  Whole-DESKTOP screenshot of a Hyper-V guest via the WMI console thumbnail (fallback).
.DESCRIPTION
  Headless, no GUI: reads the VM's console framebuffer through
  Msvm_VirtualSystemManagementService.GetVirtualSystemThumbnailImage (RGB565) and saves a PNG.
  This is the FALLBACK / whole-desktop view — for crisp, watermark-free, per-window README shots
  prefer the in-guest capture (Show-InGuestApp.ps1). RGB565 = 16-bit (visible banding); the API
  returns w*h*2 (+a few trailer bytes) — we copy exactly w*h*2 and recompute per requested size.
#>
[CmdletBinding()]
param(
    [string] $VMName = 'WCK-E2E',
    [Parameter(Mandatory)] [string] $OutPng,
    [int] $Width = 1024,
    [int] $Height = 768
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$need = $Width * $Height * 2
$vm  = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_ComputerSystem -Filter "ElementName='$VMName'"
if(-not $vm){ throw "VM '$VMName' not found" }
$svc = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_VirtualSystemManagementService
$settings = Get-CimAssociatedInstance -InputObject $vm -ResultClassName Msvm_VirtualSystemSettingData -Association Msvm_SettingsDefineState |
            Where-Object { $_.VirtualSystemType -like '*Realized*' } | Select-Object -First 1
$res = Invoke-CimMethod -InputObject $svc -MethodName GetVirtualSystemThumbnailImage -Arguments @{
    TargetSystem=$settings; WidthPixels=[uint16]$Width; HeightPixels=[uint16]$Height }
$b = $res.ImageData
if($null -eq $b -or $b.Length -lt $need){ throw "thumbnail unavailable (got $($b.Length) bytes, need $need; is the VM running with a framebuffer?)" }
$bmp = New-Object System.Drawing.Bitmap($Width,$Height,[System.Drawing.Imaging.PixelFormat]::Format16bppRgb565)
$rect = New-Object System.Drawing.Rectangle(0,0,$Width,$Height)
$d = $bmp.LockBits($rect,[System.Drawing.Imaging.ImageLockMode]::WriteOnly,[System.Drawing.Imaging.PixelFormat]::Format16bppRgb565)
# GDI rows can be stride-padded; only bulk-copy when stride == width*2 (true for even widths like 1024),
# otherwise copy row-by-row so an arbitrary width never skews the image.
$rowBytes = $Width * 2
if([Math]::Abs($d.Stride) -eq $rowBytes){
  [System.Runtime.InteropServices.Marshal]::Copy($b,0,$d.Scan0,$need)   # exact w*h*2; ignore trailer
} else {
  for($row=0; $row -lt $Height; $row++){
    [System.Runtime.InteropServices.Marshal]::Copy($b, $row*$rowBytes, [IntPtr]($d.Scan0.ToInt64() + $row*$d.Stride), $rowBytes)
  }
}
$bmp.UnlockBits($d)
$dir = Split-Path $OutPng -Parent; if($dir -and -not(Test-Path $dir)){ New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$bmp.Save($OutPng,[System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
Write-Host "saved $Width x $Height -> $OutPng"
