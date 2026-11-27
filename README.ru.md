# CUSTOS

Агенты резервного копирования для Windows, macOS и Linux-серверов с мониторингом на своём сервере. В Windows CUSTOS — бесплатная замена Cobian Backup 11.

[English version](README.md)

[![Выпуски клиента](https://img.shields.io/github/v/release/openpass1/custos?filter=client-v*&label=Client%20releases)](https://github.com/openpass1/custos/releases)
[![Сайт мониторинга](https://img.shields.io/github/v/release/openpass1/custos?filter=server-v*&label=Monitor)](https://github.com/openpass1/custos/releases)
[![Client build](https://github.com/openpass1/custos/actions/workflows/build.yml/badge.svg)](https://github.com/openpass1/custos/actions/workflows/build.yml)
[![Server](https://github.com/openpass1/custos/actions/workflows/server.yml/badge.svg)](https://github.com/openpass1/custos/actions/workflows/server.yml)
[![Лицензия: GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-blue)](LICENSE)

Агенты делают резервные копии на каждой машине. CUSTOS Monitor показывает ход копирования в одном месте, предупреждает
о проблемах и позволяет управлять заданиями удалённо. Агенты для Windows и macOS можно настроить локально. У агента
для Linux-серверов нет графического интерфейса, задания для него настраиваются в Monitor.

## Что установить

| Компонент | Где работает | Скачать | Инструкция |
|---|---|---|---|
| Агент для Windows | Windows 10 и 11, Windows Server 2019, 2022 и 2025 (x64) | [Выпуски](https://github.com/openpass1/custos/releases) · `CUSTOS-Setup-<версия>.exe` | [Windows](client/README.ru.md#windows) |
| Windows legacy | Windows 7 SP1 и 8.1 (32-битная сборка) | [Предварительный выпуск](https://github.com/openpass1/custos/releases/tag/client-legacy-v1.0.1) · `CUSTOS-Legacy-Setup-<версия>.exe` | [Версия для старых Windows](client/README.ru.md#версия-для-windows-7-и-81) |
| Агент для macOS | macOS 15+ (Apple silicon или Intel) | [Предварительный выпуск](https://github.com/openpass1/custos/releases/tag/client-v1.2.0) · `Custos-<версия>.pkg` | [macOS](client/README.ru.md#macos) |
| Агент для Linux-серверов | Linux-серверы с systemd и glibc 2.28+ (x86_64 или ARM64) | [Выпуски](https://github.com/openpass1/custos/releases) · `custos-linux-x64.tar.gz` или `custos-linux-arm64.tar.gz` | [Агент Linux](client/README.ru.md#custos-для-linux) |
| CUSTOS Monitor | Linux с nginx, PHP 8.3+, MariaDB 10.11+ или MySQL 8.0+ | [Выпуски](https://github.com/openpass1/custos/releases) · `custos-server-<версия>.zip` | [Установка и обновление](server/INSTALL.md) |

Откройте **Assets** в нужном выпуске и выберите установщик или архив для своей платформы. ZIP-пакеты обновления агентов
предназначены для раздела **«Обновления»** в Monitor. Предварительные выпуски можно скачать вручную или выбрать
**предварительный** канал выпусков GitHub в Monitor 1.4.3 и новее.

Графический клиент для рабочего стола Linux планируется. Установщика и объявленной даты выпуска пока нет.
Скрипт [custos-helper](helper/README.md) позволяет агенту CUSTOS читать файлы и дампы баз на Linux-сервере по SSH.

## Быстрый старт

- **Windows.** Запустите установщик и [настройте первое задание](client/README.ru.md#windows). Чтобы подключиться к
  Monitor, скопируйте строку подключения со страницы **«Установка»** и вставьте в программе в разделе
  **«Настройки» → «Сервер мониторинга»**. Подтвердите машину в Monitor в разделе **«Машины»**.
- **Windows legacy и macOS.** Системные требования и шаги установки описаны в инструкциях из таблицы.
- **Linux-сервер.** В CUSTOS Monitor 1.4.1 или новее добавьте пакет `custos-linux-<версия>.zip` в
  **«Обновлениях»**, скопируйте команду для Linux со страницы **«Установка»**, запустите её на Linux-машине и
  подтвердите машину в разделе **«Машины»**. [Установка Linux-агента и команды](client/README.ru.md#custos-для-linux)
- **Мониторинг.** Следуйте инструкции для [CloudPanel](server/INSTALL.md) или [Debian и Ubuntu без панели](server/INSTALL.md#debian-и-ubuntu-одной-командой).
  В разделе [«Обновление»](server/INSTALL.md#обновление) описаны обновление через сайт и установка пакета без интернета.

<img src="docs/screenshots/ru/client-main-window.png" alt="CUSTOS для Windows: главное окно со списком заданий и карточкой выбранного задания">

<img src="docs/screenshots/ru/site-dashboard.png" alt="CUSTOS Monitor: главная страница с семью компьютерами, открытой проблемой, двумя идущими копиями и результатами за 30 дней">

## CUSTOS для Windows

- Полные, инкрементальные, дифференциальные копии и зеркало (точное или безопасное, где удалённые файлы хранятся
  ещё какое-то время). Расписание по дням, неделям, месяцам или по таймеру; запуск, пропущенный из-за выключенного
  компьютера, выполняется после включения.
- Пишет сразу в место назначения, без временной копии на локальном диске. Назначения: локальная папка, сетевая
  папка со своей учётной записью, USB-диск по метке тома, SFTP, FTP и FTPS, WebDAV, S3 (с защитой от удаления
  Object Lock), Google Диск, Яндекс Диск и Dropbox. Одно задание может писать в несколько назначений сразу.
- Google Диск работает в выпущенной программе сразу. Для Яндекс Диска и Dropbox зарегистрируйте у сервиса своё
  бесплатное приложение и впишите его в «Настройки» → «Облачные диски» → «Свои приложения…»
  ([по шагам](client/README.ru.md#приложения-custos-у-сервисов)).
- Архивы 7z и ZIP с шифрованием AES-256, тома 7z, проверка новых и хранящихся копий.
- Хранение по количеству, по возрасту или по схеме «дед-отец-сын» (GFS). Набор удаляется только целиком, никогда
  после неудачного запуска, а последняя удачная полная копия остаётся всегда.
- Открытые файлы через VSS, права NTFS, низкий приоритет, чтобы на компьютере можно было работать во время копии.
- Дампы баз данных: MySQL, MariaDB и PostgreSQL, в том числе в OSPanel.
- Linux-серверы: файлы и дампы баз забираются по SSH через скрипт только для чтения
  [custos-helper](helper/README.md).
- Восстановление на дату, перенос заданий на другой компьютер (экспорт в файл и импорт, пароли по желанию в
  зашифрованном виде), импорт заданий из Cobian Backup 11.
- Пароль программы и урезанный интерфейс для пользователей, которые не администраторы (только состояние или скрыт);
  действия сразу над несколькими заданиями.
- Русский и английский язык, светлая и тёмная тема.

## CUSTOS Monitor

- Замечает не только упавшие копии, но и те, что вообще не случились: задание отключили, служба зависла, компьютер
  был выключен.
- Сводка с ходом выполнения в реальном времени, карта результатов за 30 дней, история запусков с журналами, место в
  хранилищах с прогнозом.
- Уведомления в Telegram и на почту, шаблон для Zabbix, экран для телевизора.
- Удалённое управление: запуск и остановка заданий, их редактирование, шаблоны для групп компьютеров, обновление
  программы и 7-Zip.
- Компьютер подтверждается на сайте по отпечатку ключа; каждый запрос и ответ подписаны.
- Поиск машин и заданий; русский и английский язык, светлая и тёмная тема, работает на телефоне.

## Документация

| Документ | Что в нём |
|---|---|
| Руководства агентов | [Windows](client/README.ru.md#windows), [legacy](client/README.ru.md#версия-для-windows-7-и-81), [macOS](client/README.ru.md#macos), [Linux](client/README.ru.md#custos-для-linux). Резервное копирование, места хранения, обновления, перенос заданий и разработка. |
| [server/INSTALL.md](server/INSTALL.md), [server/INSTALL.en.md](server/INSTALL.en.md) | Установка и обновление сайта на CloudPanel или на чистом nginx, Telegram, почта, Zabbix |
| [server/README.md](server/README.md) | Устройство сайта, тесты, имитатор агента (на английском) |
| [helper/README.md](helper/README.md) | Как забирать данные через custos-helper по SSH |
| [docs/](docs/) | Технические задания и [протокол](docs/protocol.md) между программой и сайтом |

## Проверка скачанного файла

В каждом выпуске есть `SHA256SUMS.txt`. В Windows сравните его с тем, что выведет
`certutil -hashfile CUSTOS-Setup-<версия>.exe SHA256`; в Linux выполните `sha256sum -c --ignore-missing SHA256SUMS.txt` в папке с
файлами. Файлы для Windows пока не подписаны цифровой подписью: проект подал заявку на бесплатную подпись от
SignPath Foundation, см. [политику подписи кода](CODE_SIGNING.md#по-русски).

## Подпись кода (Code signing policy) и конфиденциальность

Проект подал заявку на бесплатную подпись кода Windows в [SignPath Foundation](https://signpath.org/).
Текущий статус и порядок подписи описаны в [политике подписи кода](CODE_SIGNING.md#по-русски).

У CUSTOS нет телеметрии, авторам она ничего не отправляет. Единственное соединение, которое вы не настраиваете сами, —
ежедневный вопрос к GitHub о новой версии, и его можно выключить: [PRIVACY.md](PRIVACY.md#по-русски).

## Ошибки, идеи и безопасность

Ошибки и предложения: [Issues](https://github.com/openpass1/custos/issues), по-русски или по-английски. Об уязвимостях
пишите не публично, как описано в [SECURITY.md](SECURITY.md).

## Поддержать проект

CUSTOS бесплатна. Если она вам полезна, можно поддержать её развитие переводом в криптовалюте: кошельки,
их сети и QR-коды — в [DONATE.md](DONATE.md).

## Лицензия

CUSTOS — свободная программа под лицензией [GNU General Public License v3.0](LICENSE). В комплекте есть сторонние
компоненты под своими лицензиями: [client/THIRD-PARTY-NOTICES.txt](client/THIRD-PARTY-NOTICES.txt),
[server/THIRD-PARTY-NOTICES.txt](server/THIRD-PARTY-NOTICES.txt). 7-Zip (`7z.dll`) распространяется по лицензии GNU LGPL.

CUSTOS поставляется **без каких-либо гарантий** в той мере, в какой это допускает закон: программа даётся «как есть», и
её авторы не отвечают за ущерб от её использования, в том числе за потерю данных (разделы 15 и 16 лицензии). Время от
времени проверяйте копии, восстанавливая их.
