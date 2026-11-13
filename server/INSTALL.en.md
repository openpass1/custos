# Installing CUSTOS Monitor on CloudPanel, step by step

Русская версия: [INSTALL.md](INSTALL.md). Installing on a server without CloudPanel (plain nginx) is described in [Without CloudPanel](#without-cloudpanel) at the end.

Do the steps in order. Each step says what you should see afterwards. If you see something else, look at [If something goes wrong](#if-something-goes-wrong) at the end.

The examples use these names. If you chose others, use yours everywhere:

| What | In the examples |
|---|---|
| Site address | `custos.lan` |
| PHP version of the site | 8.4, so the commands say `php8.4`. If CloudPanel uses 8.5, type `php8.5` |
| Site user in CloudPanel | `custos` |
| Database | `custos` |
| Database user | `custos` |

Two rules:

- Every SSH command runs **as the site user** (`custos`), not as root.
- PHP scripts are always started like this: `php8.4 bin/install.php`. Without `php8.4` in front you get `Permission denied` or `?php: No such file or directory`. No `chmod` is needed.

---

## Part 1. Download the package (on your computer)

**Step 1.** Open the repository on GitHub. Go to **Actions** and choose **Server** on the left. Open the top run with a green check mark. At the bottom of the page, in **Artifacts**, click `custos-server`.

✅ The file `custos-server.zip` is downloaded. Do not unpack it on your computer.

> The package is kept for 14 days. If Artifacts is empty, press **Run workflow** on the **Actions → Server** page, wait a couple of minutes and open the new run.

---

## Part 2. CloudPanel (in the browser)

**Step 2. Site.** **Sites → + Add Site → Create a PHP Site**. Fill in:

- Application: **Generic**
- Domain Name: `custos.lan`
- PHP Version: **8.4** or newer
- Site User: `custos`, Site User Password: make one up and write it down (you need it for SSH)

Press **Create**.

✅ The site is in the list of sites.

**Step 3. Root folder.** Open the site, **Settings** tab. In **Root Directory**, replace `custos.lan` with `custos.lan/server/public`. Press **Save**.

**Step 4. Database.** In the site, **Databases → Add Database**:

- Database Name: `custos`
- Database User Name: `custos`
- Database User Password: make one up and write it down (you need it in step 10)

✅ The database is in the list.

**Step 5. Only if the site will work over HTTP (without a certificate).** In the site, **Vhost** tab. Find this block, delete it completely, then **Save**:

```nginx
if ($scheme != "https") {
  rewrite ^ https://$host$request_uri permanent;
}
```

If there is no such block, do nothing. With this block agents cannot work over HTTP.

---

## Part 3. Upload the package to the server

**Step 6.** In the site, **File Manager** tab. Open the folder `custos.lan` and upload `custos-server.zip` there (**Upload** button).

WinSCP works too: protocol SFTP, login `custos` and the password from step 2, folder `/home/custos/htdocs/custos.lan/`.

✅ The folder `custos.lan` contains `custos-server.zip`.

---

## Part 4. Installing over SSH

**Step 7. Log in as the site user.** On your computer (PowerShell or PuTTY):

```bash
ssh custos@server-address
```

The password is from step 2. Then check who you are:

```bash
whoami
```

✅ It prints `custos`. If it prints `root`, log out (`exit`) and log in as `custos`.

**Step 8. Unpack the package.**

```bash
cd ~/htdocs/custos.lan
unzip -o custos-server.zip
rm custos-server.zip
cd server
ls
```

✅ `ls` shows: `INSTALL.en.md INSTALL.md README.md bin composer.json composer.lock config lang migrations public src templates tools vendor zabbix`.

From here on every command runs in the folder `~/htdocs/custos.lan/server`.

**Step 9. Create the settings file.**

```bash
cp config/config.example.php config/config.php
```

The command prints nothing, that is normal.

**Step 10. Fill in the settings.**

```bash
nano config/config.php
```

Change the values in these lines (keep the quotes):

```php
        'name' => 'custos',          // database name from step 4
        'user' => 'custos',          // database user from step 4
        'password' => 'change-me',   // ← database password from step 4
...
    'url' => 'http://custos.lan',    // ← site address as it is opened in the browser, with http:// or https://
...
    'timezone' => 'UTC',             // ← your time zone, for example 'Europe/Berlin'
```

Save: **Ctrl+O**, then **Enter**. Exit: **Ctrl+X**.

**Step 11. Run the installation.** `--language=en` makes English the default language of the site and of the messages (without it the site starts in Russian; it can be changed later under Settings):

```bash
php8.4 bin/install.php --language=en
```

The script asks three questions:

1. `Login of the first administrator:` the login for the site, for example `admin`.
2. `Password (at least 8 characters):` a password of at least 8 characters. It is not shown while you type, that is normal.
3. `Repeat the password:` the same password again.

✅ The result looks like this (your code and fingerprint differ):

```
[ok] PHP 8.4.x with all required extensions
[ok] Database 10.11.x-MariaDB
[ok] Applied migration 001_init.sql
...
[ok] Applied migration 004_analytics.sql
[ok] Folder /home/custos/htdocs/custos.lan/server/var is ready
[ok] Created the server key pair in /home/custos/htdocs/custos.lan/server/config/keys
Login of the first administrator: admin
Password (at least 8 characters):
Repeat the password:
[ok] Created administrator "admin"
[ok] Generated the agent registration key
[ok] Generated zabbix_token
[ok] Generated wall_token
[ok] Default language: en

Server key fingerprint:  XXXX-XXXX-XXXX-XXXX
Connection string:       http://custos.lan#XXXXX-XXXXX-XXXXX-XXXXX-XXXXX#XXXX-XXXX-XXXX-XXXX
It is also shown on the site under Settings.

Add a cron job that runs every minute:
  * * * * * /usr/bin/php8.4 /home/custos/htdocs/custos.lan/server/bin/cron.php
```

Every line starts with `[ok]`. The **Connection string** is the server address and the registration code together. You paste it into the CUSTOS program on a machine. You do not have to write it down: it is always on the site under Settings.

If a line starts with `[error]`, fix what it says and run `php8.4 bin/install.php --language=en` again. Running it again breaks nothing.

---

## Part 5. Cron (in CloudPanel)

**Step 12.** In the site, **Cron Jobs → Add Cron Job**:

- Name: `custos`
- Schedule: every minute (all fields `*`)
- Command:
  ```
  /usr/bin/php8.4 /home/custos/htdocs/custos.lan/server/bin/cron.php
  ```

Press **Add Cron Job**.

---

## Part 6. Check the site

**Step 13.** Open `http://custos.lan` in the browser.

✅ The CUSTOS login page opens. The browser tab shows the CUSTOS shield icon. Log in with the login and password from step 11. You see Home: the tile "Machines 0 / 0", the block "Needs attention" saying "No problems." and below it "Results for 30 days" saying "No approved machines yet.".

Each user chooses their own language (Russian or English), theme (light, dark or same as the system) and password: click your login at the top right.

If "Needs attention" shows a red message about cron, wait a minute and reload the page. If the message stays, the cron job from step 12 does not work: check the path to `cron.php` and the PHP version in the command.

---

## Part 7. Check the work with an agent (fake agent)

The fake agent behaves like a real agent: it registers and sends tasks and runs. The commands run over SSH as in part 4, in the folder `~/htdocs/custos.lan/server`.

**Step 14.** Take the connection string on the site: Settings, field "Connection string", button "Copy". Paste it into the command between the single quotes:

```bash
php8.4 tools/fake-agent.php --connect='PASTE-STRING-HERE' --once
```

✅ The last lines of the output:

```
FAKE-PC-01: waiting for approval on the site (key fingerprint XXXX-XXXX-XXXX-XXXX); 3 report(s) kept in the outbox
done: every answer was signed and accepted
```

**Step 15.** On the site, reload the Machines page. `FAKE-PC-01` is in the block "Awaiting approval", and its key fingerprint matches the output. Press **Approve**.

**Step 16.** Run the fake agent again. The code is not needed any more, only the site address:

```bash
php8.4 tools/fake-agent.php --url=http://custos.lan --once
```

✅ It ends with `done: every answer was signed and accepted` again. On the site the machine `FAKE-PC-01` has 4 tasks, and Runs lists runs with logs. On the Machines page tasks are shown as colored marks: green is fine, yellow has warnings, red is an error. If the fake agent picked the result "error", Home shows this error under "Needs attention".

The machine page now has a "Storages" block: where the tasks write, how much space is free and which backup sets are there. A forecast of when the space runs out appears once there is data for at least a day. If 14 days or fewer are left (the limit is in Settings → Thresholds), Home shows a "space" problem and one message is sent. Run the fake agent once more with the same command: the page of the first task shows charts (source size, written, duration), and the heat map on Home gets a colored cell for today.

**Step 17. Remove the test machine.**

```bash
php8.4 tools/fake-agent.php --reset
```

On the site open the machine `FAKE-PC-01` and press **Delete**.

The installation is finished.

---

## Part 8. Before connecting real agents

Agents send logs of up to 8 MB, so nginx and PHP must accept requests of up to 10 MB. The fake agent does not need this, so the step can wait.

**Step 18. PHP.** In the site, **Settings** tab, **PHP Settings** block: `post_max_size` is at least `10M` (usually it is already more).

**Step 19. nginx.** This command runs **as root**:

```bash
nginx -T 2>/dev/null | grep client_max_body_size
```

✅ The value is at least `10m` (for example `client_max_body_size 64m;`). If the command prints nothing, nginx allows only 1 MB: in the site, **Vhost** tab, inside the `server { ... }` block add the line `client_max_body_size 100m;` and press **Save** (agents need 10 MB; more is needed to upload update packages through the site).

---

## Part 9. Telegram and email notifications (optional)

Messages are sent only about problems: a task ended with an error, or missed a start while the machine was on. One message per problem, no repeats.

**Telegram.**

1. In Telegram, send `/newbot` to `@BotFather` and choose a name. It sends a token like `123456789:AA...`.
2. Add your bot to the group that should get the messages and write any message in the group. For personal messages just send `/start` to the bot.
3. Find the Chat ID. Open in the browser (put the token from item 1 instead of `TOKEN`):
   ```
   https://api.telegram.org/botTOKEN/getUpdates
   ```
   Find `"chat":{"id":-100...`. The number with its minus sign is the Chat ID. A group's starts with `-100`, a personal chat's has no minus. If it is empty (`"result":[]`), write one more message in the group and reload the page.
4. On the site: Settings → tab "Telegram and email". Paste the token and the Chat ID, press **Save and send a test to Telegram**.

✅ A green message "Test message sent" at the top, and a message from the bot in Telegram.

**Email.** On the same tab fill in the SMTP server, port, encryption, mailbox login and password, the sender address and the recipients. Press **Save and send a test email**.

✅ A green message "Test message sent", and the email arrived (check the spam folder too).

The token and the password are not shown after saving. To keep them, leave the field empty.

---

## Part 10. Zabbix and the wall screen (optional)

### Zabbix

The template works with Zabbix 7.0 and newer (checked on 7.0 and 8.0). Zabbix makes one request a minute and gets everything at once: machines, tasks and the state of the site itself. It finds machines and tasks by itself, once an hour.

**Step 20. Check that the Zabbix server can reach the site.** On the Zabbix server run (with your site address):

```bash
curl -s http://custos.lan/api/health
```

✅ The answer is `{"status":"ok","db":"ok","cron_lag_s":...}`. If it is `"status":"fail"` and `cron_lag_s` is above 300 or `null`, cron does not work (step 12). If there is no answer, the Zabbix server cannot reach the site: check DNS and the network.

**Step 21. Import the template.** On the site: Settings → tab "Zabbix and wall screen". Click the link `custos_template.yaml` to download the file. In Zabbix: **Data collection → Templates**, **Import** at the top right, choose the file, **Import**.

✅ The template list has `CUSTOS by HTTP`.

**Step 22. Create a host.** **Data collection → Hosts → Create host**:

- Host name: `CUSTOS`
- Templates: `CUSTOS by HTTP`
- Host groups: any, for example `Applications`
- Interfaces: none needed

**Macros** tab, press **Add** twice:

| Macro | Value |
|---|---|
| `{$CUSTOS.URL}` | the site address from the page "Zabbix and wall screen", without `/` at the end, for example `http://custos.lan` |
| `{$CUSTOS.TOKEN}` | the token from the same page ("Copy" button); value type "Secret text" |

Press **Add**.

**Step 23. Check.** After 1–2 minutes open **Monitoring → Latest data** and filter by the host `CUSTOS`.

✅ "CUSTOS: health" is `ok`, "CUSTOS: machines" is the number of machines. Machines and tasks appear within an hour. To skip the wait: **Data collection → Hosts**, the **Discovery** link in the `CUSTOS` row, tick both rules and press **Execute now**, then reload Latest data after a minute.

What Zabbix reports:

| Trigger | Severity |
|---|---|
| "machine / task: backup failed": the task ended with an error | High |
| "machine / task: backup did not start on schedule": a missed start while the machine was on | Average |
| "machine / task: no recent successful copy" | Warning |
| "machine / task: storage will be full soon": a storage of the task fills up within 14 days (the same as the "space" problem on the site) | Warning |
| "machine has been offline for more than 24h" (the macro `{$CUSTOS.OFFLINE.MAX}` sets the time) | Warning |
| "monitoring site is unhealthy (database or cron)" | High |
| "no data from the monitoring site for 10 minutes" | High |

Task status code (the item "status code"): 0 fine, 1 running or paused, 2 warning or anomaly, 3 missed while the machine was off, 4 no recent successful copy, 5 missed, 6 error, 9 disabled or not run yet. If a task has several problems, the code shows the most serious one. While a new run goes, an error or a missed start stays visible (the code does not change to 1).

### Wall screen

**Step 24.** On the same tab copy the wall screen "Link" and open it in the browser of a TV or a monitor (full screen: F11). No login is needed: the token in the link gives access. The screen is dark, problems are on top in large print, machine tiles below; it refreshes by itself every 30 seconds. If the connection to the server is lost, a red "No connection to the server" note appears at the top.

Do not share the link publicly. If it got to the wrong place, press "Generate a new token": the old link stops working at once, and the screens need the new one.

**Limit the addresses.** In the field "Allowed from" on the same tab list the addresses of the Zabbix server and the screens (one per line, networks like `192.0.2.0/24` are fine) and press "Save". An empty field means any address. This is a separate list: the wall screen does not open the site itself, and `/api/health` is always open.

---

## If something goes wrong

| What you see | What to do |
|---|---|
| `Permission denied` or `?php: No such file or directory` | The script was started without `php8.4`. Type `php8.4 bin/install.php`. |
| `PHP 8.3 or newer is required` | It was started with `php` instead of `php8.4`. |
| "The form is out of date" when logging in | The site is opened over `http://`, but `url` in `config.php` says `https://`, or the package is older than 06.11.2026. Fix `url` (step 10) or update the package (see Updating). |
| `config.php not found` | Step 9 was skipped. |
| `Cannot connect to the database: ... Access denied` | The database name, user or password in `config.php` differ from step 4. Fix them (step 10) and run the installation again. |
| `unzip: command not found` | Unpack the archive in the CloudPanel File Manager (right click on the file → Extract) and continue with `cd server` in step 8. |
| The site shows 404 or someone else's page | Step 3 was skipped: Root Directory must be `custos.lan/server/public`. |
| The browser goes to `https://` and complains about the certificate | Step 5 was skipped. |
| `custos.lan` does not open at all | Your local DNS has no record for `custos.lan` pointing to the server. |
| In step 14 `custos.lan` does not open from the server itself | As root, add the line `127.0.0.1 custos.lan` to `/etc/hosts`. |
| Home shows a red message "Background jobs (cron) …" | Cron does not work: check step 12. By hand: `php8.4 bin/cron.php --verbose` in the `server` folder must print a `{ ... }` block without errors. |
| Telegram test: `Telegram is not reachable` | The server cannot reach `api.telegram.org`. If your provider blocks it, enter a proxy in the "Proxy" field, for example `socks5h://192.0.2.1:1080`. |
| Telegram test: `HTTP 401` | Wrong token: copy it from the `@BotFather` message again. |
| Telegram test: `HTTP 400 Bad Request: chat not found` | Wrong Chat ID, or the bot is not in the group (items 2–3 of part 9). |
| Telegram test: `HTTP 403` | The bot was removed from the group or blocked in the personal chat. Add it again. |
| Test email: `Could not connect to SMTP host`, `SMTP connect() failed` or `Could not authenticate` | Check the server, port and encryption (587 is STARTTLS, 465 is SSL), login and password. Many mail services require an "app password" instead of the normal one. |
| The page says "Internal server error", and the log in `var/log` has `Server key ... is missing` | The server key is gone, for example the `server` folder was deleted and unpacked again. Run `php8.4 bin/install.php`: it creates the key. Agents that were already connected have to register again. |
| Error 500, or `Permission denied` for the `var` folder or `Server key ... cannot be read` in the log | The files were unpacked or the installation was run as root. As root run `chown -R custos:custos /home/custos/htdocs/custos.lan` and check again. |
| Zabbix: the item "CUSTOS: status data" is not supported, the error has `401` | The `{$CUSTOS.TOKEN}` macro is wrong (or the token was generated again). Copy the token from the page "Zabbix and wall screen" again. |
| Zabbix: the error has `403` and `ip_not_allowed` | The Zabbix server address is not in "Allowed from". Add it (the address is in the answer) or clear the field. |
| Zabbix: the error has `Couldn't resolve host` or `Connection refused` | The Zabbix server cannot reach the site: check `{$CUSTOS.URL}` and step 20. |
| Wall screen: "The link is outdated" | The token was generated again. Open the new link from the page "Zabbix and wall screen". |
| Wall screen: "Access from … is not allowed by the site settings" | The screen's address is not in "Allowed from" (part 10). |

If the error is different, send its full text or a screenshot.

---

## Updating

**Do not delete the old `server` folder:** the new package is unpacked over it. It holds the settings and the server key. If the key is lost, every connected agent stops trusting the server and has to register again.

1. Download the new `custos-server.zip` (step 1) and upload it to the folder `custos.lan` (step 6).
2. Over SSH as the site user:
   ```bash
   cd ~/htdocs/custos.lan
   unzip -o custos-server.zip
   rm custos-server.zip
   php8.4 server/bin/migrate.php
   ```

✅ The last command prints `[ok] Database schema is up to date` or `[ok] Applied N migration(s)`.

After updating, open Home: the new version is shown at the bottom of the page. If "Needs attention" shows a red message about cron, check step 12: since version 0.2.0 missed runs are not checked and no messages are sent without cron.

The settings (`config/config.php`), the server keys (`config/keys/`) and the `var/` folder are not in the package, so unpacking over them leaves them alone.

**Version 0.3.0 (management).** `migrate.php` applies migration `003`. New:
- the Updates page: once a day the server asks `api.github.com` for a new 7-Zip version. If the server has no internet access, turn the check off on the same page and upload the 7-Zip installer by hand;
- uploaded update packages and 7-Zip installers are kept in `server/var/packages/`;
- a CUSTOS update package is uploaded through the site. If it is larger than the PHP limit, the site says so; the limit is raised in CloudPanel: site → **Settings** → **PHP Settings**, `upload_max_filesize` and `post_max_size`, for example `100M`.

**Version 0.4.0 (analytics and integrations).** `migrate.php` applies migrations `004` and `005` and creates two tokens (lines `[ok] Generated zabbix_token ...` and `[ok] Generated wall_token ...`). New:
- Home has a heat map of results for 30 days, the task page has charts, the machine page has a "Storages" block with a forecast of when the space runs out;
- a storage warning: when the forecast says the space runs out within 14 days, Home shows a "space" problem and one message is sent. The limit (or 0 to turn it off) is in Settings → Thresholds;
- Runs has a "Download CSV" button (for Excel);
- Zabbix and the wall screen (part 10);
- the user profile: click your login at the top right for language, theme and password. The default language for everyone is set under Settings → General;
- the site icon in the browser tab.

Nothing has to be set up. If the site runs without CloudPanel, check the nginx block: `favicon.ico`, `favicon.svg` and `assets/vendor/chart.umd.min.js` must be served from `server/public` like the other files (the example in [Without CloudPanel](#without-cloudpanel) does this).

**Version 0.4.1.** No migrations. A new storage warning limit applies as soon as the settings are saved, not an hour later. Long pages get a "Back to top" button at the bottom right.

**Version 0.4.2.** `migrate.php` applies migrations `006` and `007`: the default heartbeat interval is now 20 seconds instead of 60, and a machine counts as "offline" after 15 minutes instead of 60. A site that had the old values of 60 gets the new ones; values chosen by hand stay. Also in this version:
- in the task and template editors, "Create task" and "Save" send the form again (they silently did nothing when the schedule was not "by timer");
- Settings → General: a connection string, the server address and the registration code in one line for the program on the machine; hints for the heartbeat interval and "offline";
- a disabled machine contacts the site every 5 minutes, so it comes back sooner after it is enabled (needs a client with this change);
- the example path of sources and destinations follows the chosen kind;
- "Replace password" looks like a link. SSH keys and cloud drives cannot get a password from the site: they are made on the machine.

**Version 0.4.3.** No migrations. Settings → General keeps one connection string: it holds both the server address and the registration code. The separate "Server address" and "Registration code" fields are gone. `bin/install.php` prints the connection string at the end as well, and the fake agent takes it in `--connect='...'`. When there is nothing to update (the machines already run this version or newer), the site says so in a plain message, not in red.

**Version 0.4.4.** No migrations. Pages are put in order (the first part of the new look):
- a machine page starts with the name, the state and the main buttons, "Add task" and "Agent settings"; rare actions (inventory, rename, group, disable, delete) are in the "More" menu, and rename and group open a small dialog;
- one line under the name sums the machine up (group, OS, IP, versions, last contact); the full machine details fold into "Machine details";
- a task row has one button ("Run" or "Stop") and the rest under "⋯"; the task page has the same: the main button, "Edit" and "More";
- while nothing runs, one line with the nearest run replaces the empty "Now" block; empty blocks take one line;
- buttons get icons and one set of rules: blue is the main action, white an ordinary one, a red outline deletes or does something risky and asks first;
- the tables of "Machines", "Runs", "Audit log", "Users" and "Notices" sit in panels, and a row lights up under the pointer;
- the "Copy" button answers "✓ Copied".

## What to keep in backups

- the `custos` database;
- `server/config/config.php`;
- `server/config/keys/`: the server key. If it is lost, every agent stops trusting the server and has to register again.
- `server/var/packages/`: optional, update packages can be uploaded again.

## Without CloudPanel

An example for Ubuntu 24.04 (PHP 8.3, MariaDB, nginx). Other systems need the same parts: PHP 8.3 or newer with PHP-FPM and the extensions `pdo_mysql`, `curl`, `mbstring`, `openssl`, `zlib`; MariaDB 10.11+ or MySQL 8.0+; nginx. Debian 12 has PHP 8.2, which is not enough: install PHP 8.4 from the packages.sury.org repository and type `php8.4` everywhere.

Every command in this section runs **as root** unless marked otherwise. The site runs as a separate user `custos`, like in CloudPanel.

**Step N1. Packages.**

```bash
apt update
apt install -y nginx mariadb-server php8.3-fpm php8.3-cli php8.3-mysql php8.3-curl php8.3-mbstring unzip
```

**Step N2. Site user and folder.**

```bash
adduser --disabled-password --gecos "" custos
mkdir -p /var/www/custos
chown custos:custos /var/www/custos
```

**Step N3. Database.** Make up your own password instead of `PASSWORD` and write it down:

```bash
mariadb -e "CREATE DATABASE custos CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci; CREATE USER 'custos'@'localhost' IDENTIFIED BY 'PASSWORD'; GRANT ALL ON custos.* TO 'custos'@'localhost';"
```

**Step N4. PHP-FPM as the site user.** Create the file:

```bash
nano /etc/php/8.3/fpm/pool.d/custos.conf
```

with this content:

```ini
[custos]
user = custos
group = custos
listen = /run/php/php8.3-fpm-custos.sock
listen.owner = www-data
listen.group = www-data
pm = ondemand
pm.max_children = 10
php_admin_value[upload_max_filesize] = 100M
php_admin_value[post_max_size] = 100M
```

```bash
systemctl restart php8.3-fpm
```

**Step N5. nginx.**

```bash
nano /etc/nginx/sites-available/custos
```

```nginx
server {
    listen 80;
    server_name custos.lan;
    root /var/www/custos/server/public;
    index index.php;
    client_max_body_size 100m;   # agents need 10m; more is needed to upload update packages through the site

    location / {
        try_files $uri /index.php?$query_string;
    }

    location = /index.php {
        include fastcgi_params;
        fastcgi_param SCRIPT_FILENAME $document_root$fastcgi_script_name;
        fastcgi_pass unix:/run/php/php8.3-fpm-custos.sock;
    }

    location ~ \.php$ {
        return 404;
    }
}
```

```bash
ln -s /etc/nginx/sites-available/custos /etc/nginx/sites-enabled/custos
nginx -t
systemctl reload nginx
```

✅ `nginx -t` says `syntax is ok` and `test is successful`.

**Step N6. Package and installation.** Download `custos-server.zip` (step 1) and upload it to `/home/custos/` (WinSCP with protocol SFTP, or `scp custos-server.zip root@server-address:/home/custos/`). Then, **as the site user**:

```bash
chown custos:custos /home/custos/custos-server.zip
su - custos
cd /var/www/custos
unzip -o ~/custos-server.zip
rm ~/custos-server.zip
cd server
cp config/config.example.php config/config.php
nano config/config.php
```

In `config.php` fill in the same as in step 10 (database `custos`, user `custos`, the password from step N3, the site address, the time zone). Then:

```bash
php8.3 bin/install.php --language=en
exit
```

✅ The output is as in step 11, every line starts with `[ok]`.

**Step N7. Cron** (as root):

```bash
echo '* * * * * custos /usr/bin/php8.3 /var/www/custos/server/bin/cron.php' > /etc/cron.d/custos
```

Then check the site and the agent as in parts 6 and 7 (steps 13–17), typing `php8.3` instead of `php8.4`; the site folder is `/var/www/custos/server`. Updating: unpack the new package over `/var/www/custos` as the user `custos` and run `php8.3 server/bin/migrate.php`.

**HTTPS (optional).** If the site has a public name: `apt install -y certbot python3-certbot-nginx`, then `certbot --nginx -d your.domain`. Change `url` in `config.php` to `https://...`.

If the site is in a subfolder or behind a reverse proxy, the part of the path from `/api/v1/` on must reach PHP unchanged: agent signatures are checked against it. List the proxy addresses in `trusted_proxies` in `config.php`.
