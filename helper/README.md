# custos-helper

`custos-helper` — небольшой скрипт на bash для Linux-сервера. Через него агент CUSTOS в офисе забирает с сервера по SSH дампы баз и файлы. Скрипт только читает: что именно можно забрать, решает конфиг `/etc/custos/helper.conf` на сервере, а агент расширить это не может. Устройство и формат ответов описаны в разделе 3 [`docs/CUSTOS-remote-pull.md`](../docs/CUSTOS-remote-pull.md).

Забирает копии клиент CUSTOS: в задании «Источники» → «Linux-сервер…» (раздел «Linux-серверы» в [`client/README.ru.md`](../client/README.ru.md)). Окно сервера само создаёт ключ и даёт блок команд, который ставит custos-helper на сервер; ручная установка ниже нужна, если так удобнее. Вызвать custos-helper можно и вручную, командой `ssh` из командной строки Windows: так удобно проверить сервер до настройки задания.

> **Только чтение.** custos-helper ничего не меняет на сервере: он только выгружает дампы и файлы, которые разрешает его конфиг. Как и любой новый инструмент, сначала его разумно попробовать на тестовом VPS или виртуальной машине.

Ниже в примерах `vps.example.org` — адрес сервера, `admin` — ваш обычный пользователь для входа по SSH, `203.0.113.10` — внешний IP офиса, `site_db` — имя базы. Подставьте свои. Адреса `203.0.113.x` и `198.51.100.x` ничьи: они зарезервированы для примеров в документации.

## Что нужно на сервере

- Linux с bash 4.4 или новее, GNU find, GNU tar, gzip, sudo и OpenSSH. Подходят Debian 11 и новее, Ubuntu 20.04 и новее, AlmaLinux и Rocky Linux 8 и новее. Alpine (BusyBox) не подходит.
- СУБД, если нужны дампы: MariaDB 10.3 и новее, MySQL 8.0.17 и новее, PostgreSQL. На сервере может быть одна из MariaDB и MySQL и вместе с ней PostgreSQL.
- root входит в MariaDB или MySQL без пароля (`mariadb -e 'select 1'` работает после `sudo -i`). Если нет, см. `db_defaults_file` ниже. В PostgreSQL custos-helper входит от системного пользователя `postgres`, пароль не нужен.
- Если в `/etc/ssh/sshd_config` есть строки `AllowUsers` или `AllowGroups`, добавьте туда пользователя `custos`. Больше `sshd_config` не меняется.

Проверенные системы перечислены в разделе [Автотесты](#автотесты).

## Установка

### 1. Ключ на компьютере с Windows

Для проверки нужен отдельный ключ. Агент CUSTOS потом создаст себе свой. В командной строке Windows (`cmd`, не PowerShell). Если окно открылось в PowerShell (строка начинается с `PS`), введите `cmd` и нажмите Enter: в PowerShell эти команды не работают.

```bat
mkdir "%USERPROFILE%\.ssh"
ssh-keygen -t ed25519 -f "%USERPROFILE%\.ssh\custos_test" -C custos-test -N ""
type "%USERPROFILE%\.ssh\custos_test.pub"
```

Если папка `.ssh` уже есть, `mkdir` скажет об этом, это не ошибка. Последняя команда выводит открытый ключ, одну строку вида `ssh-ed25519 AAAAC3Nz... custos-test`. Она понадобится в шаге 4.

### 2. Скачать custos-helper

Готовый скрипт собирает GitHub Actions. В репозитории откройте **Actions**, выберите последний успешный запуск **custos-helper** для нужной ветки и внизу в разделе **Artifacts** скачайте `custos-helper`. Распакуйте zip в папку `C:\custos-helper`. В ней два файла: сам скрипт `custos-helper` и его контрольная сумма `custos-helper.sha256`.

Не открывайте скрипт в Блокноте и не сохраняйте его заново: Windows может заменить переводы строк, и скрипт перестанет работать. Контрольная сумма в шаге 3 это поймает.

### 3. Скопировать скрипт на сервер

В `cmd`:

```bat
cd /d C:\custos-helper
type custos-helper.sha256
scp custos-helper admin@vps.example.org:/tmp/custos-helper
```

Если SSH на сервере не на порту 22, добавьте `-P <порт>` сразу после `scp`.

Войдите на сервер и станьте root:

```bat
ssh admin@vps.example.org
```

```bash
sudo -i
sha256sum /tmp/custos-helper
```

Число из `sha256sum` должно совпасть с числом из `custos-helper.sha256`. Если не совпадает, скачайте артефакт заново.

### 4. Установить

Скопируйте блок ниже целиком и вставьте в сессию root на сервере (после `sudo -i`). Править в нём ничего не нужно: блок сам спросит две вещи.

- **Ключ** — открытый ключ из шага 1, строка целиком.
- **IP офиса** — внешний адрес, с которого будет подключаться CUSTOS. Ключ будет работать только с него: это проверяет sshd ещё до входа. Несколько адресов или сетей пишутся через запятую, например `203.0.113.10,198.51.100.0/24`. Если IP динамический, ответьте `any`: ключ будет работать с любого адреса, и блок об этом предупредит. Сменить адрес потом можно в любой момент, см. [Сменить адрес офиса](#сменить-адрес-офиса).

Пользователя `custos` заранее создавать не нужно: блок создаёт его сам, без пароля (вход только по ключу) и без дополнительных групп. В группу `sudo` или `wheel` его не добавляйте: sudo ему нужен только на custos-helper, и это настраивает сам блок.

Блок выполняется в отдельном `bash -e`: при ошибке он остановится, а ваша сессия останется открытой. Повторный запуск безопасен: пользователь и уже существующий конфиг не трогаются, а строка этого ключа заменяется новой, а не добавляется второй.

Чтобы ставить без вопросов (например, своим сценарием), задайте ответы заранее: `export CUSTOS_KEY='ssh-ed25519 AAAA... custos-test' CUSTOS_FROM='203.0.113.10'`, для любого адреса `CUSTOS_FROM=any`.

<!-- install-block: the tests run this block as it is, answering through CUSTOS_KEY and CUSTOS_FROM or a terminal -->
```bash
bash -e <<'CUSTOS_INSTALL_EOF'
# 0. Ключ и IP офиса: из CUSTOS_KEY и CUSTOS_FROM, если они заданы, иначе блок спрашивает.
ask() { # ask <вопрос>: ответ с клавиатуры в REPLY
  { : < /dev/tty; } 2>/dev/null || { echo 'Нет терминала: задайте CUSTOS_KEY и CUSTOS_FROM' >&2; exit 1; }
  read -r -p "$1" REPLY < /dev/tty || exit 1
}
KEY_RE='^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp(256|384|521)|sk-(ssh-ed25519|ecdsa-sha2-nistp256)@openssh\.com) [A-Za-z0-9+/]+=*( .*)?$'
FROM_RE='^[0-9A-Za-z.:/*?!-]+(,[0-9A-Za-z.:/*?!-]+)*$'

if [ -n "${CUSTOS_KEY+x}" ]; then KEY=$CUSTOS_KEY
else
  echo 'Открытый ключ из шага 1: одна строка вида ssh-ed25519 AAAA... custos-test'
  ask 'Ключ: '; KEY=$REPLY
  until [[ $KEY =~ $KEY_RE ]]; do ask 'Это не открытый ключ SSH. Вставьте строку целиком: '; KEY=$REPLY; done
fi
[[ $KEY =~ $KEY_RE ]] || { echo 'CUSTOS_KEY: это не открытый ключ SSH' >&2; exit 1; }

if [ -n "${CUSTOS_FROM+x}" ]; then FROM=${CUSTOS_FROM:-any}
else
  echo 'С какого внешнего IP будет подключаться CUSTOS? Ключ будет работать только с него.'
  echo 'Несколько адресов или сетей пишутся через запятую. Если IP динамический, ответьте any.'
  ask 'IP офиса: '; FROM=${REPLY//[[:space:]]/}
  until [[ $FROM =~ $FROM_RE ]]; do ask 'Нужен IP-адрес, сеть (203.0.113.0/24) или any: '; FROM=${REPLY//[[:space:]]/}; done
fi
FROM=${FROM//[[:space:]]/}
[[ $FROM =~ $FROM_RE ]] || { echo "CUSTOS_FROM: это не IP-адрес и не сеть: $FROM" >&2; exit 1; }
if [ "${FROM,,}" = any ]; then
  FROM=''
  echo 'ВНИМАНИЕ: ключ будет работать с любого адреса. Он по-прежнему даёт только custos-helper' >&2
  echo 'и только чтение, но если закрытый ключ украдут, им можно будет пользоваться откуда угодно.' >&2
fi

# 1. Пользователь custos. Пароль '*' значит «входа по паролю нет», вход только по ключу.
id custos >/dev/null 2>&1 || useradd --system --create-home --shell /bin/bash --password '*' custos

# 2. Скрипт, заранее скопированный в /tmp
install -m 0755 /tmp/custos-helper /usr/local/sbin/custos-helper

# 3. Конфиг (пресет «ispmanager: только сайты»). Создаётся, только если его ещё нет.
install -d -m 0755 /etc/custos
if [ ! -e /etc/custos/helper.conf ]; then
cat > /etc/custos/helper.conf <<'CUSTOS_CONF_EOF'
root       = /var/www
root       = /etc/nginx
root       = /var/spool/cron/crontabs
exclude    = /var/www/*/data/logs
exclude    = /var/www/*/data/tmp
exclude    = /var/www/*/data/mod-tmp
exclude    = /var/www/httpd-logs
db_engine  = mariadb
CUSTOS_CONF_EOF
fi
chmod 0600 /etc/custos/helper.conf

# 4. sudo только на custos-helper. Черновик с точкой в имени sudo не читает; испорченный
#    файл visudo не пропустит на место, и sudo не сломается.
cat > /etc/sudoers.d/custos.new <<'CUSTOS_SUDO_EOF'
Defaults:custos env_keep += "SSH_ORIGINAL_COMMAND"
custos ALL=(root) NOPASSWD: /usr/local/sbin/custos-helper
CUSTOS_SUDO_EOF
chmod 0440 /etc/sudoers.d/custos.new
visudo -cf /etc/sudoers.d/custos.new
mv /etc/sudoers.d/custos.new /etc/sudoers.d/custos

# 5. Ключ: только custos-helper, без терминала и пробросов, и только с IP офиса, если он задан.
#    Прежняя строка этого же ключа (например, со старым адресом) заменяется новой.
LINE='command="sudo -n /usr/local/sbin/custos-helper",restrict'
[ -z "$FROM" ] || LINE="$LINE,from=\"$FROM\""
LINE="$LINE $KEY"
BLOB=$(echo "$KEY" | cut -d ' ' -f 2)
AK=~custos/.ssh/authorized_keys
install -d -m 0700 ~custos/.ssh
touch "$AK"
{ grep -vF -- " $BLOB" "$AK" || [ $? -eq 1 ]; echo "$LINE"; } > "$AK.new"
chmod 0600 "$AK.new"
mv "$AK.new" "$AK"
chown -R custos: ~custos/.ssh

/usr/local/sbin/custos-helper info
if [ -n "$FROM" ]; then echo "OK: custos-helper установлен, ключ работает только с $FROM"
else echo 'OK: custos-helper установлен, ключ работает с любого адреса'; fi
CUSTOS_INSTALL_EOF
```

В конце блок выводит ответ `info` (одна строка JSON) и `OK: custos-helper установлен` с адресом, с которого теперь работает ключ.

### 5. Настроить конфиг

Конфиг лежит в `/etc/custos/helper.conf`. Откройте его от root: `nano /etc/custos/helper.conf`. Одна настройка на строку, `ключ = значение`; строка, которая начинается с `#`, — комментарий.

| Ключ | Что значит |
|---|---|
| `root` | папка, которую забирать вместе со всем содержимым. Можно повторять. Только абсолютный путь, `/` нельзя |
| `exclude` | что пропускать, шаблон `find -path`: `*` подходит под любую часть пути. Пропущенная папка пропускается целиком. Можно повторять |
| `db_engine` | `mariadb`, `mysql` или `postgresql`. Можно указать две СУБД: `mariadb` или `mysql` и `postgresql`. Нет строки — дампов нет |
| `db_exclude` | база, которую не забирать. Можно повторять. Системные базы custos-helper пропускает сам |
| `db_defaults_file` | только для MariaDB и MySQL: файл вида `my.cnf` с учётной записью, если root не входит в СУБД без пароля |

Пресет в блоке — «ispmanager: только сайты», для переноса сайтов с ispmanager на любой другой сервер или панель. Он забирает файлы сайтов (в `/var/www` ispmanager держит и SSL-сертификаты, папка `httpd-cert`), а для справки — настройки nginx (редиректы и правила сайтов) и задания cron. Логи веб-сервера (`/var/www/httpd-logs`) и временные папки сайтов исключены: для переноса они не нужны, а живой лог, который nginx дописывает во время чтения, давал бы предупреждение при каждом заборе. Если сайты работают ещё и через Apache, добавьте `root = /etc/apache2`. Если на сервере есть PostgreSQL, добавьте строку `db_engine = postgresql`.

Папок, которых на сервере нет, в списке быть не должно: custos-helper пропустит их, но каждый забор будет заканчиваться предупреждением (код 1). Корни не должны вкладываться друг в друга (например, `/etc` и `/etc/nginx`), иначе файлы попадут в архив дважды.

Другие пресеты:

- **«ispmanager: сайты и панель»**, чтобы поднять тот же сервер с ispmanager целиком: `root = /var/www`, `/etc`, `/usr/local/mgr5/etc`, `/var/spool/cron/crontabs`, исключения и базы те же.
- **«Обычный Linux»**, сервер без панели: `root = /etc`, `/home`, `/root`, `/var/www`, `/var/spool/cron`.

Весь `/etc` без нужды не забирайте: там хеши паролей (`/etc/shadow`) и закрытые ключи SSH самого сервера, и всё это окажется в копиях.

Если нужен `db_defaults_file`, лучше завести в СУБД отдельного пользователя только на чтение:

```sql
CREATE USER 'custos_ro'@'localhost' IDENTIFIED BY 'пароль';
GRANT SELECT, SHOW VIEW, TRIGGER, EVENT, LOCK TABLES, PROCESS ON *.* TO 'custos_ro'@'localhost';
```

и положить его в файл `/etc/custos/my.cnf` с правами 0600:

```ini
[client]
user = custos_ro
password = пароль
```

После правки проверьте конфиг от root:

```bash
custos-helper info
custos-helper db-list mariadb
```

Если в конфиге ошибка, custos-helper напишет строку и причину и завершится с кодом 78.

## Проверка с компьютера с Windows

Все команды — в `cmd`, не в PowerShell: Windows PowerShell 5 портит двоичные данные при выводе в файл. Заведите папку для результатов и две переменные, чтобы не повторять ключ и адрес:

```bat
mkdir C:\custos-test
cd /d C:\custos-test
set K=%USERPROFILE%\.ssh\custos_test
set S=custos@vps.example.org
```

После каждой команды код завершения можно посмотреть так: `echo %errorlevel%`.

| Команда | Что должно получиться |
|---|---|
| `ssh -i %K% %S% info` | JSON с версией custos-helper, временем сервера `now`, списком СУБД `db_engines` и корнями. При первом входе ssh спросит про ключ сервера, ответьте `yes` |
| `ssh -i %K% %S% db-list mariadb` | имена ваших баз по одной на строку, без `mysql`, `sys` и других системных |
| `ssh -i %K% %S% db-dump mariadb site_db > site_db.sql` | файл с дампом. `findstr /c:"-- Dump completed" site_db.sql` находит строку в конце дампа |
| `ssh -i %K% %S% db-users mariadb > users.sql` | пользователи и права СУБД: строки `CREATE USER IF NOT EXISTS` и `GRANT` |
| `ssh -i %K% %S% manifest > manifest.gz` | непустой файл, список всех файлов в сжатом виде |
| `ssh -i %K% %S% tar-since 0 > full.tar` | полная копия файлов. `tar -tvf full.tar \| more` показывает файлы, имена начинаются с `var/www/`, `etc/nginx/` и т. д. Папок `httpd-logs`, `data/logs`, `data/tmp`, `data/mod-tmp` в нём нет |
| `ssh -i %K% %S% tar-since 0 gzip > full.tar.gz` | то же со сжатием на сервере: файл меньше, и по медленному каналу приходит быстрее. `tar -tvf full.tar.gz` читает его так же |

Для PostgreSQL то же самое с `postgresql` вместо `mariadb`. Дамп PostgreSQL заканчивается строкой `-- PostgreSQL database dump complete`, после неё идёт служебная строка `\unrestrict`.

**Изменённые файлы.** Запомните число `now` из ответа `info`, например `1794328800`. На сервере измените или создайте файл, например `echo test > /var/www/custos-test.txt`. Затем:

```bat
ssh -i %K% %S% tar-since 1794328800 > changed.tar
tar -tvf changed.tar | findstr /v /b "d"
```

Вторая команда скрывает папки (они входят в каждую копию) и показывает только файлы: `var/www/custos-test.txt` и то, что ещё успело измениться на сервере за это время.

**Ограничение скорости.** `ssh -i %K% %S% tar-since 0 1000 > slow.tar` делает то же, что полная копия, но не быстрее 1000 КБ/с, поэтому идёт заметно дольше. Время видно, если выполнить `echo %time%` до и после.

**Сжатие.** К `db-dump`, `db-users` и `tar-since` можно добавить в конце слово `gzip`, например `tar-since 0 1000 gzip` или `db-dump mariadb site_db gzip > site_db.sql.gz`. Агент CUSTOS всегда просит сжатый поток. С `gzip` ограничение скорости считается по данным до сжатия, поэтому по сети идёт ещё медленнее заданного.

**Отказы.** Эти команды должны быть отклонены с кодом 64, а на сервер ничего не попадает:

| Команда | Ответ |
|---|---|
| `ssh -i %K% %S% "cat /etc/shadow"` | `custos-helper: invalid characters in the command` |
| `ssh -i %K% %S% db-dump mariadb mysql` | `custos-helper: database is not in db-list: mysql` |
| `ssh -i %K% %S% db-dump postgresql site_db` (если PostgreSQL нет в конфиге) | `custos-helper: database engine is not configured: postgresql` |
| `ssh -i %K% %S%` | `PTY allocation request failed` и `unknown operation` |

`sftp -i %K% %S%` тоже не работает: соединение сразу закрывается.

**Проверка дампа (по желанию).** Скопируйте дамп обратно на тестовый сервер и загрузите в новую базу:

```bat
scp site_db.sql admin@vps.example.org:/tmp/site_db.sql
```

```bash
mariadb -e 'CREATE DATABASE site_db_check'
mariadb site_db_check < /tmp/site_db.sql
mariadb -e 'SHOW TABLES' site_db_check
mariadb -e 'DROP DATABASE site_db_check'
rm /tmp/site_db.sql
```

### Что проверить на сервере

От root (после `sudo -i`):

| Команда | Что должно получиться |
|---|---|
| `journalctl -t custos-helper -n 20` или `grep custos-helper /var/log/syslog \| tail -n 20` | строка на каждый вызов: `cmd=info exit=0`, `cmd=cat\ /etc/shadow exit=64` и т. д. |
| `sudo -u custos sudo -n -l` | пользователю `custos` разрешён только `/usr/local/sbin/custos-helper` |
| `sudo -u custos sudo -n cat /etc/shadow` | отказ: `a password is required` или `I'm sorry custos. I'm afraid I can't do that` (зависит от версии sudo) |
| `ls -l /etc/custos/helper.conf` | `-rw-------` и владелец `root` |

## Коды завершения

| Код | Что значит |
|---|---|
| 0 | успех |
| 1 | у `manifest` и `tar-since` — предупреждение: файл изменился или исчез во время чтения, корня нет на диске и т. п. Данные пригодны, причина в сообщении. У дампов — ошибка СУБД |
| 2 и больше | ошибка tar или СУБД; сообщение в stderr |
| 64 | неверный вызов: неизвестная операция, лишние аргументы, недопустимые символы, база не из `db-list` |
| 78 | ошибка в конфиге, сообщение называет строку |
| 141 | только в журнале сервера: получатель закрыл соединение раньше конца (Ctrl+C, обрыв связи), custos-helper остановился |
| 255 | ошибка самого ssh на стороне Windows: нет связи, ключ не подошёл |

## Сменить адрес офиса

Адрес хранится не в скрипте и не в `helper.conf`, а в строке ключа в файле `/home/custos/.ssh/authorized_keys`, параметр `from="..."`. Его проверяет sshd ещё до входа: с другого адреса ключ не пускают вовсе, и custos-helper даже не запускается. У каждого ключа своя строка (например, ваш тестовый ключ из шага 1 и ключ агента из окна программы); команды ниже меняют адрес во всех строках custos-helper сразу.

От root (после `sudo -i`) посмотрите адреса сейчас и сделайте копию файла:

```bash
grep -o 'from="[^"]*"' ~custos/.ssh/authorized_keys
cp -p ~custos/.ssh/authorized_keys /root/authorized_keys.custos.bak
```

Дальше одно из двух. Новый адрес (вместо `203.0.113.20` свой; несколько адресов или сетей через запятую без пробелов):

```bash
sed -i -e 's/,from="[^"]*"//' -e 's|^command="sudo -n /usr/local/sbin/custos-helper",restrict|&,from="203.0.113.20"|' ~custos/.ssh/authorized_keys
```

Или вход с любого адреса, если IP стал динамическим:

```bash
sed -i 's/,from="[^"]*"//' ~custos/.ssh/authorized_keys
```

Проверьте результат: `grep -o 'from="[^"]*"' ~custos/.ssh/authorized_keys` показывает новый адрес (или ничего, если вход с любого адреса), а `ls -l ~custos/.ssh/authorized_keys` — `-rw-------` и владельца `custos`. Перезапускать sshd не нужно: он читает этот файл при каждом входе. Если что-то пошло не так, верните копию: `cp -p /root/authorized_keys.custos.bak ~custos/.ssh/authorized_keys`.

Если custos-helper ставили из окна программы, впишите тот же адрес в поле «IP офиса» этого сервера в задании и сохраните задание. Иначе блок из окна при следующем запуске вернёт прежний адрес. Можно и наоборот: поменять «IP офиса» в окне и выполнить его блок ещё раз, строка ключа агента заменится.

Если SSH с нового адреса вообще не соединяется (долгое ожидание, а не `Permission denied`), вход ограничен ещё и брандмауэром сервера или панели. Его custos-helper не трогает: адрес нужно добавить и там.

## Обновление

Скачайте новый артефакт (шаг 2), скопируйте скрипт на сервер (шаг 3), сверьте `sha256sum` и замените только скрипт. Конфиг, пользователь и ключ остаются.

```bash
install -m 0755 /tmp/custos-helper /usr/local/sbin/custos-helper
custos-helper info
```

## Удаление

От root:

```bash
rm -f /etc/sudoers.d/custos /usr/local/sbin/custos-helper
rm -rf /etc/custos
userdel -r custos
```

`userdel -r` удаляет и домашнюю папку `custos` вместе с ключом. На Windows удалите `%USERPROFILE%\.ssh\custos_test` и `custos_test.pub`.

## Автотесты

Тесты в `tests/` поднимают в Docker тестовый сервер из пакетов самого дистрибутива: OpenSSH, sudo, rsyslog и СУБД. Затем ставят custos-helper блоком из этого README (с ответами через `CUSTOS_KEY` и `CUSTOS_FROM` и через терминал) и проверяют его по SSH: все операции, отказы, коды завершения, имена файлов с пробелами, табуляциями, переводами строк и не в UTF-8, ссылки, исключения, выбор файлов по ctime, ограничение скорости, восстановление дампов, `from=` и его смену командами из раздела «Сменить адрес офиса», запрет терминала, пробросов и SFTP, запись в syslog.

| Система | СУБД |
|---|---|
| Debian 12 | MariaDB 10.11, PostgreSQL 15 |
| Debian 13 | MariaDB 11.8, PostgreSQL 17 |
| Ubuntu 22.04 | MariaDB 10.6 |
| Ubuntu 24.04 | MySQL 8.0, PostgreSQL 16 |
| Ubuntu 26.04 | MySQL 8.4 |
| Ubuntu 26.04 | MariaDB 11.8, PostgreSQL 18 |

AlmaLinux 9 (MariaDB 10.5 и PostgreSQL 13, отдельно MySQL 8.0) и Ubuntu 18.04 (без СУБД, только файлы; там самый старый поддерживаемый bash, 4.4) прошли те же тесты 10.11.2026, но в постоянный прогон не входят, чтобы не тратить минуты GitHub Actions. Их можно запустить вручную, как любую другую систему.

Запуск одной системы на машине с Docker, из корня репозитория:

```bash
helper/tests/run.sh debian:12 "mariadb postgresql"
helper/tests/run.sh almalinux:9 mysql
```

В GitHub Actions (`.github/workflows/helper.yml`) тесты идут на всех системах из таблицы, когда меняется что-то в `helper/`. Рядом с ними скрипт проверяется ShellCheck'ом, а когда всё прошло, workflow выкладывает артефакт `custos-helper` со скриптом и его SHA-256.
