@echo off
REM ===========================================================================
REM step4-run.cmd  —  Windows Care Kit (WCK) · Step 4 in-sandbox test runner
REM ===========================================================================
REM Executed by WindowsCareKit-step4-test.wsb's <LogonCommand> INSIDE a throwaway
REM Windows Sandbox. It (1) ensures it runs ELEVATED, (2) flips the destructive-
REM test opt-in (env var + marker) so [DisposableFact] tests actually RUN instead
REM of statically skipping, then (3) runs the FULL suite OFFLINE and writes
REM results to the writable output mapping so they come back to the host.
REM
REM WHY ELEVATION: Tier B destructive tests (service create/delete, scheduled
REM task, HKLM registry) need admin. Windows Sandbox's LogonCommand runs with a
REM UAC-FILTERED (non-elevated) token, so without self-elevation those tests
REM would silently skip/pass = FALSE confidence. We self-elevate and, if that
REM fails, FAIL LOUDLY rather than report a vacuous green. (security review, HIGH-1)
REM
REM This file does nothing on its own: it only runs when the sandbox launches it.
REM It is NEVER invoked by any build/test/CI step on the host.
REM
REM Sandbox layout (prepared on the host by step4-stage.ps1):
REM   C:\WCK-Input    (read-only)  staged bundle: repo\ , dotnet\ , localfeed\
REM   C:\WCK-Output   (writable)   TRX + console log return to the host here
REM ===========================================================================
setlocal EnableExtensions

REM --- (1) ensure ELEVATED; self-relaunch once if not -------------------------
net session >nul 2>&1
if %errorlevel% NEQ 0 (
  echo [step4] not elevated -- relaunching elevated via UAC...
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process cmd.exe -ArgumentList '/c','\"%~f0\"' -Verb RunAs" 2>nul
  if errorlevel 1 (
    echo [step4] FATAL: could not elevate. Tier B destructive tests cannot run.> "C:\WCK-Output\step4-FATAL-not-elevated.txt"
  )
  exit /b
)

set "INPUT=C:\WCK-Input"
set "WORK=C:\WCK"
set "OUT=C:\WCK-Output"

echo [step4] ELEVATED: YES
echo ELEVATED: YES> "%OUT%\step4-elevation.txt"

echo [step4] preparing writable working copy of the repo...
REM Build needs a writable tree (bin/obj). The SDK + offline feed are read fine
REM straight from the read-only mapping, so only the repo is copied locally.
robocopy "%INPUT%\repo" "%WORK%\repo" /MIR /NFL /NDL /NJH /NJS /NP >nul

REM --- portable .NET SDK: run read-only directly from the mapping --------------
set "DOTNET_ROOT=%INPUT%\dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"
set "DOTNET_NOLOGO=1"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_CLI_HOME=%WORK%\home"
REM Restore extracts the offline feed into this writable per-run folder.
set "NUGET_PACKAGES=%WORK%\gp"

REM --- (2) disposable-machine opt-in (BOTH signals required) -------------------
REM   1) env var WCK_DISPOSABLE_MACHINE == "1"   2) %TEMP%\wck-disposable.marker
REM Only place in the whole project that turns [DisposableFact] tests ON; exists
REM only inside the VM. With elevation guaranteed above, Tier B genuinely runs.
set "WCK_DISPOSABLE_MACHINE=1"
echo disposable> "%TEMP%\wck-disposable.marker"

echo [step4] .NET SDK version:
"%DOTNET_ROOT%\dotnet.exe" --version

echo [step4] running FULL suite (incl. Category=Destructive) OFFLINE...
echo        console output -^> %OUT%\step4-console.log
pushd "%WORK%\repo"
"%DOTNET_ROOT%\dotnet.exe" test tests\Suite.Tests\Suite.Tests.csproj -c Debug ^
  --logger "trx;LogFileName=step4.trx" --results-directory "%OUT%" -v normal ^
  1> "%OUT%\step4-console.log" 2>&1
set "RC=%ERRORLEVEL%"
popd

REM redirect-first form: `echo 1>file` would otherwise be parsed as a stream-1 redirect
REM (writing "ECHO is off."); `echo 2>file` would redirect stderr. This form is RC-safe.
>"%OUT%\step4-exitcode.txt" echo %RC%

REM --- (3) RAN-vs-SKIPPED tally so vacuous passes are visible ------------------
REM xUnit prints a summary line "Passed: X, Skipped: Y, ...". Any destructive
REM test that SKIPPED at runtime (should be 0 in an elevated sandbox) is a red flag.
findstr /C:"Passed!" /C:"Failed!" /C:"Skipped" /C:"Passed:" "%OUT%\step4-console.log" > "%OUT%\step4-summary.txt" 2>nul

REM --- (4) AUTHORITATIVE Tier B pass-gate (security review FIX-2, 2026-06-20) ----
REM `dotnet test` RC=0 ALONE is NOT proof Tier B ran: a statically-SKIPPED
REM [DisposableFact] also exits 0 (vacuous pass). step4-gate.ps1 parses the TRX
REM and FAILS LOUDLY unless B1/B2/B3 each ran AND passed. Same script runs on the
REM host when results are read. %~dp0 = ...\repo\sandbox\ (read-only mapping; PS reads fine).
if exist "%OUT%\step4-TIERB-GATE-FAILED.txt" del /q "%OUT%\step4-TIERB-GATE-FAILED.txt"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0step4-gate.ps1" -Trx "%OUT%\step4.trx"
set "GATE=%ERRORLEVEL%"
if not "%GATE%"=="0" (
  echo TIER B GATE FAILED ^(code %GATE%^): one or more disposable tests did not RUN+PASS. See step4.trx.> "%OUT%\step4-TIERB-GATE-FAILED.txt"
)

REM PASS only if BOTH the test run AND the Tier B gate are green (default FAIL).
set "RESULT=FAIL"
if "%RC%"=="0" if "%GATE%"=="0" set "RESULT=PASS"

echo.
echo ===========================================================================
if "%RESULT%"=="PASS" (
  echo [step4] RESULT: PASS  ^(dotnet RC=0 AND Tier B gate OK^)
) else (
  echo [step4] RESULT: FAIL / ERROR  ^(dotnet RC=%RC%  TierB-gate=%GATE%^)
)
echo   ELEVATED: YES   ^(Tier B destructive tests were eligible to run^)
echo   gate    : step4-gate.ps1 exit=%GATE%   ^(0 = B1/B2/B3 ran+passed; 3 = no TRX; 4 = skipped/failed^)
echo   summary : %OUT%\step4-summary.txt
echo   TRX     : %OUT%\step4.trx
echo   console : %OUT%\step4-console.log
echo   On the HOST these are at  C:\WCK-SandboxOutput\
echo ===========================================================================
echo You may now close the sandbox (everything inside is discarded).
echo.
REM Unattended/autonomous runs drop C:\WCK-Output\autoclose.marker via the -auto .wsb so
REM the VM shuts itself down cleanly (no human X, no host force-kill). Human double-click
REM of the normal .wsb has no marker -> pause as before (backward compatible).
if exist "%OUT%\autoclose.marker" (
  echo [step4] autoclose marker present -- shutting down the sandbox VM cleanly...
  shutdown /s /t 5 /c "WCK step4 done; auto-closing sandbox"
) else (
  pause
)
