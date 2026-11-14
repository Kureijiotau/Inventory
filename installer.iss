; Inno Setup script for the Inventory setup wizard.
; Build:  ISCC.exe /DAppVersion=1.0.12 installer.iss   (after build.ps1)
;
; Installs for the current user only, so no administrator password is needed
; and the in-app Update button can run this setup silently.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{B7C0B3C2-6D0E-4C2B-9E1E-5A3B0E7C9D41}
AppName=Inventory
AppVersion={#AppVersion}
AppPublisher=Salil Baghdad
DefaultDirName={localappdata}\Programs\Inventory
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=lowest
OutputDir=dist
OutputBaseFilename=InventorySetup
SetupIconFile=host\Inventory.ico
UninstallDisplayIcon={app}\Inventory.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
#if FileExists(AddBackslash(CompilerPath) + "Languages\Arabic.isl")
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "dist\Inventory.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Inventory"; Filename: "{app}\Inventory.exe"
Name: "{autodesktop}\Inventory"; Filename: "{app}\Inventory.exe"; Tasks: desktopicon

[Run]
; Normal install: offer to start the app on the last page.
Filename: "{app}\Inventory.exe"; Description: "{cm:LaunchProgram,Inventory}"; Flags: nowait postinstall skipifsilent
; Silent update started by the app itself (/RELAUNCH=1): reopen the app.
Filename: "{app}\Inventory.exe"; Flags: nowait; Check: WantRelaunch

[UninstallDelete]
; Program settings only. The data in Documents\Inventory is never touched.
Type: filesandordirs; Name: "{localappdata}\Inventory"

[Code]
function WantRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

{ The running app holds this mutex. Wait a few seconds for it to close
  (during an update it is closing itself), then give up politely. }
function AppClosed: Boolean;
var
  i: Integer;
begin
  for i := 1 to 50 do
  begin
    if not CheckForMutexes('InventoryAppMutex') then Break;
    Sleep(200);
  end;
  Result := not CheckForMutexes('InventoryAppMutex');
end;

function InitializeSetup: Boolean;
begin
  Result := AppClosed;
  if not Result then
    SuppressibleMsgBox('Please close Inventory, then run this setup again.', mbInformation, MB_OK, IDOK);
end;

function InitializeUninstall: Boolean;
begin
  Result := AppClosed;
  if not Result then
    SuppressibleMsgBox('Please close Inventory, then try again.', mbInformation, MB_OK, IDOK);
end;
