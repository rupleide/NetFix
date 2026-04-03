[Setup]
AppId={{551BC3CA-4FAC-4797-8E9F-9BAA236BAEE9}
AppName=NetFix
AppVersion=1.0.6
AppPublisher=rupleide
DefaultDirName={autopf64}\NetFix
DefaultGroupName=NetFix
OutputDir=C:\Users\rubi\Desktop\NetFix_Installer
OutputBaseFilename=NetFix_Setup_v1.0.6
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "C:\Users\rubi\Desktop\NetFix\bin\x64\Release\net8.0-windows\NetFix.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "C:\Users\rubi\Desktop\NetFix\bin\x64\Release\net8.0-windows\NetFix.runtimeconfig.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "C:\Users\rubi\Desktop\NetFix\bin\x64\Release\net8.0-windows\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
; Source: "C:\Users\rubi\Downloads\dotnet-runtime-8.0.25-win-x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\NetFix"; Filename: "{app}\NetFix.exe"
Name: "{commondesktop}\NetFix"; Filename: "{app}\NetFix.exe"

[Code]
function IsDotNet8Installed(): Boolean;
var
  Path: String;
begin
  Path := ExpandConstant('{pf64}\dotnet\shared\Microsoft.WindowsDesktop.App');
  Result := DirExists(Path + '\8.0.25') or
            DirExists(Path + '\8.0.13') or
            DirExists(Path + '\8.0.10') or
            DirExists(Path + '\8.0.5') or
            DirExists(Path + '\8.0.0');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    if not IsDotNet8Installed() then
    begin
      MsgBox('Будет установлен .NET Desktop Runtime 8.0. Нажмите OK для продолжения.', mbInformation, MB_OK);
      Exec(ExpandConstant('{tmp}\windowsdesktop-runtime-8.0.25-win-x64.exe'),
        '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
    end;
  end;
end;

[Run]
Filename: "{app}\NetFix.exe"; Description: "Запустить NetFix"; Flags: nowait postinstall skipifsilent runascurrentuser