; Windows Care Kit — component installer (modular M5). Compiled ONLY in CI by a pinned,
; hash-verified official ISCC against the dotnet-publish folder output. Base (publish root)
; mandatory; each Modules\<id>\ folder = one optional component — unchecked => never
; extracted => the M4 folder-discovery loader omits it. "Add later" = re-run (same AppId).
; SECURITY (binding, M4 audit): admin + Program Files default so the install dir (and
; Modules\) inherits an admin-owned ACL. No per-user install offered. No Permissions:,
; no PrivilegesRequiredOverridesAllowed, no network. Signing arrives with M6.

#ifndef AppVer
  #define AppVer "0.0.0-dev"
#endif
#ifndef AppVerNum
  #define AppVerNum "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish\win-x64"
#endif
#ifndef OutputName
  #define OutputName "WindowsCareKit-Setup-dev-win-x64"
#endif

[Setup]
AppId={{5CDA29A9-74D6-48D3-A70E-806E22E4A47A}
AppName=Windows Care Kit
AppVersion={#AppVer}
VersionInfoVersion={#AppVerNum}
AppPublisher=Yasin Derya Bilgin
AppPublisherURL=https://github.com/ydbilgin
AppSupportURL=https://github.com/ydbilgin/windows-care-kit/issues
AppUpdatesURL=https://github.com/ydbilgin/windows-care-kit/releases
AppCopyright=Copyright (c) 2026 Yasin Derya Bilgin
DefaultDirName={autopf}\Windows Care Kit
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\publish\installer
OutputBaseFilename={#OutputName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
UninstallDisplayName=Windows Care Kit
UninstallDisplayIcon={app}\WindowsCareKit.exe

[Types]
Name: "full";    Description: "Full installation (all modules)"
Name: "compact"; Description: "Base only (Settings; add modules later by re-running Setup)"
Name: "custom";  Description: "Custom — choose modules"; Flags: iscustom

[Components]
Name: "uninstall"; Description: "Uninstall — guided program removal";          Types: full
Name: "clean";     Description: "Clean — disk and artifact cleanup";           Types: full
Name: "backup";    Description: "Back up — profile and settings backup";       Types: full
Name: "migration"; Description: "Migration — settings migration (40 recipes)"; Types: full
Name: "restore";   Description: "Restore — new-machine restore";               Types: full
Name: "install";   Description: "Reinstall — program reinstall from a recipe"; Types: full

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "\Modules\*,\manifests\*,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\Modules\uninstall\*"; DestDir: "{app}\Modules\uninstall"; Components: uninstall; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\Modules\clean\*";     DestDir: "{app}\Modules\clean";     Components: clean;     Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\Modules\backup\*";    DestDir: "{app}\Modules\backup";    Components: backup;    Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\Modules\migration\*"; DestDir: "{app}\Modules\migration"; Components: migration; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\Modules\restore\*";   DestDir: "{app}\Modules\restore";   Components: restore;   Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\Modules\install\*";   DestDir: "{app}\Modules\install";   Components: install;   Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\manifests\00-ai-tools.json";      DestDir: "{app}\manifests"; Components: backup;  Flags: ignoreversion
Source: "{#PublishDir}\manifests\10-developer.json";     DestDir: "{app}\manifests"; Components: backup;  Flags: ignoreversion
Source: "{#PublishDir}\manifests\20-browser.json";       DestDir: "{app}\manifests"; Components: backup;  Flags: ignoreversion
Source: "{#PublishDir}\manifests\30-games.json";         DestDir: "{app}\manifests"; Components: backup;  Flags: ignoreversion
Source: "{#PublishDir}\manifests\40-system.json";        DestDir: "{app}\manifests"; Components: backup;  Flags: ignoreversion
Source: "{#PublishDir}\manifests\50-notes.json";         DestDir: "{app}\manifests"; Components: backup;  Flags: ignoreversion
Source: "{#PublishDir}\manifests\60-wsl.json";           DestDir: "{app}\manifests"; Components: backup;  Flags: ignoreversion
Source: "{#PublishDir}\manifests\70-general-user.json";  DestDir: "{app}\manifests"; Components: backup;  Flags: ignoreversion
Source: "{#PublishDir}\manifests\80-network-drive.json"; DestDir: "{app}\manifests"; Components: backup;  Flags: ignoreversion
Source: "{#PublishDir}\manifests\90-install.json";       DestDir: "{app}\manifests"; Components: install; Flags: ignoreversion

[InstallDelete]
Type: filesandordirs; Name: "{app}\Modules\uninstall"; Components: not uninstall
Type: filesandordirs; Name: "{app}\Modules\clean";     Components: not clean
Type: filesandordirs; Name: "{app}\Modules\backup";    Components: not backup
Type: filesandordirs; Name: "{app}\Modules\migration"; Components: not migration
Type: filesandordirs; Name: "{app}\Modules\restore";   Components: not restore
Type: filesandordirs; Name: "{app}\Modules\install";   Components: not install
Type: files; Name: "{app}\manifests\00-ai-tools.json";      Components: not backup
Type: files; Name: "{app}\manifests\10-developer.json";     Components: not backup
Type: files; Name: "{app}\manifests\20-browser.json";       Components: not backup
Type: files; Name: "{app}\manifests\30-games.json";         Components: not backup
Type: files; Name: "{app}\manifests\40-system.json";        Components: not backup
Type: files; Name: "{app}\manifests\50-notes.json";         Components: not backup
Type: files; Name: "{app}\manifests\60-wsl.json";           Components: not backup
Type: files; Name: "{app}\manifests\70-general-user.json";  Components: not backup
Type: files; Name: "{app}\manifests\80-network-drive.json"; Components: not backup
Type: files; Name: "{app}\manifests\90-install.json";       Components: not install

[UninstallDelete]
Type: filesandordirs; Name: "{app}\Modules"
Type: filesandordirs; Name: "{app}\manifests"

[Icons]
Name: "{autoprograms}\Windows Care Kit"; Filename: "{app}\WindowsCareKit.exe"
Name: "{autodesktop}\Windows Care Kit";  Filename: "{app}\WindowsCareKit.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\WindowsCareKit.exe"; Description: "{cm:LaunchProgram,Windows Care Kit}"; Flags: nowait postinstall skipifsilent runasoriginaluser
