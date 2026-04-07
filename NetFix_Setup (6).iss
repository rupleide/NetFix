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
Source: "C:\Users\rubi\Downloads\windowsdesktop-runtime-8.0.25-win-x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

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

function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
  ErrorCode: Integer;
begin
  Result := True;
  
  if not IsDotNet8Installed() then
  begin
    if MsgBox('Для работы NetFix требуется .NET Desktop Runtime 8.0.' + #13#10#13#10 +
              'Сейчас будет запущена установка .NET Runtime.' + #13#10 +
              'Вы увидите окно установки Microsoft .NET.' + #13#10 +
              'Это займёт несколько минут.' + #13#10#13#10 +
              'Продолжить?', mbConfirmation, MB_YESNO) = IDYES then
    begin
      ExtractTemporaryFile('windowsdesktop-runtime-8.0.25-win-x64.exe');
      
      // Запускаем установщик .NET с видимым окном (убрали /quiet)
      if ShellExec('', ExpandConstant('{tmp}\windowsdesktop-runtime-8.0.25-win-x64.exe'), 
                   '/install /norestart', '', SW_SHOW, ewWaitUntilTerminated, ErrorCode) then
      begin
        if ErrorCode = 0 then
        begin
          MsgBox('.NET Runtime успешно установлен!' + #13#10 + 
                 'Сейчас продолжится установка NetFix.', mbInformation, MB_OK);
          Result := True;
        end
        else if ErrorCode = 1638 then
        begin
          // Уже установлена более новая версия
          MsgBox('.NET Runtime уже установлен.', mbInformation, MB_OK);
          Result := True;
        end
        else if ErrorCode = 1602 then
        begin
          // Пользователь отменил установку
          MsgBox('Установка .NET Runtime была отменена.' + #13#10 +
                 'NetFix не может работать без .NET Runtime.', mbError, MB_OK);
          Result := False;
        end
        else
        begin
          MsgBox('Установка .NET Runtime завершилась с ошибкой (код: ' + IntToStr(ErrorCode) + ').' + #13#10#13#10 +
                 'Попробуйте установить .NET Runtime вручную с сайта Microsoft:' + #13#10 +
                 'https://dotnet.microsoft.com/download/dotnet/8.0', mbError, MB_OK);
          Result := False;
        end;
      end
      else
      begin
        MsgBox('Не удалось запустить установку .NET Runtime.' + #13#10#13#10 +
               'Попробуйте установить его вручную с сайта Microsoft:' + #13#10 +
               'https://dotnet.microsoft.com/download/dotnet/8.0', mbError, MB_OK);
        Result := False;
      end;
    end
    else
    begin
      Result := False;
    end;
  end;
end;

[Run]
Filename: "{app}\NetFix.exe"; Description: "Запустить NetFix"; Flags: nowait postinstall skipifsilent runascurrentuser