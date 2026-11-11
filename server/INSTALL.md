# Установка CUSTOS Monitor на CloudPanel, пошагово

Делайте шаги по порядку. После каждого шага написано, что должно получиться. Если получилось не так, смотрите раздел «Если что-то пошло не так» в конце.

В примерах используются такие имена. Если выбрали другие, подставляйте свои везде:

| Что | В примерах |
|---|---|
| Адрес сайта | `custos.lan` |
| Версия PHP сайта | 8.4, поэтому в командах `php8.4`. Если в CloudPanel выбрана 8.5, пишите `php8.5` |
| Пользователь сайта в CloudPanel | `custos` |
| База данных | `custos` |
| Пользователь базы данных | `custos` |

Два правила:

- Все команды в SSH выполняются **под пользователем сайта** (`custos`), не под root.
- PHP-скрипты запускаются только так: `php8.4 bin/install.php`. Если не написать `php8.4` в начале, будет ошибка `Permission denied` или `?php: No such file or directory`. `chmod` делать не нужно.

---

## Часть 1. Скачать пакет (на своём компьютере)

**Шаг 1.** Откройте репозиторий на GitHub. Перейдите в **Actions**, слева выберите **Server**. Откройте верхний запуск с зелёной галочкой. Внизу страницы, в блоке **Artifacts**, нажмите `custos-server`.

✅ Скачался файл `custos-server.zip`. Распаковывать его на компьютере не нужно.

> Пакет хранится 14 дней. Если в блоке Artifacts пусто, на странице **Actions → Server** нажмите **Run workflow**, подождите пару минут и откройте новый запуск.

---

## Часть 2. CloudPanel (в браузере)

**Шаг 2. Сайт.** **Sites → + Add Site → Create a PHP Site**. Заполните:

- Application: **Generic**
- Domain Name: `custos.lan`
- PHP Version: **8.4** или новее
- Site User: `custos`, Site User Password: придумайте и запишите (понадобится для SSH)

Нажмите **Create**.

✅ Сайт появился в списке сайтов.

**Шаг 3. Корневая папка.** Откройте сайт, вкладка **Settings**. В поле **Root Directory** вместо `custos.lan` впишите `custos.lan/server/public`. Нажмите **Save**.

**Шаг 4. База данных.** В сайте вкладка **Databases → Add Database**:

- Database Name: `custos`
- Database User Name: `custos`
- Database User Password: придумайте и запишите (понадобится в шаге 10)

✅ База появилась в списке.

**Шаг 5. Только если сайт будет работать по HTTP (без сертификата).** В сайте вкладка **Vhost**. Найдите такой блок и удалите его целиком, затем **Save**:

```nginx
if ($scheme != "https") {
  rewrite ^ https://$host$request_uri permanent;
}
```

Если такого блока нет, ничего не делайте. С этим блоком агенты по HTTP работать не смогут.

---

## Часть 3. Загрузить пакет на сервер

**Шаг 6.** В сайте вкладка **File Manager**. Откройте папку `custos.lan` и загрузите туда `custos-server.zip` (кнопка **Upload**).

Можно и через WinSCP: протокол SFTP, логин `custos` и пароль из шага 2, папка `/home/custos/htdocs/custos.lan/`.

✅ В папке `custos.lan` лежит `custos-server.zip`.

---

## Часть 4. Установка по SSH

**Шаг 7. Войти на сервер под пользователем сайта.** На своём компьютере (PowerShell или PuTTY):

```bash
ssh custos@адрес-сервера
```

Пароль: из шага 2. Затем проверьте, кто вы:

```bash
whoami
```

✅ Выводится `custos`. Если выводится `root`, выйдите (`exit`) и войдите как `custos`.

**Шаг 8. Распаковать пакет.**

```bash
cd ~/htdocs/custos.lan
unzip -o custos-server.zip
rm custos-server.zip
cd server
ls
```

✅ `ls` показывает: `INSTALL.md README.md bin composer.json composer.lock config lang migrations public src templates tools vendor`.

С этого места все команды выполняются из папки `~/htdocs/custos.lan/server`.

**Шаг 9. Создать файл настроек.**

```bash
cp config/config.example.php config/config.php
```

Команда ничего не выводит, это нормально.

**Шаг 10. Заполнить настройки.**

```bash
nano config/config.php
```

Поменяйте значения в этих строках (кавычки оставьте):

```php
        'name' => 'custos',          // имя базы из шага 4
        'user' => 'custos',          // пользователь базы из шага 4
        'password' => 'change-me',   // ← пароль базы из шага 4
...
    'url' => 'http://custos.lan',    // ← адрес сайта, как его открывают в браузере, с http:// или https://
...
    'timezone' => 'UTC',             // ← ваш часовой пояс, например 'Europe/Berlin'
```

Сохранить: **Ctrl+O**, затем **Enter**. Выйти: **Ctrl+X**.

**Шаг 11. Запустить установку.**

```bash
php8.4 bin/install.php
```

Скрипт задаст три вопроса:

1. `Login of the first administrator:` — логин для входа на сайт, например `admin`.
2. `Password (at least 8 characters):` — пароль, не короче 8 символов. При вводе он не отображается, это нормально.
3. `Repeat the password:` — тот же пароль ещё раз.

✅ Должно получиться так (код и отпечаток у вас будут свои):

```
[ok] PHP 8.4.x with all required extensions
[ok] Database 10.11.x-MariaDB
[ok] Applied migration 001_init.sql
[ok] Folder /home/custos/htdocs/custos.lan/server/var is ready
[ok] Created the server key pair in /home/custos/htdocs/custos.lan/server/config/keys
Login of the first administrator: admin
Password (at least 8 characters):
Repeat the password:
[ok] Created administrator "admin"
[ok] Generated the agent registration key

Server key fingerprint:  XXXX-XXXX-XXXX-XXXX
Registration code:       XXXXX-XXXXX-XXXXX-XXXXX-XXXXX#XXXX-XXXX-XXXX-XXXX
The code is also shown on the site under Settings.

Add a cron job that runs every minute:
  * * * * * /usr/bin/php8.4 /home/custos/htdocs/custos.lan/server/bin/cron.php
```

Все строки начинаются с `[ok]`. **Registration code** — код, который вводят в агенте. Записывать его не обязательно: он всегда есть на сайте в разделе «Настройки».

Если какая-то строка начинается с `[error]`, исправьте то, что в ней написано, и запустите `php8.4 bin/install.php` ещё раз. Повторный запуск ничего не ломает.

---

## Часть 5. Cron (в CloudPanel)

**Шаг 12.** В сайте вкладка **Cron Jobs → Add Cron Job**:

- Name: `custos`
- Расписание: каждую минуту (все поля `*`)
- Command:
  ```
  /usr/bin/php8.4 /home/custos/htdocs/custos.lan/server/bin/cron.php
  ```

Нажмите **Add Cron Job**.

---

## Часть 6. Проверить сайт

**Шаг 13.** В браузере откройте `http://custos.lan`.

✅ Открывается страница входа CUSTOS. Войдите с логином и паролем из шага 11. Видна страница «Машины» с надписью «Машин пока нет».

---

## Часть 7. Проверить работу с агентом (имитатор)

Имитатор ведёт себя как настоящий агент: регистрируется, присылает задания и запуски. Команды выполняются по SSH, как в части 4, из папки `~/htdocs/custos.lan/server`.

**Шаг 14.** Возьмите код на сайте: «Настройки», поле «Код регистрации», кнопка «Скопировать». Вставьте его в команду между одинарными кавычками:

```bash
php8.4 tools/fake-agent.php --url=http://custos.lan --code='СЮДА-КОД' --once
```

✅ Последние строки вывода:

```
FAKE-PC-01: waiting for approval on the site (key fingerprint XXXX-XXXX-XXXX-XXXX); 3 report(s) kept in the outbox
done: every answer was signed and accepted
```

**Шаг 15.** На сайте обновите страницу «Машины». В блоке «Ждут подтверждения» появилась `FAKE-PC-01`, отпечаток ключа совпадает с выводом. Нажмите **Подтвердить**.

**Шаг 16.** Запустите имитатор ещё раз, код уже не нужен:

```bash
php8.4 tools/fake-agent.php --url=http://custos.lan --once
```

✅ В конце снова `done: every answer was signed and accepted`. На сайте у машины `FAKE-PC-01` появились 4 задания, а в «Запусках» — запуски с журналами.

**Шаг 17. Убрать тестовую машину.**

```bash
php8.4 tools/fake-agent.php --reset
```

На сайте откройте машину `FAKE-PC-01` и нажмите **Удалить**.

Установка закончена.

---

## Часть 8. До подключения настоящих агентов

Агенты присылают журналы до 8 МБ, поэтому nginx и PHP должны пропускать запросы до 10 МБ. Имитатору это не нужно, поэтому шаг можно сделать позже.

**Шаг 18. PHP.** В сайте вкладка **Settings**, блок **PHP Settings**: значение `post_max_size` не меньше `10M` (обычно там уже больше).

**Шаг 19. nginx.** Эта команда выполняется **под root**:

```bash
nginx -T 2>/dev/null | grep client_max_body_size
```

✅ Значение не меньше `10m` (например `client_max_body_size 64m;`). Если команда ничего не вывела, у nginx лимит 1 МБ: в сайте вкладка **Vhost**, внутри блока `server { ... }` добавьте строку `client_max_body_size 10m;` и нажмите **Save**.

---

## Если что-то пошло не так

| Что видно | Что делать |
|---|---|
| `Permission denied` или `?php: No such file or directory` | Скрипт запущен без `php8.4`. Пишите `php8.4 bin/install.php`. |
| `PHP 8.3 or newer is required` | Запущено через `php`, а не через `php8.4`. |
| При входе на сайт «Форма устарела» | Сайт открыт по `http://`, а в `url` в `config.php` стоит `https://`, или пакет старше 06.11.2026. Исправьте `url` (шаг 10) или обновите пакет (раздел «Обновление»). |
| `config.php not found` | Не сделан шаг 9. |
| `Cannot connect to the database: ... Access denied` | В `config.php` имя базы, пользователь или пароль не совпадают с шагом 4. Исправьте (шаг 10) и запустите установку ещё раз. |
| `unzip: command not found` | Распакуйте архив в File Manager CloudPanel (правой кнопкой по файлу → Extract) и продолжайте с `cd server` в шаге 8. |
| Сайт показывает 404 или чужую страницу | Не сделан шаг 3: Root Directory должен быть `custos.lan/server/public`. |
| Браузер уходит на `https://` и ругается на сертификат | Не сделан шаг 5. |
| `custos.lan` не открывается вообще | Нет записи `custos.lan` в вашем локальном DNS, которая указывает на сервер. |
| В шаге 14 `custos.lan` не открывается с самого сервера | Под root добавьте в `/etc/hosts` строку `127.0.0.1 custos.lan`. |
| На странице «Внутренняя ошибка сервера», в журнале `var/log` строка `Server key ... is missing` | Ключа сервера нет, например папку `server` удалили и распаковали заново. Выполните `php8.4 bin/install.php`: он создаст ключ. Если агенты уже были подключены, их придётся зарегистрировать заново. |
| Ошибка 500, или в логе `Permission denied` для папки `var` или `Server key ... cannot be read` | Файлы распакованы или установка запущена под root. Под root выполните `chown -R custos:custos /home/custos/htdocs/custos.lan` и проверьте снова. |

Если ошибка другая, пришлите её текст целиком или скриншот.

---

## Обновление

**Старую папку `server` не удаляйте:** новый пакет распаковывается поверх неё. В ней лежат настройки и ключ сервера. Если ключ потерять, все подключённые агенты перестанут доверять серверу, и их придётся регистрировать заново.

1. Скачайте новый `custos-server.zip` (шаг 1) и загрузите его в папку `custos.lan` (шаг 6).
2. По SSH под пользователем сайта:
   ```bash
   cd ~/htdocs/custos.lan
   unzip -o custos-server.zip
   rm custos-server.zip
   php8.4 server/bin/migrate.php
   ```

✅ Последняя команда выводит `[ok] Database schema is up to date` или `[ok] Applied N migration(s)`.

Настройки (`config/config.php`), ключи сервера (`config/keys/`) и папка `var/` в пакет не входят, поэтому распаковка поверх их не трогает.

## Что сохранять в резервной копии

- базу `custos`;
- `server/config/config.php`;
- `server/config/keys/` — ключ сервера. Если он потеряется, все агенты перестанут доверять серверу, и их придётся регистрировать заново.

## Без CloudPanel

Корень сайта — `server/public`. Минимальный блок nginx:

```nginx
root /var/www/custos/server/public;
index index.php;
client_max_body_size 10m;

location / {
    try_files $uri /index.php?$query_string;
}

location ~ \.php$ {
    include fastcgi_params;
    fastcgi_param SCRIPT_FILENAME $document_root$fastcgi_script_name;
    fastcgi_pass unix:/run/php/php8.4-fpm.sock;
}
```

Если сайт стоит в подпапке или за обратным прокси, часть пути, начиная с `/api/v1/`, должна доходить до PHP без изменений: по ней проверяются подписи агентов. Адреса прокси перечислите в `trusted_proxies` в `config.php`.
