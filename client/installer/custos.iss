; CUSTOS installer. Built by build.ps1, which passes the defines below.
; Section 10.1 of docs/CUSTOS-client.md.

#if VER < EncodeVer(6, 3, 0)
  #error Inno Setup 6.3 or newer is required
#endif

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef FileVersion
  #define FileVersion AppVersion
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#define ServiceName "CustosService"
#define EventSource "CUSTOS"

[Setup]
AppId={{E3419532-55F2-4A91-A19E-0E603322B4C9}
AppName=CUSTOS
AppVersion={#AppVersion}
AppVerName=CUSTOS {#AppVersion}
AppPublisher=CUSTOS contributors
VersionInfoVersion={#FileVersion}
DefaultDirName={autopf}\CUSTOS
DisableDirPage=yes
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
PrivilegesRequired=admin
OutputDir={#OutputDir}
OutputBaseFilename=CUSTOS-Setup-{#AppVersion}
SetupIconFile=..\src\Custos.Gui\Resources\custos.ico
; The GPL is shown and must be accepted: CUSTOS comes without any warranty (sections 15 and 16).
LicenseFile=..\..\LICENSE
UninstallDisplayIcon={app}\Custos.exe
UninstallDisplayName=CUSTOS
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=auto
; The interface is closed by the script itself, in every session (see StopServiceAndInterfaces).
CloseApplications=no
SetupLogging=yes

[Languages]
; InfoBeforeFile: what the program sends over the network, shown before installing (PRIVACY.md, the code signing
; terms of SignPath Foundation ask for it).
Name: "en"; MessagesFile: "compiler:Default.isl"; InfoBeforeFile: "privacy.en.txt"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"; InfoBeforeFile: "privacy.ru.txt"

[CustomMessages]
en.LaunchGui=Start the CUSTOS interface
ru.LaunchGui=Запустить интерфейс CUSTOS
en.RuntimeFailed=Installing the .NET 10 Desktop Runtime failed (code %1). CUSTOS cannot run without it.
ru.RuntimeFailed=Не удалось установить .NET 10 Desktop Runtime (код %1). Без него CUSTOS не работает.
en.ServiceFailed=The CUSTOS service did not start (code %1).%n%nSee the log files in %2 and the Windows "Application" event log, source CUSTOS.
ru.ServiceFailed=Служба CUSTOS не запустилась (код %1).%n%nПодробности в журналах в папке %2 и в журнале Windows «Приложение», источник CUSTOS.
en.ServerTitle=Monitoring server
ru.ServerTitle=Сервер мониторинга
en.ServerSubtitle=Optional: connect this computer to a CUSTOS monitoring site.
ru.ServerSubtitle=Необязательно: подключить этот компьютер к сайту мониторинга CUSTOS.
en.ServerPrompt=Leave the field empty to work without a server or to keep the current connection. The line can be entered later in the CUSTOS settings.%n%nThe connection line is copied on the site: Settings, Connecting agents. It holds the address of the site and the registration code.
ru.ServerPrompt=Оставьте поле пустым, чтобы работать без сервера или сохранить текущее подключение. Строку можно ввести и позже, в настройках CUSTOS.%n%nСтрока подключения копируется на сайте: «Настройки» → «Подключение агентов». В ней адрес сайта и код регистрации.
en.ServerLine=Connection line:
ru.ServerLine=Строка подключения:
en.ServerPaste=Paste from the clipboard
ru.ServerPaste=Вставить из буфера обмена
en.ServerLineBad=This does not look like a connection line. Copy it on the site whole (Settings, Connecting agents): the address of the site, "#", the registration code, "#" and the fingerprint of the server key, for example%n%nhttps://custos.example.lan#K7QHM-2XARV-4NTB6-EWZ3P-LC5DQ#3F2A-91C0-77DE-0B14
ru.ServerLineBad=Это не похоже на строку подключения. Скопируйте её на сайте целиком («Настройки» → «Подключение агентов»): адрес сайта, «#», код регистрации, «#» и отпечаток ключа сервера, например%n%nhttps://custos.example.lan#K7QHM-2XARV-4NTB6-EWZ3P-LC5DQ#3F2A-91C0-77DE-0B14
en.RemoveData=Also delete the CUSTOS settings, logs and database in %1?%n%nThe backups themselves are never deleted.
ru.RemoveData=Удалить также настройки, журналы и базу CUSTOS в папке %1?%n%nСами резервные копии не удаляются никогда.

[Files]
Source: "{#PublishDir}\*"; Excludes: "7z.dll"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; 7z.dll has its own entry without ignoreversion: Setup compares the file versions, so a newer 7z.dll that an
; administrator put in by hand stays (section 10.2). An older one is replaced, the same version is left alone.
Source: "{#PublishDir}\7z.dll"; DestDir: "{app}"
#ifdef RuntimeInstaller
Source: "{#RuntimeInstaller}"; DestDir: "{tmp}"; Flags: dontcopy
#endif

[Icons]
Name: "{autoprograms}\CUSTOS"; Filename: "{app}\Custos.exe"

[Registry]
; Event log source "CUSTOS" in the Application log (section 6.10).
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Services\EventLog\Application\{#EventSource}"; ValueType: expandsz; ValueName: "EventMessageFile"; ValueData: "{app}\System.Diagnostics.EventLog.Messages.dll"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Services\EventLog\Application\{#EventSource}"; ValueType: dword; ValueName: "TypesSupported"; ValueData: "7"
; The interface starts at every user's logon (section 5).
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "CUSTOS"; ValueData: """{app}\Custos.exe"" --tray"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Custos.exe"; Description: "{cm:LaunchGui}"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallDelete]
; The previous 7z.dll, kept by hand or by the 7-Zip update button for a rollback (client README), and what an
; update could not delete while the old file was still loaded.
Type: files; Name: "{app}\7z.dll.old"
Type: files; Name: "{app}\7z.dll.new"
Type: files; Name: "{app}\7z.dll.old.*.delete"

[UninstallRun]
Filename: "{sys}\net.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden; RunOnceId: "DeleteService"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM Custos.exe"; Flags: runhidden; RunOnceId: "StopInterfaces"

[Code]
const
  ServiceName = '{#ServiceName}';

var
  ServerPage: TInputQueryWizardPage;

function RunHidden(const FileName, Params: String): Integer;
var
  ResultCode: Integer;
begin
  if Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := ResultCode
  else
    Result := -1;
  Log(Format('%s %s -> %d', [FileName, Params, Result]));
end;

function Sc(const Params: String): Integer;
begin
  Result := RunHidden(ExpandConstant('{sys}\sc.exe'), Params);
end;

function ServiceExists: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\' + ServiceName);
end;

{ Stops the service (net stop waits until it has stopped) and closes the interface in all sessions,
  so that their files can be replaced. }
procedure StopServiceAndInterfaces;
begin
  if ServiceExists then
    RunHidden(ExpandConstant('{sys}\net.exe'), 'stop ' + ServiceName);
  RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Custos.exe');
end;

#ifdef RuntimeInstaller
function IsDesktopRuntimeInstalled: Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec);
  if Result then
    FindClose(FindRec);
end;

{ Installs the .NET 10 Desktop Runtime bundled into setup.exe, so no internet is needed. Returns an error text or ''. }
function InstallDesktopRuntime: String;
var
  FileName, Params: String;
  ResultCode: Integer;
begin
  Result := '';
  FileName := '{#ExtractFileName(RuntimeInstaller)}';
  ExtractTemporaryFile(FileName);
  if WizardSilent then
    Params := '/install /quiet /norestart'
  else
    Params := '/install /passive /norestart';
  if not Exec(ExpandConstant('{tmp}\') + FileName, Params, '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
    ResultCode := -1;
  Log(Format('%s %s -> %d', [FileName, Params, ResultCode]));
  { 3010 and 1641 mean "installed, restart recommended": the runtime works without a restart. }
  if (ResultCode <> 0) and (ResultCode <> 3010) and (ResultCode <> 1641) then
    Result := FmtMessage(CustomMessage('RuntimeFailed'), [IntToStr(ResultCode)]);
end;
#endif

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  StopServiceAndInterfaces;
#ifdef RuntimeInstaller
  if not IsDesktopRuntimeInstalled then
    Result := InstallDesktopRuntime;
#endif
end;

function StartsText(const Prefix, S: String): Boolean;
begin
  Result := CompareText(Copy(S, 1, Length(Prefix)), Prefix) = 0;
end;

{ The connection line of the site (A.3), "http://custos.example.lan#KEY#FINGERPRINT": everything before the first
  "#" is the address, the rest the code. False when the text is not such a line. The service checks both again. }
function SplitConnection(const Line: String; var Url, Code: String): Boolean;
var
  Hash: Integer;
begin
  Result := False;
  Url := '';
  Code := '';
  if not (StartsText('http://', Line) or StartsText('https://', Line)) then
    Exit;
  Hash := Pos('#', Line);
  if Hash = 0 then
    Exit;
  Url := Trim(Copy(Line, 1, Hash - 1));
  Code := Trim(Copy(Line, Hash + 1, Length(Line)));
  Result := True;
end;

{ "Paste from the clipboard": the line copied on the site replaces what the field holds. Pascal Script has no
  clipboard functions, so the field itself is told to select everything (EM_SETSEL) and to paste (WM_PASTE);
  a clipboard without text changes nothing. }
procedure PasteServerLine(Sender: TObject);
begin
  WizardForm.ActiveControl := ServerPage.Edits[0];
  SendMessage(ServerPage.Edits[0].Handle, $00B1, 0, -1);
  SendMessage(ServerPage.Edits[0].Handle, $0302, 0, 0);
end;

{ The "Monitoring server" page (section 10.1, A.3): the connection line of the site, optional, and a button
  under the field that pastes it. }
procedure InitializeWizard;
var
  PasteButton: TNewButton;
begin
  ServerPage := CreateInputQueryPage(wpWelcome, CustomMessage('ServerTitle'), CustomMessage('ServerSubtitle'), CustomMessage('ServerPrompt'));
  ServerPage.Add(CustomMessage('ServerLine'), False);

  PasteButton := TNewButton.Create(ServerPage);
  PasteButton.Parent := ServerPage.Surface;
  PasteButton.Caption := CustomMessage('ServerPaste');
  PasteButton.Left := ServerPage.Edits[0].Left;
  PasteButton.Top := ServerPage.Edits[0].Top + ServerPage.Edits[0].Height + ScaleY(8);
  PasteButton.Width := ScaleX(190);
  PasteButton.Height := ScaleY(23);
  PasteButton.OnClick := @PasteServerLine;
end;

function WithoutSeparators(const S: String): String;
var
  I: Integer;
begin
  Result := '';
  for I := 1 to Length(S) do
    if (S[I] <> '-') and (S[I] <> ' ') then
      Result := Result + S[I];
end;

{ The same rules as the service (RegistrationCode.TryParse): 25 characters of base32, then optionally "#" and
  16 hex digits of the server key fingerprint. The service checks it again. }
function IsRegistrationCode(const Code: String): Boolean;
var
  Hash, I: Integer;
  Key, Fingerprint: String;
  C: Char;
begin
  Hash := Pos('#', Code);
  if Hash = 0 then
  begin
    Key := UpperCase(WithoutSeparators(Code));
    Fingerprint := '';
  end
  else
  begin
    Key := UpperCase(WithoutSeparators(Copy(Code, 1, Hash - 1)));
    Fingerprint := UpperCase(WithoutSeparators(Copy(Code, Hash + 1, Length(Code))));
  end;

  Result := Length(Key) = 25;
  for I := 1 to Length(Key) do
  begin
    C := Key[I];
    if not (((C >= 'A') and (C <= 'Z')) or ((C >= '2') and (C <= '7'))) then
      Result := False;
  end;

  if (Hash > 0) and (Length(Fingerprint) <> 16) then
    Result := False;
  for I := 1 to Length(Fingerprint) do
  begin
    C := Fingerprint[I];
    if not (((C >= '0') and (C <= '9')) or ((C >= 'A') and (C <= 'F'))) then
      Result := False;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Line, Url, Code: String;
begin
  Result := True;
  if CurPageID <> ServerPage.ID then
    Exit;

  Line := Trim(ServerPage.Values[0]);
  if Line = '' then
    Exit;

  if (not SplitConnection(Line, Url, Code)) or (Length(Url) < 10) or (not IsRegistrationCode(Code)) then
  begin
    MsgBox(CustomMessage('ServerLineBad'), mbError, MB_OK);
    Result := False;
  end;
end;

function JsonString(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
  Result := '"' + Result + '"';
end;

{ Hands the address and the code to the service in config\server-setup.json: the service connects with them at its
  start and deletes the file. On a first install the config folder is created here, so it is closed to everyone but
  SYSTEM and Administrators first, as the service would do (section 3.3): the code must not be readable by users. }
procedure WriteServerSetup;
var
  Url, Code, ConfigDir: String;
  Lines: TArrayOfString;
begin
  if ServerPage = nil then
    Exit;
  if not SplitConnection(Trim(ServerPage.Values[0]), Url, Code) then
    Exit;

  ConfigDir := ExpandConstant('{commonappdata}\CUSTOS\config');
  if not DirExists(ConfigDir) then
  begin
    ForceDirectories(ConfigDir);
    RunHidden(ExpandConstant('{sys}\icacls.exe'), '"' + ConfigDir + '" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F');
  end;

  SetArrayLength(Lines, 1);
  Lines[0] := '{"server_url": ' + JsonString(Url) + ', "registration_code": ' + JsonString(Code) + '}';
  if SaveStringsToUTF8File(ConfigDir + '\server-setup.json', Lines, False) then
    Log('Monitoring server address and code handed to the service: ' + Url)
  else
    Log('Could not write server-setup.json');
end;

{ Registers the service: automatic start as LocalSystem, restart after a crash (section 10.1). }
procedure RegisterAndStartService;
var
  BinPath, ErrorText: String;
  ResultCode: Integer;
begin
  { The path is quoted inside the value, otherwise Windows would try C:\Program.exe first. }
  BinPath := '"\"' + ExpandConstant('{app}\CustosService.exe') + '\""';
  if ServiceExists then
    Sc('config ' + ServiceName + ' binPath= ' + BinPath + ' start= auto')
  else
    Sc('create ' + ServiceName + ' binPath= ' + BinPath + ' start= auto DisplayName= "CUSTOS"');
  Sc('description ' + ServiceName + ' "CUSTOS backup service. Runs the backup tasks; the CUSTOS interface only shows and controls them."');
  Sc('failure ' + ServiceName + ' reset= 86400 actions= restart/60000/restart/60000/restart/60000');
  Sc('failureflag ' + ServiceName + ' 1');

  ResultCode := RunHidden(ExpandConstant('{sys}\net.exe'), 'start ' + ServiceName);
  if ResultCode <> 0 then
  begin
    { No line here may start with "[": Inno Setup would take it for a section header. }
    ErrorText := FmtMessage(CustomMessage('ServiceFailed'), [IntToStr(ResultCode), ExpandConstant('{commonappdata}\CUSTOS\logs')]);
    SuppressibleMsgBox(ErrorText, mbError, MB_OK, IDOK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    WriteServerSetup;
    RegisterAndStartService;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{commonappdata}\CUSTOS');
    if DirExists(DataDir) and not UninstallSilent then
      if MsgBox(FmtMessage(CustomMessage('RemoveData'), [DataDir]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
