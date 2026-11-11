# CUSTOS client for Windows

C# / .NET 10, x64. Specification: [`docs/CUSTOS-client.md`](../docs/CUSTOS-client.md), interface design: [`docs/client-ui.md`](../docs/client-ui.md).

| Project | Output | What it is |
|---|---|---|
| `src/Custos.Core` | `Custos.Core.dll` | Shared code: task model, schedule, copy engine, set manifests, Cobian import, the interface/service pipe protocol |
| `src/Custos.Service` | `CustosService.exe` | Windows service (LocalSystem): does all the work — queue, runs, VSS, share logons, database |
| `src/Custos.Gui` | `Custos.exe` | Tray interface, one per user session; only shows state and sends commands |
| `src/Custos.Updater` | `Custos.Updater.exe` | Update helper (placeholder until stage 3) |
| `tests/Custos.Core.Tests` | | xUnit tests of the core; `tests/fixtures` holds sample files for them |
| `tests/Custos.Gui.Tests` | | Window tests: open the real windows with made-up data (run on Windows only) |
| `installer/custos.iss` | | Inno Setup script |

## Build

Needs Windows x64 with the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and
[Inno Setup 6.3+](https://jrsoftware.org/isdl.php). [7-Zip x64](https://www.7-zip.org) is optional for now.

```powershell
cd client
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

The installer appears in `artifacts\installer\CUSTOS-Setup-<version>.exe`. Options: `-SkipTests`, `-SkipInstaller`,
`-Version 0.1.1`, `-RefreshRuntime`. The first build downloads the .NET Desktop Runtime installer (about 60 MB) into
`installer\redist`; it is bundled into setup.exe so that target machines need no internet.

GitHub Actions runs the same script on every change under `client/` (workflow `Client build`) and keeps the installer
as a downloadable artifact for 14 days.

For development: `dotnet build Custos.sln` and `dotnet test Custos.sln`. The solution also builds on Linux and macOS
(tests that need Windows are skipped there, and the window tests are not run).

The window tests open windows on the desktop for a moment; a test that waits longer than a minute (a message box
nobody clicks) fails instead of hanging the build.

## Files on the computer

| Where | What |
|---|---|
| `C:\ProgramData\CUSTOS\config\tasks.json`, `settings.json`, `credentials.json` | Tasks, settings, accounts (passwords encrypted with DPAPI). Only SYSTEM and Administrators may read the folder |
| `C:\ProgramData\CUSTOS\db\custos.db` | Run history and file states for incremental and differential backups |
| `C:\ProgramData\CUSTOS\logs\` | Service log; `runs\` holds one log file per run |
| `%APPDATA%\CUSTOS\ui.json` | Window size, card, columns of this Windows user |
| `%APPDATA%\CUSTOS\gui-errors.log` | Unexpected errors of the interface, with details |

The interface starts with Windows only in the tray (`Custos.exe --tray`, set by the installer); started from the
Start menu or by the installer it also opens its window. A second start only brings the running copy to the front.

## Diagnostics

- Service log: `C:\ProgramData\CUSTOS\logs\service-YYYYMMDD.log`, kept 180 days.
- Windows event log "Application", source `CUSTOS`: 1000 service started, 1001 service stopped,
  9000 unexpected error (details in the log file). Full list in section 6.10 of the specification.
- To watch the service live, stop it and start it in an administrator console:
  `sc.exe stop CustosService`, then `& "C:\Program Files\CUSTOS\CustosService.exe"` (Ctrl+C to stop), then
  `sc.exe start CustosService`.

## Interface ↔ service pipe

`\\.\pipe\custos`. Each message is a 4-byte little-endian length followed by UTF-8 JSON:
`{"kind":"request","id":1,"method":"hello","data":{...}}`, answered by `{"kind":"response","id":1,"data":{...}}` or
`{"kind":"response","id":1,"error":{"code":"...","message":"..."}}`; the service also sends
`{"kind":"event","event":"service.state","data":{...}}` at any time. Types are in `src/Custos.Core/Ipc`.

Any signed-in local user may connect; network clients are refused. The interface connects at the Identification
impersonation level, so the service can see who is calling (and whether they are an administrator) but cannot act
on their behalf. The service creates the pipe with `FirstPipeInstance`, so another program cannot occupy the name first.
