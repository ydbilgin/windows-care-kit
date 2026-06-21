@echo off
REM ===========================================================================
REM uninstall-e2e-run.cmd  --  WCK Uninstall E2E in-sandbox runner
REM ===========================================================================
REM Executed by uninstall-e2e.wsb's <LogonCommand> INSIDE a throwaway Windows
REM Sandbox.  YOU DO NOT RUN THIS ON THE HOST.
REM
REM What this does (all inside the VM, nothing survives after sandbox close):
REM   (1) Self-elevates (machine-wide MSI / Inno / NSIS installs need admin). If
REM       elevation fails it FAILS LOUDLY rather than report a vacuous green.
REM   (2) Turns ON the disposable-machine opt-in (env var + %TEMP% marker) — the
REM       SAME signal step4 uses. UninstallE2E.exe REFUSES to run a real
REM       uninstaller without it, so it can never wipe a real host's programs.
REM   (3) Installs FOUR different KINDS of real program silently:
REM         - 7-Zip            (MSI, machine-wide)   -> System32-msiexec pin branch
REM         - Git for Windows  (Inno, machine-wide)  -> elevated InstallLocation anchor
REM         - Notepad++        (NSIS, machine-wide)  -> MANUAL fallback (no InstallLocation)
REM         - VS Code User     (Inno, per-user)      -> non-elevated branch
REM       Real installs are best-effort environmental setup; a download/install
REM       failure surfaces as "required program not found" = honest FAIL.
REM   (4) Runs UninstallE2E.exe, which: reads the registry (read-only), plans +
REM       production-gate-vets each app, ACTUALLY uninstalls git + vscode through
REM       the GatedExecutor + real ProcessAdapter, and RE-READS the registry to
REM       prove they are gone. 7-Zip + Notepad++ are evaluated read-only.
REM   (5) Writes a PASS/FAIL banner; auto-closes the VM when the -auto .wsb marker
REM       is present (no human X, no host force-kill).
REM
REM SANDBOX LAYOUT (prepared on the host by uninstall-e2e-stage.ps1):
REM   C:\WCK-Input  (read-only)  staged harness + scripts
REM   C:\WCK-Output (writable)   evidence report returns to the host here
REM ===========================================================================
setlocal EnableExtensions

REM --- (1) ensure ELEVATED; self-relaunch once if not -------------------------
net session >nul 2>&1
if %errorlevel% NEQ 0 (
  echo [u-e2e] not elevated -- relaunching elevated via UAC...
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process cmd.exe -ArgumentList '/c','\"%~f0\"' -Verb RunAs" 2>nul
  if errorlevel 1 (
    echo [u-e2e] FATAL: could not elevate. Machine-wide installs cannot run.> "C:\WCK-Output\uninstall-FATAL-not-elevated.txt"
    REM No elevated instance will start -> it would never reach the auto-close shutdown.
    REM Honor the autoclose marker HERE so an autonomous run can never orphan the VM.
    if exist "C:\WCK-Output\autoclose.marker" shutdown /s /t 5 /c "WCK uninstall E2E: elevation failed; auto-closing"
  )
  exit /b
)

setlocal EnableDelayedExpansion

set "INPUT=C:\WCK-Input"
set "HARNESS=%INPUT%\harness"
set "OUT=C:\WCK-Output"
set "DL=%TEMP%\wck-dl"
mkdir "%DL%" 2>nul

echo [u-e2e] ELEVATED: YES
echo ELEVATED: YES> "%OUT%\elevation.txt"
> "%OUT%\progress.log" echo [!TIME!] elevated; starting uninstall E2E

REM --- (2) disposable-machine opt-in (REQUIRED for the harness to execute) -----
REM Only place that turns the harness's --execute guard ON; exists only in the VM.
set "WCK_DISPOSABLE_MACHINE=1"
echo disposable> "%TEMP%\wck-disposable.marker"

REM --- (3) install four real programs silently --------------------------------

REM  (3a) 7-Zip MSI (machine-wide -> System32-msiexec pin branch) ---------------
echo [u-e2e] (3a) Installing 7-Zip (MSI, machine-wide)...
>> "%OUT%\progress.log" echo [!TIME!] downloading 7-Zip MSI...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "try { Invoke-WebRequest -Uri 'https://www.7-zip.org/a/7z2408-x64.msi' -OutFile '%DL%\7z.msi' -UseBasicParsing -TimeoutSec 180 } catch { exit 1 }"
if errorlevel 1 (
  echo [u-e2e] WARNING: 7-Zip download failed.
) else (
  msiexec /i "%DL%\7z.msi" /qn /norestart
  echo [u-e2e]   7-Zip msiexec exit: !ERRORLEVEL!
)

REM  (3b) Git for Windows (Inno, machine-wide -> InstallLocation anchor) --------
echo [u-e2e] (3b) Installing Git for Windows (Inno, machine-wide)...
>> "%OUT%\progress.log" echo [!TIME!] downloading Git for Windows...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "try { Invoke-WebRequest -Uri 'https://github.com/git-for-windows/git/releases/download/v2.47.0.windows.1/Git-2.47.0-64-bit.exe' -OutFile '%DL%\git.exe' -UseBasicParsing -TimeoutSec 300 } catch { exit 1 }"
if errorlevel 1 (
  echo [u-e2e] WARNING: Git download failed.
) else (
  "%DL%\git.exe" /VERYSILENT /NORESTART /SUPPRESSMSGBOXES /NOCANCEL /SP-
  echo [u-e2e]   Git installer exit: !ERRORLEVEL!
)

REM  (3c) Notepad++ (NSIS, machine-wide -> MANUAL fallback, no InstallLocation) -
echo [u-e2e] (3c) Installing Notepad++ (NSIS, machine-wide)...
>> "%OUT%\progress.log" echo [!TIME!] downloading Notepad++...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "try { Invoke-WebRequest -Uri 'https://github.com/notepad-plus-plus/notepad-plus-plus/releases/download/v8.7.1/npp.8.7.1.Installer.x64.exe' -OutFile '%DL%\npp.exe' -UseBasicParsing -TimeoutSec 240 } catch { exit 1 }"
if errorlevel 1 (
  echo [u-e2e] WARNING: Notepad++ download failed.
) else (
  "%DL%\npp.exe" /S
  echo [u-e2e]   Notepad++ installer exit: !ERRORLEVEL!
)

REM  (3d) VS Code User Setup (Inno, per-user -> non-elevated branch) ------------
echo [u-e2e] (3d) Installing Visual Studio Code (user setup)...
>> "%OUT%\progress.log" echo [!TIME!] downloading VS Code user setup...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "try { Invoke-WebRequest -Uri 'https://update.code.visualstudio.com/latest/win32-x64-user/stable' -OutFile '%DL%\vscode.exe' -UseBasicParsing -TimeoutSec 240 } catch { exit 1 }"
if errorlevel 1 (
  echo [u-e2e] WARNING: VS Code download failed.
) else (
  REM ^! becomes a literal ! under EnableDelayedExpansion: /MERGETASKS=!runcode = "do not launch after install".
  "%DL%\vscode.exe" /VERYSILENT /NORESTART /SUPPRESSMSGBOXES /MERGETASKS=^!runcode
  echo [u-e2e]   VS Code installer exit: !ERRORLEVEL!
)

REM Let the registry settle after the install wave before inventory.
>> "%OUT%\progress.log" echo [!TIME!] installs done; settling 10s before harness...
ping -n 11 127.0.0.1 >nul

REM --- (4) run the harness -----------------------------------------------------
echo [u-e2e] (4) Running UninstallE2E harness...
>> "%OUT%\progress.log" echo [!TIME!] running UninstallE2E.exe (execute git,vscode)...
REM settleSeconds 90: Git + VS Code are Inno uninstallers that relaunch unins000.exe from a
REM temp copy and return BEFORE the registry key is gone; a cold VM under post-install load can
REM lag, so poll generously (a false FAIL would cost an autonomous re-run; never a false pass).
"%HARNESS%\UninstallE2E.exe" --output "%OUT%" --execute git,vscode --require 7zip,git,vscode,notepadpp --settleSeconds 90 1> "%OUT%\harness-console.log" 2>&1
set "RC=!ERRORLEVEL!"
>> "%OUT%\progress.log" echo [!TIME!] harness exited rc=!RC!
>"%OUT%\harness-exitcode.txt" echo !RC!
echo [u-e2e] Harness exit code: !RC!

REM --- (5) PASS/FAIL banner ----------------------------------------------------
echo.
echo ===========================================================================
if "!RC!"=="0" (
  echo [u-e2e] RESULT: PASS  -- all required apps found, branches matched, git+vscode uninstalled and GONE.
  echo PASS > "%OUT%\uninstall-e2e-result.txt"
) else if "!RC!"=="3" (
  echo [u-e2e] RESULT: GUARD-REFUSED  -- disposable-machine signal missing (should not happen in the VM).
  echo GUARD-REFUSED > "%OUT%\uninstall-e2e-result.txt"
) else (
  echo [u-e2e] RESULT: FAIL  -- harness exited !RC!.  See %OUT%\uninstall-e2e-evidence.json
  echo FAIL (harness exit !RC!) > "%OUT%\uninstall-e2e-result.txt"
)
echo   Evidence : %OUT%\uninstall-e2e-evidence.json
echo   Summary  : %OUT%\uninstall-e2e-summary.txt
echo   Console  : %OUT%\harness-console.log
echo   On the HOST these are at  C:\WCK-UninstallOutput\
echo ===========================================================================
echo.
echo You may now close the sandbox (all VM contents are discarded).
echo.
REM Unattended/autonomous runs drop C:\WCK-Output\autoclose.marker via the -auto .wsb so
REM the VM shuts itself down cleanly (no human X, no host force-kill). Human double-click
REM of the normal .wsb has no marker -> pause as before (backward compatible).
if exist "%OUT%\autoclose.marker" (
  echo [u-e2e] autoclose marker present -- shutting down the sandbox VM cleanly...
  shutdown /s /t 5 /c "WCK uninstall E2E done; auto-closing sandbox"
) else (
  pause
)
