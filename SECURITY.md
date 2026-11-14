# Security policy

[По-русски ниже](#по-русски).

## Supported versions

Security fixes go into the latest release of each part: CUSTOS for Windows (`client-v…` releases) and CUSTOS Monitor
(`server-v…` releases). Please update before reporting a problem in an older version.

## Reporting a vulnerability

Please do not open a public issue for a vulnerability. Report it privately instead:

1. Open the [Security](https://github.com/openpass1/custos/security) tab of the repository.
2. Press **Report a vulnerability** and describe the problem.

Write in English or Russian. Useful details: the part and its version (the program shows it at the bottom of its main
window, the site at the bottom of every page), what an attacker can do, and the steps to reproduce it. Please leave out passwords, keys,
addresses and other data of your own systems.

You get an answer in the same report. Once a fix is released, the report is published as a security advisory, with
credit to you if you wish.

Areas that matter most: the signed protocol between the program and the site ([docs/protocol.md](docs/protocol.md)),
the Windows service and its pipe, passwords kept for tasks, update packages, and the site's login and IP allow-lists.

## По-русски

Исправления безопасности выходят в последнем выпуске каждой части: CUSTOS для Windows (выпуски `client-v…`) и
CUSTOS Monitor (выпуски `server-v…`).

Об уязвимости, пожалуйста, не пишите в открытые Issues. Откройте вкладку
[Security](https://github.com/openpass1/custos/security), нажмите **Report a vulnerability** и опишите проблему,
по-русски или по-английски. Сообщение увидит только автор проекта. Укажите часть и её версию, что может сделать
злоумышленник и как это повторить. Пароли, ключи, адреса и другие данные ваших систем присылать не нужно.
