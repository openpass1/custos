# CUSTOS Monitor

The monitoring server for CUSTOS backup agents: PHP 8.3+ without a framework, MariaDB 10.11+ (or MySQL 8.0+),
nginx with PHP-FPM. The specification is `docs/CUSTOS-monitor.md`, the agent protocol is `docs/protocol.md`.
Installation on CloudPanel is described in [INSTALL.md](INSTALL.md) (in Russian).

Stage 1 is done: login, users, CSRF, login rate limiting, IP allow-lists, audit log, server keys, the whole agent
API with signed requests and responses, machine approval, the machine page, run history and run logs.

## Layout

```
bin/         install.php, migrate.php, cron.php (command line only)
config/      config.example.php -> config.php; keys/ holds the server key pair (created by install.php)
lang/        interface strings
migrations/  SQL migrations, applied in name order
public/      web root: index.php front controller and assets
src/         Custos\ classes: Infra (config, DB, crypto), Domain (data), Http (agent API and web pages)
templates/   page templates
tools/       fake-agent.php and its Custos\Tools\ classes
tests/       PHPUnit tests
var/         sessions, logs, cron lock, fake agent state (created at run time)
```

## Local development

```bash
composer install
cp config/config.example.php config/config.php    # point it at a local database
php bin/install.php
php -S 127.0.0.1:8080 -t public public/index.php
```

`CUSTOS_CONFIG=/path/to/config.php` makes the scripts and the site use another configuration file.

## Fake agent

`tools/fake-agent.php` is an independent implementation of the agent side of the protocol. It registers, sends
heartbeats, the configuration snapshot, runs with logs and inventory, and answers commands. Agents keep their keys
and state in `var/fake-agents` between runs.

```bash
php tools/fake-agent.php --url=http://127.0.0.1:8080 --code='KEY#FINGERPRINT' --once   # one pass, exit code 0 if all went well
php tools/fake-agent.php --url=http://127.0.0.1:8080 --code='KEY#FINGERPRINT' --count=5 --run-now  # five machines until Ctrl+C
php tools/fake-agent.php --help
```

## Tests

The integration tests need an empty MariaDB or MySQL database that they may drop and recreate tables in.

```bash
CUSTOS_TEST_DB_USER=custos CUSTOS_TEST_DB_PASSWORD=custos vendor/bin/phpunit
```

Variables: `CUSTOS_TEST_DB_HOST`, `_PORT`, `_NAME`, `_USER`, `_PASSWORD` (defaults `127.0.0.1`, `3306`,
`custos_test`, `root`, empty). Without a database the integration tests are skipped.

## CI

`.github/workflows/server.yml` runs on changes under `server/`: PHP syntax check and PHPUnit against MariaDB 10.11,
then it builds the installable package (with `vendor/`, without tests) and uploads it as the `custos-server`
artifact.
