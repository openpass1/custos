# CUSTOS — общий контракт клиента и сервера

Приложение A к обоим техническим заданиям проекта: [`CUSTOS-client.md`](CUSTOS-client.md) (программа для Windows) и [`CUSTOS-monitor.md`](CUSTOS-monitor.md) (сервер мониторинга). Ссылки вида «A.9» в обоих ТЗ ведут сюда.

> Клиент и сервер разрабатываются отдельно, поэтому этот файл единственное, что их связывает. Контракт существует **в одном экземпляре**: любая правка вносится только сюда.

Версия протокола: **1**.

### A.1. Общие правила

- Транспорт: HTTP или HTTPS, JSON в UTF-8, `Content-Type: application/json`.
- Базовый адрес: `{server_url}/api/v1/`. `server_url` задаётся в клиенте, например `http://custos.lan`. Сервер может стоять и в подпапке.
- Каждый запрос агента несёт заголовок `X-Custos-Protocol: 1`.
- Время везде в UTC, формат ISO 8601: `2026-10-12T21:00:00Z`. Агент дополнительно сообщает свой часовой пояс в формате IANA (`Europe/Moscow`). В .NET его даёт `TimeZoneInfo.TryConvertWindowsIdToIanaId`.
- Идентификаторы: UUID v4 в виде строк. Агент сам генерирует id заданий, назначений, источников, учётных данных и запусков. Сервер генерирует `agent_id` и id команд.
- Сжатые поля (журналы): gzip, затем base64, поле с суффиксом `_gz_b64`.
- Размер тела запроса не больше 8 МБ. Полный журнал обрезается до 5 МБ после сжатия.

### A.2. Ключи и подписи

HTTP внутри локальной сети допустим, потому что подлинность обеих сторон обеспечивают подписи, а не транспорт.

- **Ключ агента.** RSA-3072, создаётся при первом запуске службы. Закрытый ключ хранится в `C:\ProgramData\CUSTOS\` под защитой DPAPI от имени SYSTEM.
- **Ключ сервера.** RSA-3072, создаётся при установке сервера (`bin/install.php`). Закрытый ключ лежит вне веб-корня.
- **Подпись:** RSASSA-PKCS1-v1_5 + SHA-256, результат в base64. PHP: `openssl_sign(..., OPENSSL_ALGO_SHA256)`. .NET: `RSA.SignData(..., HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)`.
- **Отпечаток ключа:** первые 16 hex-символов SHA-256 от DER открытого ключа, группами по 4: `3F2A-91C0-77DE-0B14`. Показывается в GUI агента и на сайте, чтобы администратор мог их сверить.

**Запрос агента** (всё, кроме регистрации) несёт заголовки:

```
X-Custos-Agent: <agent_id>
X-Custos-Time: <unix-время, секунды>
X-Custos-Nonce: <32 hex-символа, случайные>
X-Custos-Signature: <base64>
```

Подписывается строка `"{METHOD}\n{PATH}\n{TIME}\n{NONCE}\n{sha256hex(body)}"`, где `PATH` — путь запроса вместе со строкой запроса, как он пришёл на сервер (`/api/v1/agent/heartbeat`). Для пустого тела берётся SHA-256 от пустой строки.

Сервер проверяет подпись открытым ключом агента, расхождение времени (не больше 300 с) и уникальность nonce для этого агента в течение 10 минут. При ошибке отвечает 401.

**Ответ сервера** несёт заголовки `X-Custos-Time` и `X-Custos-Signature`. Подписывается строка `"{TIME}\n{NONCE из запроса}\n{sha256hex(body)}"`. Агент проверяет её закреплённым открытым ключом сервера. Ответ с неверной подписью агент отбрасывает так же, как недоступность сервера, и пишет об этом в журнал. Поэтому подменить команды в сети нельзя.

**Секреты, которые сервер передаёт агенту** (пароли шар, FTP, ключи S3 и SFTP, пароль архива), шифруются гибридно. Случайный ключ AES-256 шифруется RSA-OAEP (SHA-1, MGF1-SHA1) на открытом ключе агента, сами данные шифруются AES-256-GCM.

`secret_enc` = base64( зашифрованный ключ, 384 байта ‖ nonce, 12 байт ‖ шифртекст ‖ tag, 16 байт ).

- PHP: `openssl_public_encrypt(..., OPENSSL_PKCS1_OAEP_PADDING)` и `openssl_encrypt('aes-256-gcm', ..., OPENSSL_RAW_DATA, $nonce, $tag)`.
- .NET: `RSA.Decrypt(..., RSAEncryptionPadding.OaepSHA1)` и `AesGcm`.

Агент расшифровывает секрет и сразу сохраняет его через DPAPI. **Сервер не хранит секреты в открытом виде и никогда не получает их от агента.**

**Пароль программы** передаётся только как хэш: `pbkdf2-sha256$<итерации>$<salt base64>$<hash base64>`, 200 000 итераций, соль 16 байт, хэш 32 байта. Сервер вычисляет его через `hash_pbkdf2`, агент через `Rfc2898DeriveBytes.Pbkdf2`.

### A.3. Регистрация агента

1. Администратор задаёт на сервере общий ключ регистрации.
2. Агент отправляет `POST /api/v1/agent/register` без заголовков подписи:

```json
{
  "registration_key": "общий-ключ",
  "hostname": "PC-01",
  "machine_guid": "значение HKLM\\SOFTWARE\\Microsoft\\Cryptography\\MachineGuid",
  "os": "Windows 11 Pro 24H2 (26100)",
  "agent_version": "1.0.0",
  "time_zone": "Europe/Moscow",
  "public_key": "-----BEGIN PUBLIC KEY-----\n...",
  "proof": "base64 подписи строки \"{registration_key}\\n{hostname}\\n{machine_guid}\" ключом агента"
}
```

3. Ответ `200`:

```json
{ "agent_id": "uuid", "status": "pending", "server_public_key": "-----BEGIN PUBLIC KEY-----\n...", "server_name": "CUSTOS" }
```

   Неверный ключ: `403 bad_registration_key`. Агент закрепляет ключ сервера (TOFU) и показывает в GUI «Ожидает подтверждения на сервере» с отпечатками обоих ключей.
4. Администратор подтверждает агента на сайте. До подтверждения сервер принимает от агента только heartbeat. Отчёты о запусках копятся в очереди агента.
5. **Повторная установка.** Если с тем же `machine_guid` уже есть агент, новая регистрация попадает в ожидающие с пометкой «повторная регистрация». Администратор выбирает: заменить существующий (история и задания сохраняются, ключ меняется) или завести новый.
6. Если сервер отвечает `404 unknown_agent` (агента удалили), агент сбрасывает `agent_id` и регистрируется заново.

### A.4. Heartbeat и команды

`POST /api/v1/agent/heartbeat` отправляется каждые `heartbeat_interval_s` секунд (по умолчанию 60). Это быстрый «пульс», через него же приходят команды.

Запрос:

```json
{
  "agent_version": "1.0.0",
  "config_revision": 42,
  "time_zone": "Europe/Moscow",
  "service_started_at": "2026-10-12T05:01:10Z",
  "state": "idle",
  "running": null,
  "queue": ["task-uuid-2", "task-uuid-5"]
}
```

`state`: `idle` или `running`. Во время работы `running` = `{"run_id": "...", "task_id": "..."}`. `queue` — задания, стоящие в очереди.

Ответ:

```json
{
  "status": "approved",
  "server_time": "2026-10-12T13:00:00Z",
  "heartbeat_interval_s": 60,
  "want_config": false,
  "commands": [
    { "id": "cmd-uuid", "kind": "task.run", "payload": { "task_id": "..." }, "expires_at": "2026-10-12T14:00:00Z" }
  ]
}
```

- `status`: `pending`, `approved` или `disabled`. Отключённый (`disabled`) агент продолжает работать автономно и шлёт только heartbeat раз в 10 минут.
- `want_config: true` значит, что сервер просит полный снимок конфигурации (A.6). Так бывает после подтверждения или при расхождении `config_revision`.
- Команду с истёкшим `expires_at` агент отклоняет.

Результат команды: `POST /api/v1/agent/commands/{id}/result` с телом `{"status": "done|failed|rejected", "message": "..."}`.

| `kind` | `payload` | Что делает агент |
|---|---|---|
| `task.run` | `{task_id}` | Ставит задание в очередь с `trigger: "remote"` |
| `task.stop` | `{task_id}` | Прерывает выполнение (статус `aborted`) |
| `task.pause` / `task.resume` | `{task_id}` | Пауза и продолжение |
| `task.enable` / `task.disable` | `{task_id}` | Включает или отключает задание |
| `task.upsert` | `{task, base_revision, secrets}` | Создаёт или изменяет задание (см. ниже) |
| `task.delete` | `{task_id}` | Удаляет задание (сами копии в хранилище не трогает) |
| `settings.update` | `{settings}` (частично) | Меняет настройки из A.9, кроме локальных |
| `log.fetch` | `{run_id}` | Выгружает полный журнал запуска (A.7) |
| `inventory.refresh` | `{}` | Отправляет инвентаризацию (A.8) |
| `update.install` | `{version, sha256, signature}` | Скачивает и ставит обновление (A.10) |

Если на агенте выключено «Разрешить серверу менять задания», команды `task.upsert`, `task.delete`, `task.enable`, `task.disable` и `settings.update` отклоняются (`rejected`).

**`task.upsert` и конфликты.** `secrets` — объект `{ "<credential_id>": { "kind", "user", "domain", "secret_enc" } }`, где `secret_enc` зашифрован по A.2. Правило конфликтов: **побеждает последняя правка**. Агент применяет изменение, даже если `base_revision` не совпадает с текущей ревизией задания. В этом случае он отвечает `done` с `message: "conflict: local revision N overwritten"` и прикладывает перезаписанную версию к ближайшему снимку конфигурации в поле `overwritten`. Новая ревизия задания = max(локальная, `base_revision`) + 1, `updated_by` = `server:<логин>`.

### A.5. Запуски

`POST /api/v1/agent/runs/start`:

```json
{
  "run_id": "uuid", "task_id": "uuid", "task_revision": 7,
  "backup_type": "incremental",
  "trigger": "schedule",
  "scheduled_for": "2026-10-12T21:00:00Z",
  "started_at": "2026-10-12T21:00:02Z"
}
```

- `trigger`: `schedule`, `catch_up` (догоняющий запуск после выключения), `manual` (кнопка в GUI) или `remote` (команда с сервера).
- `scheduled_for` — слот расписания, к которому относится запуск. Для `schedule` и `catch_up` он обязателен, для `manual` и `remote` равен `null`. По нему сервер сопоставляет запуски с ожидаемым расписанием.

`POST /api/v1/agent/runs/progress` отправляется каждые 10 секунд во время выполнения. В очередь не ставится.

```json
{
  "run_id": "uuid", "phase": "copy", "percent": 37.5,
  "files_done": 1200, "files_total": 3200, "bytes_done": 104857600, "bytes_total": 279620266,
  "speed_bps": 52428800, "eta_s": 210, "current_file": "D:\\Data\\reports\\2026-10.pdf"
}
```

`phase`: `pre_events`, `vss`, `scan`, `copy`, `verify`, `retention`, `post_events`.

`POST /api/v1/agent/runs/finish`:

```json
{
  "run_id": "uuid", "task_id": "uuid", "status": "warning",
  "finished_at": "2026-10-12T21:04:40Z",
  "source_files": 3200, "source_bytes": 279620266,
  "files_processed": 3197, "bytes_written": 98112000,
  "errors": 0, "warnings": 3,
  "error_message": null,
  "destinations": [
    { "destination_id": "uuid", "status": "success", "set_name": "Documents 2026-10-12 21-00 (Incremental)", "bytes": 98112000, "error": null }
  ],
  "verify": { "performed": true, "ok": true, "scope": "new", "sets_checked": 1, "failed_sets": [] },
  "retention": { "deleted_sets": 1 },
  "log_tail": "последние 100 строк журнала",
  "log_gz_b64": "есть только при status warning или error"
}
```

- `status`: `success`, `warning` (выполнено, но часть файлов пропущена), `error` или `aborted` (остановлено пользователем или командой).
- `source_files` и `source_bytes` описывают весь источник на момент запуска, даже при инкрементальной копии: по ним сервер ищет аномалии.
- `verify.scope`: `new` (проверена только что созданная копия) или `stored` (плановая проверка всех хранящихся наборов). Если какой-то набор не прошёл проверку, его имя попадает в `failed_sets`, а статус запуска становится `error` с `error_message` вида «Повреждён набор …».
- `start` и `finish` идемпотентны по `run_id`. При недоступности сервера они сохраняются в локальной очереди агента и отправляются позже строго по порядку.

### A.6. Снимок конфигурации

`POST /api/v1/agent/config` отправляется через 10 секунд после последнего локального изменения и по запросу `want_config`.

```json
{
  "config_revision": 43,
  "changed": [ { "task_id": "uuid", "revision": 8, "updated_by": "local:OFFICE\\user1", "updated_at": "..." } ],
  "settings": { "...": "объект A.9" },
  "credentials": [ { "id": "uuid", "kind": "windows", "user": "backup1", "domain": ".", "has_secret": true } ],
  "tasks": [ "объекты A.9 без секретов" ],
  "deleted_task_ids": [],
  "overwritten": [ "перезаписанные версии заданий (A.4), если были" ]
}
```

`updated_by` имеет вид `local:<ДОМЕН\пользователь>` для правок в программе или `server:<логин>` для правок с сайта. Правки `local:` сайт показывает ненавязчивым уведомлением.

### A.7. Журнал по запросу

`POST /api/v1/agent/runs/{run_id}/log` с телом `{"log_gz_b64": "..."}`. Это ответ на команду `log.fetch`. Если журнал уже удалён по сроку хранения, команда завершается `failed`.

### A.8. Инвентаризация хранилищ

`POST /api/v1/agent/inventory` отправляется после каждого запуска, раз в 6 часов и по команде.

```json
{
  "destinations": [
    {
      "task_id": "uuid", "destination_id": "uuid", "kind": "unc", "path": "\\\\fileserver\\docs",
      "total_bytes": 4000000000000, "free_bytes": 1250000000000,
      "sets": [ { "name": "Documents 2026-10-12 00-30 (Full)", "type": "full", "time": "2026-10-11T21:30:00Z", "bytes": 5100000000, "verified_at": "2026-10-12T21:10:00Z", "verify_ok": true } ]
    }
  ]
}
```

Для FTP, WebDAV и S3 поля `total_bytes` и `free_bytes` могут быть `null`. Если набор ещё не проверялся, `verified_at` и `verify_ok` равны `null`.

### A.9. Объекты: задание, учётные данные, настройки

**Задание** (`task`). Значения по умолчанию указаны в комментариях. Неизвестные поля обе стороны сохраняют и не удаляют: это защита от расхождения версий.

```jsonc
{
  "id": "uuid",
  "revision": 7,
  "updated_at": "2026-10-12T10:00:00Z",
  "updated_by": "local:OFFICE\\admin",
  "name": "Documents",
  "enabled": true,
  "comment": "",
  "priority": 0,                       // больше = раньше в очереди
  "backup_type": "incremental",        // full | incremental | differential | mirror
  "mirror_mode": null,                 // exact | safe, только для mirror
  "force_full": { "every_runs": 0, "weekday": null, "month_day": null },  // weekday 1–7 (ISO, 1 = пн), month_day 1–31 или -1 = последний день
  "sources": [
    { "id": "uuid", "kind": "folder", "path": "D:\\Data", "credential_id": null, "recursive": true }
  ],                                   // kind: folder | file | unc
  "filters": {
    "include": [], "exclude": ["*.tmp", "~$*"], "exclude_dirs": ["Temp"],
    "skip_hidden": false, "skip_system": true, "skip_temp": true
  },
  "profile_sid": null,                 // чей профиль для %USERPROFILE% и т.п.; null = кто создал задание
  "destinations": [
    {
      "id": "uuid", "kind": "unc",     // local | unc | usb | ftp | ftps | sftp | s3 | webdav
      "path": "\\\\fileserver\\docs",
      "usb_label": null,               // для usb: метка тома; path тогда относительный
      "host": null, "port": null,      // ftp/ftps/sftp/webdav
      "endpoint": null, "bucket": null, "region": null,   // s3
      "credential_id": "uuid",
      "disconnect_after": true,
      "speed_limit_kbps": 0            // 0 = без ограничения
    }
  ],
  "layout": {
    "separate_folders": true,          // «Отдельная папка для каждой копии»
    "name_template": "{task} {yyyy}-{MM}-{dd} {HH}-{mm} ({type})",
    "keep_top_dir": true,
    "delete_empty_dirs": false
  },
  "archive": {
    "format": "none",                  // none | zip | 7z (7z только если все назначения local/unc/usb/sftp)
    "level": 5,                        // 0 без сжатия … 9 ультра
    "split_mb": 0,                     // 0 = не разбивать; только для 7z
    "encrypt": false,                  // AES-256
    "encrypt_names": false,            // только 7z
    "password_credential_id": null
  },
  "verify": {
    "every_runs": 0,                   // проверка новой копии: 0 = нет, 1 = каждый раз, N = каждый N-й запуск
    "stored_every_days": 0             // плановая проверка ВСЕХ хранящихся наборов раз в N дней; 0 = выкл
  },
  "retention": {
    "mode": "count",                   // none | count | age | gfs
    "keep_full": 12,                   // для count
    "max_age_days": 0,                 // для age
    "gfs": { "daily": 7, "weekly": 4, "monthly": 12 },
    "safe_mirror_days": 30             // хранение удалённых файлов безопасного зеркала
  },
  "schedule": {
    "type": "daily",                   // daily | weekly | monthly | interval | manual
    "times": ["00:00"],                // локальное время агента, ЧЧ:ММ
    "days_of_week": [1, 2, 3, 4, 5, 6, 7],  // ISO, 1 = пн; для daily фильтр, для weekly обязателен
    "days_of_month": [],               // для monthly; -1 = последний день месяца
    "interval_minutes": 0,             // для interval
    "catch_up": true                   // выполнить пропущенный запуск при включении
  },
  "vss": { "enabled": true, "on_failure": "abort" },   // abort | continue
  "copy": { "preserve_acl": true, "read_retries": 1 },
  "events": {
    "pre":  [ { "kind": "run", "command": "C:\\scripts\\dump.cmd", "args": "", "timeout_s": 3600, "service": null } ],
    "post": [],                        // kind: run | service_stop | service_start
    "abort_on_pre_failure": true,
    "after": "none"                    // none | shutdown | restart | sleep | hibernate
  },
  "log_level": "inherit"               // inherit | summary | files
}
```

**Учётные данные** хранятся отдельно, задания ссылаются на них по `credential_id`. Одну учётку можно использовать в нескольких заданиях.

```json
{ "id": "uuid", "kind": "windows", "user": "backup1", "domain": ".", "has_secret": true }
```

`kind`: `windows` (шары), `ftp`, `sftp` (пароль или закрытый ключ), `s3` (`user` = access key, секрет = secret key), `webdav`, `archive_password`. Секрет хранится только на агенте под DPAPI и никогда не уходит на сервер.

**Настройки агента** (`settings`):

```jsonc
{
  "language": "auto",                  // auto | ru | en
  "theme": "auto",                     // auto | light | dark
  "tray_mode": "full",                 // full | status | hidden
  "allow_all_users_edit": false,
  "program_password_hash": null,       // формат A.2; null = пароль не задан
  "log_retention_days": 180,
  "default_log_level": "summary",      // summary | files
  "process_priority": "low",           // low | normal
  "allow_remote_edit": true,           // ЛОКАЛЬНАЯ: сервер изменить не может
  "server_url": "http://custos.lan",   // ЛОКАЛЬНАЯ
  "heartbeat_interval_s": 60           // задаёт сервер в ответе heartbeat
}
```

### A.10. Обновления агента

Команда `update.install` несёт `version`, `sha256` пакета и `signature`: подпись сервера (A.2) строки `"{version}\n{sha256}"`. Агент скачивает пакет `GET /api/v1/agent/updates/{version}` (запрос подписан как обычно), проверяет SHA-256 и подпись, затем устанавливает. Новую версию сервер видит по следующему heartbeat. Пакет — zip с файлами программы и `manifest.json` (`version`, `min_protocol`, список файлов).

### A.11. Ошибки

Тело ошибки: `{"error": "код", "message": "текст"}`.

| HTTP | `error` | Действие агента |
|---|---|---|
| 400 | `bad_request` | Записать в журнал, не повторять |
| 401 | `bad_signature`, `clock_skew` | При `clock_skew` в теле есть `server_time`: записать «расходится время» |
| 403 | `pending`, `disabled`, `bad_registration_key` | Ждать, отчёты держать в очереди |
| 404 | `unknown_agent` | Зарегистрироваться заново (A.3) |
| 413 | `too_large` | Отправить без `log_gz_b64` |
| 429 | `rate_limited` | Ждать столько, сколько указано в `Retry-After` |
| 5xx / нет связи | — | Повторять через 1, 2, 5, 10, 10… минут |

### A.12. Статусы (общий словарь)

| Код | Где | Русское название | Смысл |
|---|---|---|---|
| `success` | запуск | Успешно | Всё скопировано |
| `warning` | запуск | С предупреждениями | Выполнено, часть файлов пропущена |
| `error` | запуск | Ошибка | Копия не создана или создана не полностью |
| `aborted` | запуск | Прервано | Остановлено вручную или командой |
| `running` | задание | Выполняется | Есть активный запуск |
| `missed` | задание | Пропущено | Слот расписания прошёл, машина была на связи, а запуска не было. **Это проблема** |
| `missed_offline` | задание | Пропущено, машина выключена | Слот прошёл, когда машина была не на связи. Не проблема, ждём догоняющий запуск |
| `stale` | задание | Давно нет успешной копии | Последняя успешная копия старше N дней (по умолчанию 7). Мягкая пометка |
| `online` / `offline` | агент | На связи / Не на связи | `offline`: нет heartbeat дольше N минут (по умолчанию 60). Само по себе не проблема |
| `pending` | агент | Ожидает подтверждения | Зарегистрирован, но не подтверждён |
| `disabled` | агент или задание | Отключено | — |
