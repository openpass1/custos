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
[Inno Setup 6.3+](https://jrsoftware.org/isdl.php). 7-Zip need not be installed (see below).

```powershell
cd client
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

The installer appears in `artifacts\installer\CUSTOS-Setup-<version>.exe`. Options: `-SkipTests`, `-SkipInstaller`,
`-Version 0.1.1`, `-RefreshRuntime`. The first build downloads the .NET Desktop Runtime installer (about 60 MB) into
`installer\redist`; it is bundled into setup.exe so that target machines need no internet.

`7z.dll` always comes from the 7-Zip build pinned in [`installer/7-zip.json`](installer/7-zip.json) (version, links and
SHA-256 of the official x64 installer and `7zr.exe` from [github.com/ip7z/7zip](https://github.com/ip7z/7zip/releases)).
The build downloads both once into `installer\redist\7-zip\<version>`, refuses a file whose SHA-256 differs, unpacks
`7z.dll` and the 7-Zip license from the installer and ships them; the tests use the same `7z.dll`. To move to a new
7-Zip, change the five values in `7-zip.json`. Once a month the workflow `7-Zip release check` opens an issue when a
newer 7-Zip is out, with the values to paste.

GitHub Actions runs the same script on every change under `client/` (workflow `Client build`) and keeps the installer
as a downloadable artifact for 14 days.

For development: `dotnet build Custos.sln` and `dotnet test Custos.sln`. The solution also builds on Linux and macOS
(tests that need Windows are skipped there, and the window tests are not run).

The window tests open windows on the desktop for a moment; a test that waits longer than a minute (a message box
nobody clicks) fails instead of hanging the build.

## Archives

A task can pack each set into one 7z or ZIP archive (the "Compression and checks" page of the task). They are ordinary
archives: 7-Zip, Windows Explorer (ZIP without a password) and other archivers open them without CUSTOS. Encrypted
archives use AES-256 and ask for the task's archive password. Each archive also holds `custos-set.json`, the list of
the set's files with their SHA-256, which restore and the checks use. A split archive is `name.7z.001`, `.002`, ...;
open the first part.

## Базы данных (MySQL, MariaDB, PostgreSQL, OSPanel)

Страница задания «Базы данных» → «Добавить» → «Найти…»: CUSTOS покажет запущенные серверы (с адресом и портом,
на которых они работают; у OSPanel 6 это адреса вида `127.127.126.x`) и найденные папки программ. Выберите сервер,
введите пользователя (`root` для MySQL и MariaDB, `postgres` для PostgreSQL) и пароль или отметьте «Без пароля»,
нажмите «Проверить подключение». Появится список баз, отмеченные попадают в копию. «Все базы»: отмечено всё, новые
базы добавятся сами; если снять галочку с базы, выбор переключится на «Выбранные базы», где выгружаются только
отмеченные.

- Каждый запуск выгружает базы целиком в папку `db` копии: `db\mysql\<база>.sql` и `db\mysql\_users.sql`
  (пользователи и права). Копировать папку данных сервера файлами не нужно.
- Если сервер не запущен (например, OSPanel выключена) или дамп оборвался, запуск завершается ошибкой, но файлы и
  остальные базы копируются, а старые копии не удаляются.
- При восстановлении дампы ложатся файлами `.sql` в папку `db` вместе с `README.txt`, где написаны команды для их
  загрузки в сервер (запускать в `cmd`, не в PowerShell). Сам CUSTOS дампы в сервер не загружает. Для восстановления
  в исходные места укажите отдельную папку «Дампы баз в папку».
- MS SQL и 1С выгружаются своими программами через страницу «События» (до копирования) в папку-источник.

## Linux-серверы (custos-helper по SSH)

Страница задания «Источники» → «Linux-сервер…». Спецификация: [`docs/CUSTOS-remote-pull.md`](../docs/CUSTOS-remote-pull.md),
сам скрипт на сервере и его ручная установка: [`helper/README.md`](../helper/README.md).

1. Адрес сервера, порт SSH и IP офиса (внешний адрес этого компьютера: ключ будет работать только с него).
2. Что забирать: «ispmanager: только сайты» (по умолчанию), «ispmanager: сайты и панель» или «Обычный Linux»;
   папки, исключения и СУБД можно поправить.
3. «Создать ключ»: служба делает ключ ed25519, закрытая часть остаётся у неё (DPAPI) и никуда не уходит.
4. Блок под ключом вставляется целиком в сессию root на сервере (после `sudo -i`), кнопка «Копировать» или
   «Сохранить как файл…». Он создаёт пользователя `custos`, ставит custos-helper (сверяя SHA-256), его настройки,
   sudo только для custos-helper и ключ. Повторный запуск безопасен; после изменений в окне его надо выполнить снова.
5. «Проверить подключение»: CUSTOS покажет ключ сервера, его нужно сверить на сервере
   (`for f in /etc/ssh/ssh_host_*_key.pub; do ssh-keygen -lf "$f"; done`) и подтвердить. Затем видны версия
   custos-helper, базы, число файлов и объём.
6. «Базы данных в копии»: базы сервера появляются в списке после проверки, как у баз OSPanel. «Все базы» — всё
   отмечено, новые базы попадут в копию сами; снятая галочка переключает выбор на «Выбранные базы», где выгружаются
   только отмеченные. Выбор хранится в задании, блок на сервере после него выполнять заново не нужно.

- В задании с Linux-сервером других источников нет, зеркало недоступно, фильтры страницы не применяются.
- Набор: `files.tar` (все файлы или изменённые), `db\<СУБД>\<база>.sql`, `_users.sql` и `custos-set.json`.
  Tar на Windows не распаковывается никогда.
- Ключ сервера изменился, сервер недоступен (три попытки с паузой в минуту), дамп оборвался — запуск с ошибкой.
- Восстановление — только в папку: tar на каждую папку сервера (`var-www.tar` …), дампы и `README.txt` с командами
  для сервера. Настройки (`/etc` и папки `etc`) распаковываются на сервере в `/root/old-etc`, не поверх системы.
- Если сервер отдаёт данные быстрее, чем архив успевает сжимать (больше 256 МБ ждут в памяти), запуск прерывается:
  задайте ограничение скорости в окне сервера или уменьшите степень сжатия.

## Переход с Cobian Backup

Главное окно → «Импорт из Cobian Backup…» → файл `MainList.lst` (обычно
`C:\Program Files (x86)\Cobian Backup 11\DB`). Задания добавляются отключёнными, чтобы не работать параллельно с
Cobian: проверьте каждое в редакторе и включите, а в Cobian выключите.

- События до и после Cobian записывает в своём формате, поэтому импорт кладёт их текстом в комментарий задания.
  Задайте их заново на странице «События».
- Пару заданий по одной папке «каждый день инкрементальное» + «раз в неделю полное» лучше заменить одним
  инкрементальным заданием: «Принудительная полная копия» → «В день недели» и хранение «Дед-отец-сын» (например, 7 дней,
  4 недели, 12 месяцев). Так копии не мешают друг другу, а старые удаляются целыми цепочками.

## Замена 7z.dll

CUSTOS работает с архивами 7z через `C:\Program Files\CUSTOS\7z.dll` из 64-битной 7-Zip. Новую версию 7-Zip можно
поставить самому, не дожидаясь новой сборки CUSTOS:

1. Скачать и установить 64-битную 7-Zip (x64) с [7-zip.org](https://www.7-zip.org) или
   [github.com/ip7z/7zip](https://github.com/ip7z/7zip/releases) на любой компьютер.
2. На компьютере с CUSTOS открыть командную строку от имени администратора (в PowerShell сначала набрать `cmd`) и
   остановить службу: `net stop CustosService`.
3. Сохранить старый файл для отката:
   `copy /y "C:\Program Files\CUSTOS\7z.dll" "C:\Program Files\CUSTOS\7z.dll.old"`.
4. Скопировать новый `7z.dll` из папки 7-Zip (обычно `C:\Program Files\7-Zip\7z.dll`) в `C:\Program Files\CUSTOS\`
   с заменой.
5. Запустить службу: `net start CustosService`.
6. Проверить версию: CUSTOS → «Настройки», раздел «Архиватор». Там же будет написано, если файл не подошёл (например,
   32-битный). Версия есть и в журнале службы `C:\ProgramData\CUSTOS\logs\service-ГГГГММДД.log`.

Откат: так же остановить службу, скопировать `7z.dll.old` обратно в `7z.dll`, запустить службу.

Проще — кнопкой: CUSTOS → «Настройки» → «Архиватор» → «Проверить обновление 7-Zip» (нужны права администратора и
интернет). Служба покажет установленную и последнюю версию и после согласия сама скачает 64-битный установщик 7-Zip с
GitHub, сверит его SHA-256 с опубликованным, проверит новый `7z.dll` и заменит старый (он останется как `7z.dll.old`).
Пока идёт задание или восстановление, обновление не начинается. Сама служба 7-Zip не проверяет: только по кнопке.

Если `7z.dll` нет или он не подходит, служба при запуске пишет об этом в журнал Windows «Приложение» (событие 3002), а
ошибкой завершаются только задания с форматом 7z, с понятной причиной в журнале запуска. Задания с ZIP и без сжатия
работают как обычно. Установщик CUSTOS не заменяет
более новый `7z.dll` более старым.

## Files on the computer

| Where | What |
|---|---|
| `C:\ProgramData\CUSTOS\config\tasks.json`, `settings.json`, `credentials.json` | Tasks, settings, accounts (passwords encrypted with DPAPI). Only SYSTEM and Administrators may read the folder |
| `C:\ProgramData\CUSTOS\db\custos.db` | Run history and file states for incremental and differential backups |
| `C:\ProgramData\CUSTOS\logs\` | Service log; `runs\` holds one log file per run |
| `C:\ProgramData\CUSTOS\updates\` | The 7-Zip installer downloaded by the update button and the new `7z.dll` until the service restarts. Only SYSTEM and Administrators may read the folder |
| `%APPDATA%\CUSTOS\ui.json` | Window size, card, columns of this Windows user |
| `%APPDATA%\CUSTOS\gui-errors.log` | Unexpected errors of the interface, with details |
| `C:\Program Files\CUSTOS\7z.dll`, `7-Zip-License.txt` | 7-Zip library for 7z archives (x64, GNU LGPL) and its license |
| `C:\Program Files\CUSTOS\THIRD-PARTY-NOTICES.txt` | Licenses of the other libraries CUSTOS ships with (SSH.NET, BouncyCastle, SharpZipLib, Serilog, SQLite …) |

The interface starts with Windows only in the tray (`Custos.exe --tray`, set by the installer); started from the
Start menu or by the installer it also opens its window. A second start only brings the running copy to the front.

## Diagnostics

- Service log: `C:\ProgramData\CUSTOS\logs\service-YYYYMMDD.log`, kept 180 days.
- Windows event log "Application", source `CUSTOS`: 1000 service started, 1001 service stopped,
  2000-2005 runs, 2006-2008 shutdown or sleep after a run (done, cancelled, failed), 3002 7z.dll missing or unusable,
  3003/3004 7-Zip updated / update failed, 9000 unexpected error (details in the log file). Full list in section 6.10
  of the specification.
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
