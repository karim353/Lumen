; Установщик Lumen (Inno Setup 6). Собирается из build.ps1:
;   ISCC.exe /DAppVersion=1.2.0 /DSourceDir=..\dist\app installer\Lumen.iss
; Ставит Lumen для текущего пользователя в %LocalAppData%\Programs\Lumen — без прав администратора.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\app"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

; Не менять: по AppId установщик узнаёт уже установленный Lumen и обновляет его
#define AppGuid "7D3C204F-E6CE-4297-9867-DDE36C7B483C"
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"

[Setup]
AppId={{{#AppGuid}}
AppName=Lumen
AppVersion={#AppVersion}
AppVerName=Lumen {#AppVersion}
AppPublisher=Lumen
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Установщик Lumen

PrivilegesRequired=lowest
DefaultDirName={autopf}\Lumen
DisableDirPage=auto
DisableProgramGroupPage=yes
DisableReadyPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; Файлы, занятые другими программами, закрывает Restart Manager (Lumen закрываем сами — см. StopLumen)
CloseApplications=yes
RestartApplications=no

WizardStyle=modern
ShowLanguageDialog=auto
WizardSmallImageFile=wizard-small-55.bmp,wizard-small-64.bmp
WizardImageAlphaFormat=defined
SetupIconFile=..\src\Lumen.App\Assets\lumen.ico
UninstallDisplayIcon={app}\Lumen.exe
UninstallDisplayName=Lumen

OutputDir={#OutputDir}
OutputBaseFilename=Lumen-{#AppVersion}-setup
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

[Languages]
; Язык выбирается по языку Windows, без лишнего окна; первый — для систем, язык которых не нашёлся в списке
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
english.CannotCloseLumen=Lumen is running and could not be closed automatically.%n%nRight-click the Lumen tray icon, choose «Выход» (Exit), then run Setup again.
russian.CannotCloseLumen=Lumen запущен, и закрыть его автоматически не удалось.%n%nЩёлкните правой кнопкой по иконке Lumen в трее, выберите «Выход» и запустите установку снова.
english.DeleteSettings=Also delete Lumen settings (lamps, schedule) and the log?
russian.DeleteSettings=Удалить также настройки Lumen (список ламп, расписание) и журнал?

[Tasks]
; Автозапуск предлагаем только при первой установке: дальше им управляет сам Lumen (Настройки → Автозапуск)
Name: "autostart"; Description: "{cm:AutoStartProgram,Lumen}"; Check: not IsUpgrade
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Lumen"; Filename: "{app}\Lumen.exe"
Name: "{autodesktop}\Lumen"; Filename: "{app}\Lumen.exe"; Tasks: desktopicon

[Registry]
; Та же запись, что пишет переключатель «Автозапуск» в самом Lumen
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "Lumen"; ValueData: """{app}\Lumen.exe"" --minimized"; Tasks: autostart

[Run]
Filename: "{app}\Lumen.exe"; Description: "{cm:LaunchProgram,Lumen}"; Flags: nowait postinstall skipifsilent

[Code]
const
  LumenMutex = 'Lumen.SingleInstance';
  EVENT_MODIFY_STATE = $0002;

function OpenEvent(dwDesiredAccess: DWORD; bInheritHandle: BOOL; lpName: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(hEvent: THandle): BOOL;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';

function IsUpgrade(): Boolean;
var
  Path: String;
begin
  Result := RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{{#AppGuid}}_is1',
    'UninstallString', Path);
end;

{ Просим запущенный Lumen закрыться (событие Lumen.Exit) и ждём, пока он отпустит мьютекс.
  Так он успевает остановить синхронизацию и сохранить настройки — в отличие от снятия процесса. }
function StopLumen(): Boolean;
var
  Handle: THandle;
  I: Integer;
begin
  Result := not CheckForMutexes(LumenMutex);
  if Result then Exit;

  Handle := OpenEvent(EVENT_MODIFY_STATE, False, 'Lumen.Exit');
  if Handle = 0 then Exit; { старая версия Lumen, без события — пусть пользователь закроет сам }
  try
    SetEvent(Handle);
    for I := 1 to 100 do
    begin
      if not CheckForMutexes(LumenMutex) then Break;
      Sleep(100);
    end;
  finally
    { Закрываем сразу: пока событие открыто, оно остаётся взведённым }
    CloseHandle(Handle);
  end;
  Result := not CheckForMutexes(LumenMutex);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if StopLumen() then Result := ''
  else Result := CustomMessage('CannotCloseLumen');
end;

function InitializeUninstall(): Boolean;
begin
  Result := StopLumen();
  if not Result then
    MsgBox(CustomMessage('CannotCloseLumen'), mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command, Settings: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;

  { Автозапуск убираем, только если он ведёт в эту папку: переносную копию Lumen не трогаем }
  if RegQueryStringValue(HKCU, '{#RunKey}', 'Lumen', Command) and
     (Pos(Lowercase(ExpandConstant('{app}\Lumen.exe')), Lowercase(Command)) > 0) then
    RegDeleteValue(HKCU, '{#RunKey}', 'Lumen');

  Settings := ExpandConstant('{userappdata}\Lumen');
  if DirExists(Settings) and not UninstallSilent() then
    if MsgBox(CustomMessage('DeleteSettings'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(Settings, True, True, True);
end;
