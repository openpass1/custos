# Privacy policy

[По-русски ниже](#по-русски).

CUSTOS has no telemetry, no usage statistics and no accounts with its authors. The authors receive nothing from your
computers or your site. Both parts send data only where you tell them to, with one exception that is on by default:
a daily question to GitHub about a new version. It can be turned off, as described below.

## CUSTOS for Windows

The program sends data only:

- **To the places that your tasks name.** These are folders and network shares; SFTP, FTP, WebDAV and S3 servers;
  Google Drive, Yandex Disk and Dropbox; Linux servers through custos-helper; and database servers for dumps. What is
  sent there, and whether it is encrypted, is set in each task.
- **To the cloud drive service while you add a cloud account.** This is the sign-in (OAuth) at Google, Yandex or
  Dropbox.
- **To the monitoring site that you connect it to** (Settings → Monitoring server). It sends the state of the
  computer, its tasks, and the results and logs of its runs, as described in [docs/protocol.md](docs/protocol.md).
- **To GitHub, when an administrator presses the 7-Zip update button.** The program downloads 7-Zip from
  [github.com/ip7z/7zip](https://github.com/ip7z/7zip).
- **To GitHub, once a day. This is on by default.** The service asks `api.github.com` whether a new CUSTOS version is
  out.
  - The request carries the name of the program and nothing about the computer, its files or its tasks. GitHub sees
    the computer's IP address, as with any web request.
  - A new version is downloaded only when an administrator presses **Update**.
  - To turn the check off, go to **Settings → Updates** and clear **Check GitHub for new versions once a day**.
  - While the computer is connected to a monitoring site, the program does not ask GitHub; the site decides about
    updates.

Links in the program window, such as **What's new**, the guides and the donation page, open in your browser only when
you click them.

Everything else stays on the computer. Settings, tasks and accounts are kept in `C:\ProgramData\CUSTOS`; passwords are
encrypted with Windows DPAPI. Logs and the run history are kept there too.

The installer shows a short version of this section before it installs the program.

## CUSTOS Monitor (the site)

The site runs on your server and keeps its data in your database. It sends data only:

- **To the agents (computers) that you connect to it.** These are answers to their requests, commands, and updates.
- **To the notification channels that you set up.** These are e-mail through your SMTP server, and a Telegram bot
  (`api.telegram.org`).
- **To GitHub, once a day. This is on by default.**
  - The site looks at the CUSTOS releases (new versions of the site and of the program) and at the 7-Zip releases
    (a new `7z.dll` for the computers). Only the release lists are read; nothing about the site is sent.
  - Packages are downloaded only when an administrator asks for them.
  - Each check can be turned off on the **Updates** page with **Turn the daily check off**.

## Third parties

Requests to GitHub fall under the
[GitHub General Privacy Statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement).
The data that you keep at a cloud drive, a remote server or Telegram falls under the policy of that service.

## По-русски

У CUSTOS нет телеметрии, статистики использования и учётных записей у авторов. Авторы ничего не получают с ваших
компьютеров и сайта. Обе части отправляют данные только туда, куда вы им сказали. Исключение одно, оно включено по
умолчанию: раз в день программа и сайт спрашивают GitHub о новой версии. Его можно выключить, как описано ниже.

### CUSTOS для Windows

Программа отправляет данные только:

- **Туда, куда указывают ваши задания.** Это папки и сетевые папки; серверы SFTP, FTP, WebDAV и S3; Google Диск,
  Яндекс Диск и Dropbox; Linux-серверы через custos-helper; серверы баз данных для дампов. Что туда уходит и шифруется
  ли, задаётся в каждом задании.
- **В облачный сервис, пока вы добавляете учётную запись облака.** Это вход (OAuth) в Google, Яндекс или Dropbox.
- **На сайт мониторинга, к которому вы её подключили** («Настройки» → «Сервер мониторинга»). Она отправляет состояние
  компьютера, его задания, итоги и журналы запусков, как описано в [docs/protocol.md](docs/protocol.md).
- **На GitHub, когда администратор нажимает кнопку обновления 7-Zip.** Программа скачивает 7-Zip с
  [github.com/ip7z/7zip](https://github.com/ip7z/7zip).
- **На GitHub, раз в день. Это включено по умолчанию.** Служба спрашивает `api.github.com`, не вышла ли новая версия
  CUSTOS.
  - В запросе есть название программы и ничего о компьютере, его файлах и заданиях. GitHub видит IP-адрес компьютера,
    как при любом обращении к сайту.
  - Новая версия скачивается, только когда администратор нажмёт «Обновить».
  - Чтобы выключить проверку, откройте «Настройки» → «Обновления» и снимите «Проверять новые версии на GitHub раз в
    день».
  - Пока компьютер подключён к сайту мониторинга, программа GitHub не спрашивает: об обновлениях решает сайт.

Ссылки в окне программы, например «Что нового», руководства и страница пожертвований, открываются в браузере, только
когда вы на них нажмёте.

Всё остальное остаётся на компьютере. Настройки, задания и учётные записи хранятся в `C:\ProgramData\CUSTOS`, пароли
зашифрованы Windows DPAPI. Там же журналы и история запусков.

Краткую версию этого раздела установщик показывает до установки программы.

### CUSTOS Monitor (сайт)

Сайт работает на вашем сервере и хранит данные в вашей базе. Он отправляет данные только:

- **Подключённым к нему агентам (компьютерам).** Это ответы на их запросы, команды и обновления.
- **В каналы уведомлений, которые вы настроили.** Это почта через ваш SMTP-сервер и бот Telegram (`api.telegram.org`).
- **На GitHub, раз в день. Это включено по умолчанию.**
  - Сайт смотрит выпуски CUSTOS (новые версии сайта и программы) и выпуски 7-Zip (новый `7z.dll` для компьютеров).
    Читаются только списки выпусков, о самом сайте ничего не отправляется.
  - Пакеты скачиваются, только когда их запросит администратор.
  - Каждую проверку можно выключить на странице «Обновления» кнопкой «Выключить ежедневную проверку».

### Другие сервисы

На обращения к GitHub распространяется
[GitHub General Privacy Statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement).
На данные, которые вы храните в облаке, на удалённом сервере или в Telegram, распространяется политика этого сервиса.
