; CUSTOS installer (draft, stage 0). Built by build.ps1, which passes the defines below.
; Section 10.1 of docs/CUSTOS-client.md. Still missing: the "monitoring server" wizard page (stage 3).

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
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
en.LaunchGui=Start the CUSTOS interface
ru.LaunchGui=Запустить интерфейс CUSTOS
en.RuntimeFailed=Installing the .NET 10 Desktop Runtime failed (code %1). CUSTOS cannot run without it.
ru.RuntimeFailed=Не удалось установить .NET 10 Desktop Runtime (код %1). Без него CUSTOS не работает.
en.ServiceFailed=The CUSTOS service did not start (code %1).%n%nSee the log files in %2 and the Windows "Application" event log, source CUSTOS.
ru.ServiceFailed=Служба CUSTOS не запустилась (код %1).%n%nПодробности в журналах в папке %2 и в журнале Windows «Приложение», источник CUSTOS.
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
; The previous 7z.dll, kept by hand for a rollback (client README).
Type: files; Name: "{app}\7z.dll.old"

[UninstallRun]
Filename: "{sys}\net.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden; RunOnceId: "DeleteService"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM Custos.exe"; Flags: runhidden; RunOnceId: "StopInterfaces"

[Code]
const
  ServiceName = '{#ServiceName}';

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
    RegisterAndStartService;
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
