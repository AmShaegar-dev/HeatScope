; HeatScope — установщик (Inno Setup 6)
; Сборка: iscc.exe installer\HeatScope.iss

#define MyAppName "HeatScope"
#define MyAppVersion "0.1.1"
#define MyAppPublisher "HeatScope"
#define MyAppExeName "HeatScope.exe"

[Setup]
AppId={{7D3C4A21-9B6E-4E7B-9E51-2C0A8F4D5B13}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableDirPage=no
DisableProgramGroupPage=no
AllowNoIcons=yes
OutputDir=..\release
OutputBaseFilename=HeatScope-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\src\HeatScope\Assets\HeatScope.ico
UninstallDisplayName={#MyAppName}
SetupLogging=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce
Name: "pawnio"; Description: "Установить драйвер датчиков PawnIO (нужен для температур CPU/GPU)"; GroupDescription: "Компоненты:"; Flags: checkedonce

[Files]
Source: "..\dist\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme
Source: "..\vendor\PawnIO_setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "..\vendor\PawnIO-LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\PawnIO_setup.exe"; Parameters: "-install -silent"; StatusMsg: "Установка драйвера датчиков PawnIO..."; Flags: runhidden waituntilterminated; Tasks: pawnio
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// После установки показываем, установлен ли драйвер датчиков.
procedure CurStepChanged(CurStep: TSetupStep);
var
  Msg: String;
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('pawnio') then
      Msg := 'HeatScope установлен. Драйвер датчиков PawnIO установлен автоматически — температуры будут доступны сразу.'
    else
      Msg := 'HeatScope установлен без драйвера датчиков. Включить температуры можно кнопкой «Установить драйвер PawnIO» в приложении.';

    MsgBox(Msg, mbInformation, MB_OK);
  end;
end;
