#define AppName "ClipDock"
#define AppVersion "1.0.0"
#define AppPublisher "ClipDock"
#define AppExeName "ClipDock.exe"

[Setup]
AppId={{E076CD23-8128-4F17-A79B-C32931A425C4}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=ClipDock Windows Installer
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts\installer
OutputBaseFilename=ClipDock-Setup-{#AppVersion}-x64
SetupIconFile=..\src\ClipDock.App\Assets\app-icon.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no
ChangesAssociations=yes
SetupLogging=yes
UsePreviousAppDir=yes
UsedUserAreasWarning=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\1.0.0-win-x64-final\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\Extension\*"; DestDir: "{app}\Extension"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\ClipDock"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{group}\Browser Extension"; Filename: "{app}\Extension"
Name: "{autodesktop}\ClipDock"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Classes\clipdock"; ValueType: string; ValueName: ""; ValueData: "URL:ClipDock Protocol"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\clipdock"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\clipdock\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"
Root: HKCU; Subkey: "Software\Classes\clipdock\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch ClipDock"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usUninstall) and (not UninstallSilent) then
  begin
    if MsgBox('Remove ClipDock settings, history, logs, and downloaded engine tools?',
      mbConfirmation, MB_YESNO) = IDYES then
      DelTree(ExpandConstant('{localappdata}\ClipDock'), True, True, True);
  end;
end;
