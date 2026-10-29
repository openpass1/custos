# Установка CUSTOS Monitor

Инструкция для этапа 1. Пример: сайт `custos.lan`, пользователь сайта `custos`. Подставьте свои значения.

Что нужно: CloudPanel с PHP 8.4 и MariaDB 10.11+ (или MySQL 8.0+). Composer на сервере не нужен: готовый пакет уже содержит `vendor/`.

## 1. Пакет

1. На GitHub откройте **Actions → Server**, выберите последний зелёный запуск нужной ветки (для релиза — `main`).
2. Внизу страницы, в разделе **Artifacts**, скачайте `custos-server`. Получится `custos-server.zip`, внутри папка `server/`.
3. Пакет хранится 14 дней. Если он пропал, нажмите **Run workflow** на странице **Actions → Server**.

## 2. Сайт и база в CloudPanel

1. **Sites → Add Site → Create a PHP Site**. Application: **Generic**, PHP Version: **8.4**, Domain Name: `custos.lan`, Site User: `custos`, пароль задайте свой.
2. В сайте: **Databases → Add Database**. Имя базы `custos`, пользователь `custos`, пароль задайте свой.
3. В сайте: **Settings → Root Directory** укажите `custos.lan/server/public` и сохраните.
4. Если агенты и браузер будут ходить по HTTP, а не по HTTPS: в сайте откройте **Vhost** и удалите блок, который перенаправляет на HTTPS, если он есть:
   ```nginx
   if ($scheme != "https") {
     rewrite ^ https://$host$request_uri permanent;
   }
   ```
   С таким перенаправлением агенты по HTTP работать не смогут.

## 3. Файлы и первый запуск

Загрузите `custos-server.zip` в `/home/custos/htdocs/custos.lan/` (WinSCP по SFTP под пользователем сайта или **File Manager** в CloudPanel). Дальше всё делается по SSH **под пользователем сайта, не под root**:

```bash
ssh custos@<адрес-сервера>
cd ~/htdocs/custos.lan
unzip -o custos-server.zip
rm custos-server.zip
cd server
cp config/config.example.php config/config.php
nano config/config.php
```

В `config.php` укажите пароль базы (`db.password`), адрес сайта (`url`, например `http://custos.lan`) и часовой пояс для показа дат (`timezone`, например `Europe/Berlin`). Сохраните: Ctrl+O, Enter, Ctrl+X.

```bash
php8.4 bin/install.php
```

Скрипт проверит PHP и базу, создаст таблицы, ключи сервера, спросит логин и пароль первого администратора и в конце покажет:

- **Registration code** — код регистрации, его вводят в агенте. Он же есть на сайте в разделе «Настройки».
- строку для cron.

Скрипт можно запускать повторно: он ничего не ломает и не создаёт второго администратора.

## 4. Cron

В сайте: **Cron Jobs → Add Cron Job**. Расписание: каждую минуту (`* * * * *`). Команда:

```
/usr/bin/php8.4 /home/custos/htdocs/custos.lan/server/bin/cron.php
```

## 5. Размер запросов

Агенты присылают журналы до 8 МБ. И nginx, и PHP должны пропускать запросы до 10 МБ.

- **PHP:** в сайте **PHP Settings** значение `post_max_size` должно быть не меньше `10M` (обычно там уже больше).
- **nginx:** под root выполните
  ```bash
  nginx -T 2>/dev/null | grep client_max_body_size
  ```
  Значение должно быть не меньше `10m`. Если строк нет, лимит nginx по умолчанию 1 МБ: добавьте `client_max_body_size 10m;` в **Vhost** сайта внутри блока `server { ... }`.

## 6. Проверка имитатором агента

Имитатор лежит в пакете. Он ведёт себя как настоящий агент: регистрируется, шлёт heartbeat, конфигурацию и запуски.

```bash
cd ~/htdocs/custos.lan/server
php8.4 tools/fake-agent.php --url=http://custos.lan --code='КОД-РЕГИСТРАЦИИ' --once
```

Код возьмите из вывода `install.php` или со страницы «Настройки», в одинарных кавычках. Ожидаемый конец вывода:

```
FAKE-PC-01: waiting for approval on the site (key fingerprint XXXX-XXXX-XXXX-XXXX); 3 report(s) kept in the outbox
done: every answer was signed and accepted
```

1. На сайте в «Машинах» появится `FAKE-PC-01` в «Ждут подтверждения». Отпечаток ключа совпадает с выводом. Подтвердите.
2. Запустите команду ещё раз (код уже не нужен):
   ```bash
   php8.4 tools/fake-agent.php --url=http://custos.lan --once
   ```
   На сайте у машины появятся задания и запуски с журналами.
3. Уберите следы: `php8.4 tools/fake-agent.php --reset`, а на сайте удалите машину `FAKE-PC-01`.

Если `custos.lan` не открывается с самого сервера, добавьте под root строку `127.0.0.1 custos.lan` в `/etc/hosts`.

Другие режимы имитатора (несколько машин, пауза, ошибки, выключенная машина) показывает `php8.4 tools/fake-agent.php --help`.

## 7. Обновление

1. Скачайте новый `custos-server` (раздел 1) и загрузите его в `/home/custos/htdocs/custos.lan/`.
2. Под пользователем сайта:
   ```bash
   cd ~/htdocs/custos.lan
   unzip -o custos-server.zip
   rm custos-server.zip
   php8.4 server/bin/migrate.php
   ```

`config/config.php`, ключи сервера (`config/keys/`) и папка `var/` в пакет не входят, поэтому обновление их не трогает.

## 8. Что сохранять в резервной копии

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
