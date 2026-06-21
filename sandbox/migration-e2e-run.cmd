@echo off
REM ===========================================================================
REM migration-e2e-run.cmd  --  WCK Migration E2E in-sandbox runner
REM ===========================================================================
REM Executed by migration-e2e.wsb's <LogonCommand> INSIDE a throwaway Windows
REM Sandbox.  YOU DO NOT RUN THIS ON THE HOST.
REM
REM What this script does (all inside the VM, nothing survives after sandbox close):
REM   (a) Installs git and VS Code silently (requires network -- sandbox has it).
REM       REAL INSTALLS ARE BEST-EFFORT ENVIRONMENTAL SETUP.  The proof is the
REM       round-trip over real config; it does NOT depend on installs succeeding.
REM   (b) Generates REAL config:
REM         - git config --global (-> real %UserProfile%\.gitconfig)
REM         - VS Code settings.json + keybindings.json (-> %AppData%\Code\User\)
REM         - Claude: .claude\CLAUDE.md, settings.json, projects, skills
REM         - Discord: %AppData%\discord\settings.json + quotes.json
REM       Also seeds EXCLUDED noise to prove the harness excludes correctly:
REM         - Secret files INSIDE .claude\skills\demo (a backed-up subtree):
REM             id_rsa  (matches SecretGlobOverlay: id_rsa*)
REM             app.secret  (matches SecretGlobOverlay: *secret*)
REM         - A Cache dir INSIDE .claude\skills\demo (backed-up parent, Cache pruned):
REM             .claude\skills\demo\Cache\blob.dat
REM         - *Cache* directories under .claude and discord
REM         - shell-snapshots + todos directories under .claude
REM   (c) Runs the staged MigrationE2E harness:
REM         --profileA  = real sandbox %UserProfile%
REM         --profileB  = fabricated C:\WCK-MigrationB
REM         --output    = mapped C:\WCK-Output (comes back to host)
REM         --gitInstall    ok|fail   (actual installer exit code)
REM         --vscodeInstall ok|fail   (actual installer exit code)
REM   (d) Writes a PASS/FAIL banner.
REM
REM SANDBOX LAYOUT:
REM   C:\WCK-Input  (read-only)  staged harness + scripts from migration-e2e-stage.ps1
REM   C:\WCK-Output (writable)   evidence report returns to the host here
REM
REM Self-elevates if needed.  NEVER fakes a pass.
REM ===========================================================================
setlocal EnableExtensions EnableDelayedExpansion

REM --- (0) NO self-elevation (user-level only) --------------------------------
REM This E2E is entirely user-level: per-user app installs (VS Code user-setup +
REM PortableGit self-extract), user-profile config, and temp dirs. It needs NO
REM admin. We do NOT self-elevate, because an unattended UAC prompt would hang
REM the sandbox run (the cause of the earlier stuck run).

set "INPUT=C:\WCK-Input"
set "HARNESS=%INPUT%\harness"
set "OUT=C:\WCK-Output"
set "PROFB=C:\WCK-MigrationB"
set "PKG=C:\WCK-MigrationPkg"

REM Track install results to pass to harness (never masks failure -- visible in evidence).
set "GIT_INSTALL_STATUS=fail"
set "VSCODE_INSTALL_STATUS=fail"

echo [e2e] running user-level (no elevation needed)
echo.
> "%OUT%\progress.log" echo [%TIME%] started user-level; migration E2E

REM --- (a) install git via PortableGit (self-extracting, NO admin, NO UAC) -----
echo [e2e] (a) Installing PortableGit...
>> "%OUT%\progress.log" echo [%TIME%] downloading PortableGit (timeout 180s)...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "try { Invoke-WebRequest -Uri 'https://github.com/git-for-windows/git/releases/download/v2.47.0.windows.1/PortableGit-2.47.0-64-bit.7z.exe' -OutFile '%TEMP%\PortableGit.exe' -UseBasicParsing -TimeoutSec 180 } catch { exit 1 }"
if errorlevel 1 (
    echo [e2e] WARNING: PortableGit download failed.
    set "GIT_INSTALL_STATUS=fail"
    goto :skip_git_install
)
>> "%OUT%\progress.log" echo [%TIME%] extracting PortableGit (no admin, no UAC)...
REM 7-Zip self-extracting archive: -o<dir> output, -y assume-yes. No install, no UAC.
"%TEMP%\PortableGit.exe" -o"%LOCALAPPDATA%\PortableGit" -y
if exist "%LOCALAPPDATA%\PortableGit\cmd\git.exe" (
    set "GIT_INSTALL_STATUS=ok"
    set "PATH=%LOCALAPPDATA%\PortableGit\cmd;%PATH%"
) else (
    echo [e2e] WARNING: PortableGit extract produced no git.exe.
    set "GIT_INSTALL_STATUS=fail"
)
:skip_git_install
echo [e2e]   git install status: %GIT_INSTALL_STATUS%

REM --- (a) install VS Code (silent user-setup) --------------------------------
echo [e2e] (a) Installing Visual Studio Code...
>> "%OUT%\progress.log" echo [%TIME%] git done (%GIT_INSTALL_STATUS%); downloading VS Code (timeout 180s)...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "try { Invoke-WebRequest -Uri 'https://update.code.visualstudio.com/latest/win32-x64-user/stable' -OutFile '%TEMP%\VSCodeSetup.exe' -UseBasicParsing -TimeoutSec 180 } catch { exit 1 }"
if errorlevel 1 (
    echo [e2e] WARNING: VS Code download failed. Skipping VS Code install.
    set "VSCODE_INSTALL_STATUS=fail"
    goto :skip_vscode_install
)
REM FIX 5: escape the ! so EnableDelayedExpansion does not consume it.
REM /MERGETASKS=^!runcode means "do NOT run code after install" (the ^! becomes a literal ! in the arg).
"%TEMP%\VSCodeSetup.exe" /VERYSILENT /NORESTART /MERGETASKS=^!runcode
if errorlevel 1 (
    echo [e2e] WARNING: VS Code install returned non-zero; may be ok.
    set "VSCODE_INSTALL_STATUS=fail"
) else (
    set "VSCODE_INSTALL_STATUS=ok"
)
:skip_vscode_install
echo [e2e]   VS Code install status: %VSCODE_INSTALL_STATUS%

REM --- (b) generate REAL config -----------------------------------------------
>> "%OUT%\progress.log" echo [%TIME%] vscode done (%VSCODE_INSTALL_STATUS%); seeding real config...
echo [e2e] (b) Generating real config for Profile A (%UserProfile%)...

REM -- git --
where git >nul 2>&1
if %errorlevel% EQU 0 (
    git config --global user.name  "Sandbox User"
    git config --global user.email "sandbox@example.com"
    git config --global core.autocrlf true
    git config --global init.defaultBranch main
    echo [e2e]   .gitconfig written.
) else (
    REM git not installed (download failed) -- write the file manually
    echo [user]> "%UserProfile%\.gitconfig"
    echo     name = Sandbox User>> "%UserProfile%\.gitconfig"
    echo     email = sandbox@example.com>> "%UserProfile%\.gitconfig"
    echo [core]>> "%UserProfile%\.gitconfig"
    echo     autocrlf = true>> "%UserProfile%\.gitconfig"
    echo [init]>> "%UserProfile%\.gitconfig"
    echo     defaultBranch = main>> "%UserProfile%\.gitconfig"
    echo [e2e]   .gitconfig written (manual, git not found).
)

REM -- VS Code --
mkdir "%AppData%\Code\User" 2>nul
echo { "editor.fontSize": 14, "workbench.colorTheme": "Default Dark+" }> "%AppData%\Code\User\settings.json"
echo { "key": "ctrl+shift+p", "command": "workbench.action.showCommands" }> "%AppData%\Code\User\keybindings.json"
echo [e2e]   VS Code settings.json + keybindings.json written.

REM -- Claude --
mkdir "%UserProfile%\.claude\projects\demo\memory" 2>nul
mkdir "%UserProfile%\.claude\skills\demo" 2>nul
echo # WCK Demo CLAUDE.md > "%UserProfile%\.claude\CLAUDE.md"
echo { "theme": "dark", "model": "claude-sonnet" } > "%UserProfile%\.claude\settings.json"
echo # Demo project memory note > "%UserProfile%\.claude\projects\demo\memory\note.md"
echo # Demo skill > "%UserProfile%\.claude\skills\demo\SKILL.md"
echo [e2e]   .claude tree written.

REM -- Discord --
mkdir "%AppData%\discord" 2>nul
echo { "SKIP_HOST_UPDATE": true, "WINDOW_BOUNDS": {"width":1280,"height":720} } > "%AppData%\discord\settings.json"
echo { "quotes": ["Be excellent to each other."] } > "%AppData%\discord\quotes.json"
echo [e2e]   discord settings written.

REM -- Excluded noise (FIX 1 + FIX 2: non-vacuous exclusion proof) --
REM These MUST NOT appear in the package after backup.
REM All secret files are placed INSIDE .claude\skills\demo — a backed-up subtree —
REM so their absence from the package is a meaningful pruning proof.

REM  FIX 1: secret files INSIDE a backed-up subtree (.claude\skills is a recipe item).
REM  id_rsa* matches SecretGlobOverlay glob: id_rsa*
REM  app.secret matches SecretGlobOverlay glob: *secret*
echo FAKE PRIVATE KEY > "%UserProfile%\.claude\skills\demo\id_rsa"
echo FAKE APP SECRET TOKEN > "%UserProfile%\.claude\skills\demo\app.secret"
echo [e2e]   Secret noise seeded inside backed-up subtree: id_rsa, app.secret

REM  FIX 2: Cache dir INSIDE backed-up subtree (.claude\skills\demo\Cache).
REM  Parent (.claude\skills\demo) is backed up; Cache leaf matches *Cache* exclude.
mkdir "%UserProfile%\.claude\skills\demo\Cache" 2>nul
echo cache-blob-data > "%UserProfile%\.claude\skills\demo\Cache\blob.dat"
echo [e2e]   Cache noise seeded inside backed-up subtree: .claude\skills\demo\Cache\blob.dat

REM  Other cache/junk dirs (outside backed-up items but still verified as excluded).
mkdir "%UserProfile%\.claude\LocalCache" 2>nul
echo cache-data > "%UserProfile%\.claude\LocalCache\temp.dat"

mkdir "%UserProfile%\.claude\shell-snapshots" 2>nul
echo snap > "%UserProfile%\.claude\shell-snapshots\2026-06-21.snap"

mkdir "%UserProfile%\.claude\todos" 2>nul
echo TODO > "%UserProfile%\.claude\todos\todo.txt"

mkdir "%AppData%\discord\Cache" 2>nul
echo cachedata > "%AppData%\discord\Cache\f_000001"

mkdir "%AppData%\discord\blob_storage\GPUCache" 2>nul
echo gpu > "%AppData%\discord\blob_storage\GPUCache\data_0"

echo [e2e]   Excluded noise seeded (secrets inside .claude\skills\demo, Cache dirs, shell-snapshots, todos).

REM --- (c) run the staged harness ---------------------------------------------
>> "%OUT%\progress.log" echo [%TIME%] config seeded; running MigrationE2E harness...
echo [e2e] (c) Running MigrationE2E harness...

REM Profile B is a fabricated directory -- the harness creates it during restore.
set "APPDATA_B=%PROFB%\AppData\Roaming"
set "LOCAL_B=%PROFB%\AppData\Local"

mkdir "%PKG%" 2>nul
mkdir "%PROFB%" 2>nul

"%HARNESS%\MigrationE2E.exe" ^
    --profileA "%UserProfile%" ^
    --appdataA "%AppData%" ^
    --localA   "%LocalAppData%" ^
    --profileB "%PROFB%" ^
    --appdataB "%APPDATA_B%" ^
    --localB   "%LOCAL_B%" ^
    --package  "%PKG%" ^
    --output   "%OUT%" ^
    --gitInstall    "%GIT_INSTALL_STATUS%" ^
    --vscodeInstall "%VSCODE_INSTALL_STATUS%"

set "HARNESS_RC=%ERRORLEVEL%"
>> "%OUT%\progress.log" echo [%TIME%] harness exited rc=%HARNESS_RC%
echo [e2e] Harness exit code: %HARNESS_RC%

REM --- (d) PASS/FAIL banner ---------------------------------------------------
echo.
echo ===========================================================================
if "%HARNESS_RC%"=="0" (
    echo [e2e] RESULT: PASS  -- all migration targets verified, SHAs match, excludes correct.
    echo [e2e]         Git install: %GIT_INSTALL_STATUS%  VS Code install: %VSCODE_INSTALL_STATUS%
    echo [e2e]         (installs are best-effort; PASS proves the round-trip, not the installs)
    echo PASS > "%OUT%\e2e-result.txt"
) else (
    echo [e2e] RESULT: FAIL  -- harness exited %HARNESS_RC%.  See %OUT%\migration-e2e-evidence.json
    echo FAIL (harness exit %HARNESS_RC%) > "%OUT%\e2e-result.txt"
)
echo   Git install     : %GIT_INSTALL_STATUS%
echo   VS Code install : %VSCODE_INSTALL_STATUS%
echo   Evidence: %OUT%\migration-e2e-evidence.json
echo   Summary : %OUT%\migration-e2e-summary.txt
echo ===========================================================================
echo.
echo You may now close the sandbox (all VM contents are discarded).
echo.
pause
