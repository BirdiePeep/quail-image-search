; Windows installer for Quail Image Search (Inno Setup 6). Built by scripts\build.ps1, which passes:
;   /DAppVersion=1.0.0        version shown to users
;   /DFileVersion=1.0.0.0     numeric version for the setup program's file properties
;   /DArch=x64 | arm64        which build this installs
;   /DSourceDir=<dir>         the published, checked build (artifacts\publish\win-<arch>)
;   /DOutputDir=<dir>         where the setup program goes (artifacts\)
;   /DRepoRoot=<dir>          the repository root (for the icon)
;
; Installs for the current user by default (no admin rights needed); the user can choose "all users"
; instead. Nothing here uses the network (PRIVACY.md).
;
; Uninstalling keeps the app-data folder (models, indexes, settings) unless the user agrees to delete it.
; Silent uninstalls keep it too, unless /DELETEDATA is passed.

#define AppName "Quail Image Search"
#define ExeName "QuailImageSearch.exe"

[Setup]
; Never change AppId: Windows uses it to recognise upgrades and the uninstaller.
AppId={{F5AF2FC3-671D-4888-BADD-4AD2774411C6}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Chase Grozdina
AppCopyright=Copyright (c) 2026 Chase Grozdina
VersionInfoVersion={#FileVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#ExeName}
SetupIconFile={#RepoRoot}\src\QuailImageSearch\Assets\app.ico
WizardStyle=modern
MinVersion=10.0
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
Compression=lzma2/max
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=QuailImageSearch-{#AppVersion}-win-{#Arch}-setup

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#ExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#ExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#ExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// The app-data folder, worked out the same way as AppPaths.DataDir in the app.
function DataDir(): String;
begin
  Result := GetEnv('QUAILIMAGESEARCH_DATA_DIR');
  if Result = '' then Result := GetEnv('IMAGESEARCH_DATA_DIR');
  if Result = '' then Result := ExpandConstant('{userappdata}\QuailImageSearch');
end;

function HasParam(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Dir: String;
  Delete: Boolean;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  Dir := DataDir();
  if not DirExists(Dir) then Exit;
  if UninstallSilent then
    Delete := HasParam('/DELETEDATA')
  else
    Delete := MsgBox('Also delete your Quail Image Search data?' + #13#10#13#10 +
      'This permanently removes the downloaded models, search indexes and settings in:' + #13#10 + Dir + #13#10#13#10 +
      'Your images are not affected. Choose No to keep the data for a later reinstall.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  if Delete then DelTree(Dir, True, True, True);
end;
