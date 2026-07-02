<#
  Wck.CaptureAgent.ps1 — resident in-guest capture agent (runs in the autologon
  Session 1 / real window station, started once per boot by an ONLOGON interactive
  scheduled task as .\wck). Driven from the HOST over PowerShell Direct by writing
  request files into the queue; the agent launches GUI apps (so they render on the
  console), captures an opaque 24-bit PNG of JUST the target window (DWM visible
  bounds, no desktop/eval watermark), and writes a result sentinel the host polls for.

  Queue layout (all under C:\WCK-Cap):
    requests\<id>.req.json   host writes:  { id, op, exe, args(array), workdir, pid, match, timeoutSec }
    out\<id>.png             agent writes the captured window image (opaque 24bpp)
    results\<id>.json        agent writes (atomically): { id, ok, pid, hwnd, width, height, message }
    agent-alive.txt          heartbeat (UTC, every loop) — host checks FRESHNESS, not mere existence

  ops: launch | capture | launchcapture | close | settext | invoke

  settext/invoke (added for POPULATED-state screenshots — Backup/Clean/Install only render
  content after an explicit user action: type a folder path, click "Scan"/"Load"/"Build
  plan"): both target the SAME window as a prior launch, resolved fresh by process-tree pid
  (never a cached handle, mirroring 'capture'). settext finds the first UIA Edit control
  (WPF TextBox) and ValuePattern.SetValue(text); invoke finds a UIA Button whose Name equals
  -name (exact match; retries while the window settles) and Invoke()s it. Both are plain
  managed UI Automation client calls (no COM apartment requirement observed for
  Invoke/ValuePattern on a resident MTA host process); if that ever proves flaky the fix is
  to launch this scheduled task with `powershell.exe -sta`.

  Robustness (per kaizen review): resolves the target window by enumerating visible
  titled top-level windows owned by the launched PROCESS TREE and picking the largest
  (not a cached MainWindowHandle / launch PID) — adequate for conventional single-window
  apps incl. WCK's WPF MainWindow. NOTE: it does NOT specially handle splash-then-main
  hand-off or single-instance forwarding to a pre-existing process (out of scope; current
  targets don't use them). PrintWindow(PW_RENDERFULLCONTENT) with retry + black-fraction
  blank check (WPF can return black on the first call) + CopyFromScreen fallback; a
  configurable post-window settle (settleMs) rides out async content load; atomic publish.
#>
[CmdletBinding()]
param([string]$Root = 'C:\WCK-Cap')

$ErrorActionPreference = 'Continue'   # a resident agent must survive transient errors, not exit
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class WckCap {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int  GetWindowTextLength(IntPtr h);
  [DllImport("user32.dll")] public static extern int  GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("dwmapi.dll")] public static extern int  DwmGetWindowAttribute(IntPtr h, int attr, out RECT v, int cb);
  // enumerate visible, titled, non-zero-size top-level windows -> "hwnd;pid;w;h;title"
  public static List<string> Tops() {
    var outl = new List<string>();
    EnumWindows((h, l) => {
      if (!IsWindowVisible(h)) return true;
      if (GetWindowTextLength(h) == 0) return true;
      uint pid; GetWindowThreadProcessId(h, out pid);
      RECT r; GetWindowRect(h, out r);
      int w = r.Right - r.Left, ht = r.Bottom - r.Top;
      if (w <= 0 || ht <= 0) return true;
      var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
      outl.Add(h.ToInt64() + ";" + pid + ";" + w + ";" + ht + ";" + sb.ToString());
      return true;
    }, IntPtr.Zero);
    return outl;
  }
}
'@

$req = Join-Path $Root 'requests'; $out = Join-Path $Root 'out'
$resd = Join-Path $Root 'results'; $logf = Join-Path $Root 'agent.log'
$null = New-Item -ItemType Directory -Force -Path $req,$out,$resd
function Log($m){ "$([DateTime]::UtcNow.ToString('s'))Z $m" | Add-Content -LiteralPath $logf }

function Get-ProcTree([int]$root){
  $parent = @{}; Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | ForEach-Object { $parent[[int]$_.ProcessId] = [int]$_.ParentProcessId }
  $set = [System.Collections.Generic.HashSet[int]]::new(); [void]$set.Add($root)
  $changed = $true
  while($changed){ $changed=$false; foreach($k in @($parent.Keys)){ if($parent[$k] -and $set.Contains($parent[$k]) -and -not $set.Contains($k)){ [void]$set.Add($k); $changed=$true } } }
  return ,$set   # comma wrap: stop PowerShell unrolling the HashSet to its elements on return
}
# Resolve target window: largest visible titled top-level window owned by the launch process TREE.
function Resolve-Window([int]$launchPid,[int]$timeoutSec){
  $end=[DateTime]::UtcNow.AddSeconds($timeoutSec)
  while([DateTime]::UtcNow -lt $end){
    $tree = Get-ProcTree $launchPid
    $cands=@()
    foreach($line in [WckCap]::Tops()){
      $p = $line -split ';',5
      if($tree.Contains([int]$p[1])){ $cands += [pscustomobject]@{ h=[IntPtr][int64]$p[0]; opid=[int]$p[1]; area=([int]$p[2]*[int]$p[3]); title=$p[4] } }
    }
    if($cands){ return ($cands | Sort-Object area -Descending | Select-Object -First 1) }
    Start-Sleep -Milliseconds 400
  }
  throw "no visible top-level window in process tree of pid $launchPid within ${timeoutSec}s"
}
function Find-WindowByTitle([string]$match){
  foreach($line in [WckCap]::Tops()){ $p=$line -split ';',5; if($p[4] -like "*$match*"){ return [pscustomobject]@{ h=[IntPtr][int64]$p[0]; opid=[int]$p[1] } } }
  return $null
}
# --- UI Automation helpers (settext/invoke ops) -----------------------------------------
# Both re-resolve the window fresh from [h] (caller already re-ran Resolve-Window by pid) so
# they never depend on a stale AutomationElement across requests.
function Set-UiaEditText([IntPtr]$h,[string]$text,[int]$timeoutSec){
  $root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
  if(-not $root){ throw "settext: AutomationElement.FromHandle returned null" }
  $cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Edit)
  $edit=$null; $end=[DateTime]::UtcNow.AddSeconds($timeoutSec)
  while(-not $edit -and [DateTime]::UtcNow -lt $end){
    $edit = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$cond)
    if(-not $edit){ Start-Sleep -Milliseconds 300 }
  }
  if(-not $edit){ throw "settext: no Edit (TextBox) control found in target window" }
  $pat = $null
  if(-not $edit.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern,[ref]$pat)){
    throw "settext: target Edit control does not support ValuePattern"
  }
  $pat.SetValue($text)
}
function Invoke-UiaButton([IntPtr]$h,[string]$name,[int]$timeoutSec){
  $root = [System.Windows.Automation.AutomationElement]::FromHandle($h)
  if(-not $root){ throw "invoke: AutomationElement.FromHandle returned null" }
  $cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)
  $target=$null; $end=[DateTime]::UtcNow.AddSeconds($timeoutSec)
  while(-not $target -and [DateTime]::UtcNow -lt $end){
    $buttons = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,$cond)
    foreach($b in $buttons){ if($b.Current.Name -eq $name){ $target=$b; break } }
    if(-not $target){ Start-Sleep -Milliseconds 300 }
  }
  if(-not $target){ throw "invoke: no Button named '$name' found in target window" }
  $pat = $null
  if(-not $target.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern,[ref]$pat)){
    throw "invoke: button '$name' does not support InvokePattern"
  }
  $pat.Invoke()
}
function Get-RectDwm([IntPtr]$h){
  $r=New-Object WckCap+RECT
  $cb=[System.Runtime.InteropServices.Marshal]::SizeOf([type]([WckCap+RECT]))
  if([WckCap]::DwmGetWindowAttribute($h,9,[ref]$r,$cb) -ne 0){ [WckCap]::GetWindowRect($h,[ref]$r)|Out-Null }  # 9=DWMWA_EXTENDED_FRAME_BOUNDS
  return $r
}
function Test-MostlyBlack([System.Drawing.Bitmap]$b){
  $sx=[Math]::Max(1,[int]($b.Width/24)); $sy=[Math]::Max(1,[int]($b.Height/24)); $n=0;$blk=0
  for($x=0;$x -lt $b.Width;$x+=$sx){ for($y=0;$y -lt $b.Height;$y+=$sy){ $c=$b.GetPixel($x,$y); $n++; if($c.R -lt 16 -and $c.G -lt 16 -and $c.B -lt 16){ $blk++ } } }
  return ($n -gt 0 -and ($blk/$n) -gt 0.85)
}
function Save-Opaque([System.Drawing.Bitmap]$src,[string]$path){
  $flat=New-Object System.Drawing.Bitmap($src.Width,$src.Height,[System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
  $g=[System.Drawing.Graphics]::FromImage($flat); $g.Clear([System.Drawing.Color]::White); $g.DrawImageUnscaled($src,0,0); $g.Dispose()
  $flat.Save($path,[System.Drawing.Imaging.ImageFormat]::Png); $flat.Dispose()
}
function Capture-Hwnd([IntPtr]$h,[string]$pngPath,[int]$settleMs=500){
  if(-not [WckCap]::IsWindow($h)){ throw "target window no longer exists (closed before capture)" }
  if([WckCap]::IsIconic($h)){ [WckCap]::ShowWindow($h,9)|Out-Null; Start-Sleep -Milliseconds 300 }   # SW_RESTORE
  [WckCap]::SetForegroundWindow($h)|Out-Null; [WckCap]::BringWindowToTop($h)|Out-Null
  $wr=New-Object WckCap+RECT; [WckCap]::GetWindowRect($h,[ref]$wr)|Out-Null          # full window (may carry fat frame)
  $dr=Get-RectDwm $h                                                                # visible bounds
  $fw=$wr.Right-$wr.Left; $fh=$wr.Bottom-$wr.Top
  $offX=[Math]::Max(0,$dr.Left-$wr.Left); $offY=[Math]::Max(0,$dr.Top-$wr.Top)
  $vw=[Math]::Min($dr.Right-$dr.Left, $fw-$offX); $vh=[Math]::Min($dr.Bottom-$dr.Top, $fh-$offY)
  if($fw -le 0 -or $fh -le 0 -or $vw -le 0 -or $vh -le 0){ throw "bad window rect (full ${fw}x${fh} vis ${vw}x${vh})" }
  $final=$null
  for($t=0;$t -lt 3 -and -not $final;$t++){
    Start-Sleep -Milliseconds $(if($t -eq 0){$settleMs}else{350})   # settle (async content load) / retry
    $full=New-Object System.Drawing.Bitmap($fw,$fh,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g=[System.Drawing.Graphics]::FromImage($full); $hdc=$g.GetHdc()
    $ok=[WckCap]::PrintWindow($h,$hdc,2)   # 2 = PW_RENDERFULLCONTENT (DWM/WPF content, occlusion-immune)
    $g.ReleaseHdc($hdc); $g.Dispose()
    if($ok){
      $vis=$full.Clone((New-Object System.Drawing.Rectangle($offX,$offY,$vw,$vh)),[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
      if(-not (Test-MostlyBlack $vis)){ $final=$vis } else { $vis.Dispose() }
    }
    $full.Dispose()
  }
  if(-not $final){
    # fallback: bring foreground + screen-grab the visible bounds (occlusion-prone; last resort)
    [WckCap]::SetForegroundWindow($h)|Out-Null; Start-Sleep -Milliseconds 450
    $final=New-Object System.Drawing.Bitmap($vw,$vh)
    $g2=[System.Drawing.Graphics]::FromImage($final)
    $g2.CopyFromScreen($dr.Left,$dr.Top,0,0,(New-Object System.Drawing.Size($vw,$vh))); $g2.Dispose()
  }
  Save-Opaque $final $pngPath; $final.Dispose()
  return @{ width=$vw; height=$vh }
}

Log "agent started (pid=$PID, root=$Root)"
while($true){
 try {
  ([DateTime]::UtcNow.ToString('s')+'Z') | Set-Content -LiteralPath (Join-Path $Root 'agent-alive.txt')
  $files = Get-ChildItem -LiteralPath $req -Filter '*.req.json' -ErrorAction SilentlyContinue | Sort-Object Name
  foreach($f in $files){
    $res = @{ id=$null; ok=$false; pid=$null; hwnd=$null; width=$null; height=$null; message='' }
    try{
      $r = Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json
      $res.id = $r.id
      $to = if($r.timeoutSec){[int]$r.timeoutSec}else{30}
      Log "req $($r.id) op=$($r.op) exe=$($r.exe)"
      switch($r.op){
        {$_ -in 'launch','launchcapture'}{
          $sp = @{ FilePath=$r.exe; PassThru=$true }
          if($r.args){ $sp.ArgumentList = @($r.args) }    # array end-to-end (multi-arg safe)
          if($r.workdir){ $sp.WorkingDirectory = $r.workdir }
          $proc = Start-Process @sp
          $win = Resolve-Window $proc.Id $to
          $res.pid = $win.opid       # the REAL UI-owning pid (not the launcher stub) — correct for close
          $res.hwnd = [int64]$win.h
          if($_ -eq 'launchcapture'){
            $cap = Capture-Hwnd $win.h (Join-Path $out "$($r.id).png") $(if($r.settleMs){[int]$r.settleMs}else{500})
            $res.width=$cap.width; $res.height=$cap.height
          }
          $res.ok=$true
        }
        'capture'{
          $win = if($r.pid){ $w=Resolve-Window ([int]$r.pid) $to; $w }
                 elseif($r.match){ Find-WindowByTitle $r.match }
                 else { throw 'capture needs pid or match' }
          if(-not $win){ throw 'window not found' }
          $cap = Capture-Hwnd $win.h (Join-Path $out "$($r.id).png") $(if($r.settleMs){[int]$r.settleMs}else{500})
          $res.pid=$win.opid; $res.hwnd=[int64]$win.h; $res.width=$cap.width; $res.height=$cap.height; $res.ok=$true
        }
        'close'{ if($r.pid){ Stop-Process -Id ([int]$r.pid) -Force -ErrorAction SilentlyContinue }; $res.ok=$true }
        'settext'{
          if(-not $r.pid){ throw 'settext needs pid' }
          if($null -eq $r.text){ throw 'settext needs text' }
          $win = Resolve-Window ([int]$r.pid) $to
          Set-UiaEditText $win.h ([string]$r.text) $to
          $res.pid=$win.opid; $res.hwnd=[int64]$win.h; $res.ok=$true
        }
        'invoke'{
          if(-not $r.pid){ throw 'invoke needs pid' }
          if([string]::IsNullOrEmpty($r.name)){ throw 'invoke needs name' }
          $win = Resolve-Window ([int]$r.pid) $to
          Invoke-UiaButton $win.h ([string]$r.name) $to
          $res.pid=$win.opid; $res.hwnd=[int64]$win.h; $res.ok=$true
        }
        default { throw "unknown op '$($r.op)'" }
      }
    } catch { $res.message = $_.Exception.Message; Log "ERR $($res.id): $($res.message)" }
    finally {
      if($res.id){
        $tmp = Join-Path $resd "$($res.id).tmp"
        $res | ConvertTo-Json -Compress | Set-Content -LiteralPath $tmp
        Move-Item -LiteralPath $tmp -Destination (Join-Path $resd "$($res.id).json") -Force   # atomic publish
      }
      Remove-Item -LiteralPath $f.FullName -Force -ErrorAction SilentlyContinue
    }
  }
 } catch { Log "LOOP-ERR: $($_.Exception.Message)" }
  Start-Sleep -Milliseconds 500
}
