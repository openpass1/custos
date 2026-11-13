# CUSTOS client for Windows

C# / .NET 10, x64. Specification: [`docs/CUSTOS-client.md`](../docs/CUSTOS-client.md), interface design: [`docs/client-ui.md`](../docs/client-ui.md).

| Project | Output | What it is |
|---|---|---|
| `src/Custos.Core` | `Custos.Core.dll` | Shared code: task model, schedule, copy engine, set manifests, Cobian import, the interface/service pipe protocol |
| `src/Custos.Service` | `CustosService.exe` | Windows service (LocalSystem): does all the work — queue, runs, VSS, share logons, database |
| `src/Custos.Gui` | `Custos.exe` | Tray interface, one per user session; only shows state and sends commands |
| `src/Custos.Updater` | `Custos.Updater.exe` | Installs an update from the monitoring server: stops the service, replaces the files, starts it again, rolls back on failure |
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

The installer appears in `artifacts\installer\CUSTOS-Setup-<version>.exe`, the update package for the monitoring
server in `artifacts\update\custos-<version>.zip` (the program files and `manifest.json` with the SHA-256 of each).
Options: `-SkipTests`, `-SkipInstaller`, `-Version 0.4.1`, `-TestUpdateVersion 0.4.1.1` (also builds the same code
with a higher version into `artifacts\update-test\custos-0.4.1.1-test.zip`, to try an update from the server on a
machine installed from this very build), `-RefreshRuntime`. The first build downloads the .NET Desktop Runtime
installer (about 60 MB) into `installer\redist`; it is bundled into setup.exe so that target machines need no internet.

`7z.dll` always comes from the 7-Zip build pinned in [`installer/7-zip.json`](installer/7-zip.json) (version, links and
SHA-256 of the official x64 installer and `7zr.exe` from [github.com/ip7z/7zip](https://github.com/ip7z/7zip/releases)).
The build downloads both once into `installer\redist\7-zip\<version>`, refuses a file whose SHA-256 differs, unpacks
`7z.dll` and the 7-Zip license from the installer and ships them; the tests use the same `7z.dll`. To move to a new
7-Zip, change the five values in `7-zip.json`. Once a month the workflow `7-Zip release check` opens an issue when a
newer 7-Zip is out, with the values to paste.

GitHub Actions runs the same script on every change under `client/` (workflow `Client build`) and keeps three files
for 14 days: the installer (`CUSTOS-Setup`, a zip around the .exe) and two files that go to the site as they are,
without unpacking: the update package `custos-0.4.<run number>.zip` and a test update
`custos-0.4.<run number>.1-test.zip`. Each run has its own version `0.4.<run number>`, so a newer build can be
installed over an older one from the server. The update package has the version of the installer of the same run: it
updates machines that run an older build. The test update is the same code with a higher version, to try an update
from the site on a machine that was just installed from this run. The page of a run says the same above the files.
The job `monitor` checks the whole protocol against a real CUSTOS Monitor (`server/`, PHP's own web server and
MariaDB). The job `destinations` runs whole backups into real servers on the runner: OpenSSH, vsftpd with and without
TLS, Apache WebDAV over http and https, MinIO with Object Lock ([`tools/ci-destinations.sh`](tools/ci-destinations.sh);
the same script starts the servers on a development machine and prints the `CUSTOS_TEST_*` variables the tests read).
MinIO no longer publishes programs or images, so the job builds the server from its source with Go at a fixed version
and keeps the result in the Actions cache.

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

## Копии на серверах: SFTP, FTP, WebDAV, S3

Назначением задания может быть сервер: «Сервер SFTP», «Сервер FTP», «Сервер FTP с шифрованием (FTPS)», «WebDAV»
(в том числе Nextcloud) и «Хранилище S3» (Amazon S3 и совместимые: MinIO, облачные провайдеры). На сервере работает
всё то же, что в папке: архивы и копии без сжатия, зеркало, хранение, проверка, восстановление.

- **Учётные записи** серверов добавляются там же, где учётные записи сетевых папок: «Настройки» → «Учётные записи»
  или кнопкой «Добавить…» в окне назначения. FTP и WebDAV: пользователь и пароль. S3: ключ доступа и секретный ключ.
  SFTP: пароль или закрытый ключ в формате OpenSSH или PEM без пароля на ключе (ключ PuTTY `.ppk` сначала
  преобразуйте: PuTTYgen → Conversions → Export OpenSSH key).
- **Ключ сервера.** При первом «Проверить доступ» к серверу SFTP программа показывает отпечаток его ключа
  (`SHA256:…`). Сравните его с тем, что показывает на сервере `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub`, и
  подтвердите. То же с сертификатом FTPS, WebDAV или S3, которому Windows не доверяет (самоподписанный). Если ключ
  или сертификат потом изменится, задание остановится, ничего не отправив, и скажет об этом в журнале: подтвердите
  новый в окне назначения, только если знаете, почему он изменился. С сайта мониторинга подтвердить ключ нельзя.
- **7z** пишется только туда, где можно вернуться к началу файла: папка, сетевая папка, USB-диск, SFTP. Для FTP,
  WebDAV и S3 выберите ZIP или копию без сжатия.
- **Ограничение скорости** задаётся у назначения в КБ/с (сетевые папки и серверы), 0 — без ограничения.
- **Зеркало на сервере** сравнивает файлы по размеру и по времени из своего списка прошлого запуска: большинство
  серверов не хранит время файла таким, каким его прислали. Файл, который кто-то заменил прямо на сервере другим
  того же размера, зеркало не заметит.
- Права NTFS и время изменения файлов на серверах не хранятся; при восстановлении время берётся из списка набора.

### S3: права ключа и защита от удаления (Object Lock)

CUSTOS бакеты не создаёт и их настройки не меняет. Создайте бакет сами и дайте ключу CUSTOS только эти права
(пример политики AWS; `office-backup` замените именем своего бакета):

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": [
        "s3:ListBucket",
        "s3:ListBucketVersions",
        "s3:GetBucketVersioning",
        "s3:GetBucketObjectLockConfiguration"
      ],
      "Resource": "arn:aws:s3:::office-backup"
    },
    {
      "Effect": "Allow",
      "Action": [
        "s3:GetObject",
        "s3:GetObjectVersion",
        "s3:GetObjectRetention",
        "s3:PutObject",
        "s3:PutObjectRetention",
        "s3:AbortMultipartUpload",
        "s3:DeleteObject",
        "s3:DeleteObjectVersion"
      ],
      "Resource": "arn:aws:s3:::office-backup/*"
    }
  ]
}
```

Чего в политике быть **не должно**: `s3:BypassGovernanceRetention` (снимает защиту в режиме Governance),
`s3:PutBucketObjectLockConfiguration`, `s3:PutBucketVersioning`, `s3:PutLifecycleConfiguration`, `s3:DeleteBucket`
и вообще `s3:*`. Тогда тот, кто получил ключ CUSTOS (например, шифровальщик на этом компьютере), защищённые копии
до срока удалить не сможет.

- **Object Lock включают при создании бакета**, потом это сделать нельзя (у MinIO: `mc mb --with-lock`). В окне
  назначения «Проверить доступ» пишет, есть ли он у бакета.
- «Защита от удаления», дней: каждый файл набора нельзя удалить или заменить столько дней после записи. Режим
  **Governance** (по умолчанию): владелец хранилища с особым правом может снять защиту, например чтобы освободить
  место после ошибки. Режим **Compliance**: до срока защиту не снимет никто, включая владельца и провайдера; место
  под такие копии оплачивается до конца срока.
- Защита работает с архивами и с «отдельной папкой для каждой копии». Зеркало и общая папка заменяют файлы, поэтому
  с защитой не сохраняются.
- Хранение пропускает набор, срок защиты которого не вышел (в журнале: «защищён от удаления до …»), и удаляет его
  первым запуском после срока, вместе со всеми версиями объектов. Дней защиты разумно ставить не больше срока
  хранения.
- Что неудачный запуск успел записать в бакет с защитой, лежит там до конца срока, как и всё остальное.
- Добавьте в бакет правило жизненного цикла, которое удаляет незавершённые загрузки (incomplete multipart uploads)
  старше нескольких дней: после сбоя связи или питания такие части остаются и занимают место.

## Облачные диски: Google Drive, Яндекс Диск, Dropbox

Назначением задания может быть облачный диск: «Google Диск», «Яндекс Диск», «Dropbox». CUSTOS видит на диске
только свою папку: у Google это папка «CUSTOS», которую создаёт сама программа (остальные файлы диска ей не
видны), у Яндекса и Dropbox — папка приложения («Приложения/…»). Архивы, хранение по GFS, проверка и
восстановление работают так же, как в папке.

- **Подключение аккаунта.** «Настройки» → «Облачные диски» → «Подключить облачный диск…» или кнопка «Подключить…» в окне
  назначения. Нужны права администратора: аккаунт пускает службу (SYSTEM) в чужое облако. Программа открывает
  страницу входа сервиса в браузере; после входа и согласия браузер сам возвращается в программу. Если браузер
  открыт на другом компьютере или вернуться не смог, скопируйте код со страницы сервиса (или адрес из строки
  браузера) в поле «Код или адрес» и нажмите «Подключить». Пароль от облака CUSTOS не видит и не хранит: хранится
  только токен доступа, зашифрованный для этого компьютера.
- **Аккаунт подключается на каждом компьютере отдельно.** С сайта мониторинга его не передать: на сайте в задании
  выбирается один из аккаунтов, уже подключённых на этой машине. Шаблон аккаунта не содержит: при применении берётся
  аккаунт того же сервиса, подключённый на машине (если их несколько, сначала задайте назначение в задании машины).
- **Отключение.** В настройках аккаунта сервиса отзовите доступ приложения CUSTOS (Google: «Сторонние приложения и
  сервисы», Яндекс: «Входы и устройства», Dropbox: «Подключённые приложения»). Задание скажет в журнале, что доступ
  отозван, и запишет остальные назначения. «Подключить заново…» в настройках CUSTOS возвращает доступ тому же
  аккаунту в программе: задания менять не нужно.
- **Только ZIP или копия без сжатия.** 7z в облако не пишется. Зеркало в облако не поддерживается.
- **Части архива.** «Размер части» (МБ) режет архив на файлы `.zip.001`, `.zip.002`…: 0 — одним файлом. Для Яндекс
  Диска по умолчанию 900 МБ: бесплатный тариф принимает файлы до 1 ГБ. Восстановление собирает части само.
- **Обрыв связи.** Google Drive и Dropbox продолжают загрузку с места обрыва. Яндекс Диск так не умеет: файл,
  загрузка которого оборвалась, не записывается, назначение для этого запуска считается неудачным (другие
  назначения задания пишутся), а следующий запуск начнёт файл заново. Поэтому на Яндекс Диск лучше писать частями.
- **Восстановление на другом компьютере.** Подключите там тот же аккаунт, затем в окне восстановления «Копии на
  сервере или в облаке» → «Указать…», выберите сервис, аккаунт и папку назначения.
- **Ограничение скорости** задаётся так же, как у серверов, в КБ/с.

### Приложения CUSTOS у сервисов

Чтобы войти в облако, у сервиса должно быть зарегистрировано приложение. Выпуски CUSTOS и сборки из GitHub Actions
несут приложение владельца проекта только для Google Drive. Для Яндекс Диска и Dropbox зарегистрируйте своё
приложение по шагам ниже: это бесплатно и занимает несколько минут. Своё приложение можно завести и для Google, а
также если вы собираете CUSTOS сами. Впишите его в «Настройки» → «Облачные диски» → «Свои приложения…» (только на
этом компьютере; задания и подключённые аккаунты при этом не меняются, аккаунт подключается заново кнопкой
«Подключить заново…»).
Названия пунктов на сайтах сервисов со временем меняются; суть та же.

**Google Drive** (https://console.cloud.google.com):

1. Создайте проект, например «CUSTOS».
2. «APIs & Services» → «Library» → «Google Drive API» → «Enable».
3. «Google Auth Platform» (раньше «OAuth consent screen») → «Get started»: имя приложения «CUSTOS», почта
   поддержки, тип «External», контактная почта. Логотип не загружайте: с ним Google требует проверку бренда.
4. «Data Access» → «Add or remove scopes» → отметьте `.../auth/drive.file` («See, edit, create, and delete only the
   specific Google Drive files you use with this app») → «Update» → «Save». Это неконфиденциальное разрешение:
   проверка приложения Google для него не нужна.
5. «Clients» → «Create client» → тип «Desktop app», имя «CUSTOS» → «Create». Сохраните Client ID и Client secret.
6. «Audience» → «Publish app» → состояние «In production». **Обязательно:** в состоянии «Testing» вход доступен
   только тестовым пользователям, а токены перестают действовать через 7 дней, и копии остановятся.

**Яндекс Диск** (https://oauth.yandex.ru):

1. «Создать приложение», название «CUSTOS», платформа «Веб-сервисы».
2. Redirect URI: `http://localhost:47901/` и отметьте «Подставить URL для отладки» (добавит
   `https://oauth.yandex.ru/verification_code`, на ней показывается код для ввода вручную).
3. Доступы: «Яндекс Диск REST API» → «Доступ к папке приложения на Диске» (`cloud_api:disk.app_folder`) и «Доступ к
   информации о Диске» (`cloud_api:disk.info`). Больше ничего не отмечайте.
4. Сохраните ClientID и Client secret.

**Dropbox** (https://www.dropbox.com/developers/apps):

1. «Create app» → «Scoped access» → «App folder» → имя приложения (уникальное во всём Dropbox, например
   «CUSTOS Backup <ваша организация>»).
2. Вкладка «Permissions»: отметьте `account_info.read`, `files.metadata.read`, `files.metadata.write`,
   `files.content.read`, `files.content.write` → «Submit».
3. Вкладка «Settings»: «App folder name» — `CUSTOS` (так папка и будет называться в «Приложения»); в «Redirect
   URIs» добавьте `http://localhost:47901/`; «Allow public clients (Implicit Grant & PKCE)» оставьте «Allow».
   Сохраните «App key» — это Client ID. Секрет CUSTOS не нужен.
4. В состоянии «Development» приложение подключают до 500 аккаунтов. Если нужно больше, «Apply for production».

Порт 47901 на компьютере должен быть свободен во время подключения (Google порт не важен). Если он занят, вход всё
равно возможен: код вводится вручную.

**Для владельца проекта: приложения в сборках GitHub Actions.** Идентификаторы в репозитории не хранятся. В
репозитории на GitHub: «Settings» → «Secrets and variables» → «Actions».

- Вкладка «Variables» → «New repository variable»: `CUSTOS_GDRIVE_CLIENT_ID`, `CUSTOS_YADISK_CLIENT_ID`,
  `CUSTOS_DROPBOX_CLIENT_ID`.
- Вкладка «Secrets» → «New repository secret»: `CUSTOS_GDRIVE_CLIENT_SECRET`, `CUSTOS_YADISK_CLIENT_SECRET`.

Следующая сборка встроит их в программу. Сервис, для которого значения не заданы, в сборке без приложения: его
можно подключить только со своим приложением. У приложения для компьютера секрет Google и Яндекса не настоящая
тайна (его можно достать из программы), безопасность входа держится на PKCE; в секретах GitHub он лежит, чтобы не
попасть в открытый репозиторий.

## Сервер мониторинга

Подключение необязательно: без сервера CUSTOS работает полностью. Для подключения нужна строка подключения. Её
копируют на сайте («Настройки» → «Подключение агентов»), в ней адрес сайта, код регистрации и отпечаток ключа
сервера: `http://custos.example.lan#K7QHM-2XARV-4NTB6-EWZ3P-LC5DQ#3F2A-91C0-77DE-0B14`. Строку вставляют в
установщике (страница «Сервер мониторинга») или потом: CUSTOS → «Настройки» → «Сервер мониторинга» → поле «Строка
подключения» → «Подключить» (нужны права администратора). Рядом с полем есть кнопка «Вставить из буфера обмена».
Отдельных полей для адреса и кода нет. Отпечаток защищает от подмены сервера при первом подключении. После
подключения в настройках виден адрес сервера, сама строка повторно не показывается.

После подключения компьютер появляется на сайте в разделе «Машины» со статусом «ожидает подтверждения». Пока его не
подтвердят, служба шлёт только heartbeat; после подтверждения — отчёты о запусках, прогресс, снимок настроек и
инвентаризацию копий (раз в 6 часов). Если сервер недоступен, отчёты ждут в очереди (`custos.db`, до 10 000) и уходят по порядку, когда связь вернётся. Состояние связи
видно в строке состояния главного окна и в настройках.

- **«Разрешить серверу изменять задания»** (в настройках, включено по умолчанию): без неё сервер может запускать,
  останавливать задания, запрашивать журналы и ставить обновления, но не менять задания и настройки.
- **Обновление с сервера:** пакет `custos-<версия>.zip` из сборки загружается на сайт («Обновления») и ставится на
  выбранные компьютеры. Служба проверяет подпись сервера и SHA-256, дожидается конца текущего задания и запускает
  `Custos.Updater.exe`: он останавливает службу, заменяет файлы (прежние сохраняются в
  `C:\ProgramData\CUSTOS\updates\backup-<версия>`), запускает службу и при неудаче возвращает прежнюю версию.
  Журнал — `C:\ProgramData\CUSTOS\logs\updater.log`, итог — в журнале Windows (4000 / 4001).
- **«Сбросить привязку к серверу…»** в настройках забывает сервер, его ключ, код и очередь отчётов. Ключ самого агента
  (`config\agent.key`) остаётся, поэтому после нового подключения сервер узнаёт машину: она возвращается с прежним
  статусом, без повторного подтверждения. Машина, удалённая на сайте, регистрируется заново и ждёт подтверждения.
- **Отключённая на сайте машина** работает автономно и шлёт только heartbeat с интервалом, который задаёт сервер
  (5 минут), поэтому после включения на сайте связь возвращается в течение нескольких минут.

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
| `C:\ProgramData\CUSTOS\config\server.json`, `agent.key` | The monitoring server, its pinned key and the registration code (DPAPI); the agent's own RSA key (DPAPI) |
| `C:\ProgramData\CUSTOS\updates\` | The 7-Zip installer downloaded by the update button and the new `7z.dll` until the service restarts; update packages from the server and the backup of the previous version. Only SYSTEM and Administrators may read the folder |
| `%APPDATA%\CUSTOS\ui.json` | Window size, card, columns of this Windows user |
| `%APPDATA%\CUSTOS\gui-errors.log` | Unexpected errors of the interface, with details |
| `C:\Program Files\CUSTOS\7z.dll`, `7-Zip-License.txt` | 7-Zip library for 7z archives (x64, GNU LGPL) and its license |
| `C:\Program Files\CUSTOS\THIRD-PARTY-NOTICES.txt` | Licenses of the other libraries CUSTOS ships with (SSH.NET, BouncyCastle, SharpZipLib, Serilog, SQLite …) |

The interface starts with Windows only in the tray (`Custos.exe --tray`, set by the installer); started from the
Start menu or by the installer it also opens its window. A second start only brings the running copy to the front.
The tray menu also starts and stops the service: it runs `Custos.exe --service-start` (or `--service-stop`) with
administrator rights, after the Windows UAC prompt.

## Diagnostics

- Service log: `C:\ProgramData\CUSTOS\logs\service-YYYYMMDD.log`, kept 180 days.
- Windows event log "Application", source `CUSTOS`: 1000 service started, 1001 service stopped,
  2000-2005 runs, 2006-2008 shutdown or sleep after a run (done, cancelled, failed), 3002 7z.dll missing or unusable,
  3003/3004 7-Zip updated / update failed, 1100/1101 monitoring server lost for 5 minutes / back, 4000/4001 update
  from the server installed / failed, 9000 unexpected error (details in the log file). Full list in section 6.10
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
