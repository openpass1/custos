# Code signing policy

[По-русски ниже](#по-русски).

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by
[SignPath Foundation](https://signpath.org/).

**Status:** the project has applied to SignPath Foundation. Releases up to 1.0.0 are not signed; signing starts with
the first release after the application is approved. The release notes say whether the files of a release are signed.

## What is signed

- `CUSTOS-Setup-<version>.exe`, the installer of CUSTOS for Windows.
- The program files that are built from this repository: `Custos.exe`, `CustosService.exe`, `Custos.Updater.exe` and
  `Custos.Core.dll`, in the installer and in the update package `custos-<version>.zip`.

Files by other authors that ship with the program are not signed by this project. They are 7-Zip's `7z.dll` (taken
unchanged from the official 7-Zip installer, see [client/installer/7-zip.json](client/installer/7-zip.json)), the
libraries listed in [client/THIRD-PARTY-NOTICES.txt](client/THIRD-PARTY-NOTICES.txt) and the .NET Desktop Runtime
installer by Microsoft. They keep their authors' signatures where they have them.

The monitoring site (CUSTOS Monitor) and custos-helper are scripts (PHP and bash) and are not code-signed. Check them,
like every file of a release, with `SHA256SUMS.txt` (see [Checking a download](README.md#checking-a-download)).

## How signed files are made

- Every release is built by GitHub Actions from the source code in this repository, by the workflow
  [.github/workflows/release.yml](.github/workflows/release.yml). It builds and tests the program, makes the installer
  and the update package and creates a draft release.
- Only files built by this workflow are sent for signing. Nothing built on a personal computer is signed.
- SignPath checks that each file comes from that build of this repository. Build scripts and the workflow are part
  of the source code and are reviewed like it.
- Every signing request is approved by hand in SignPath before anything is signed.

## Team roles

| Role | Who |
|---|---|
| Committers and reviewers | [openpass1](https://github.com/openpass1), the maintainer. Changes from other people come as pull requests and are reviewed before they are merged. |
| Approvers | [openpass1](https://github.com/openpass1). Approves each signing request. |

Everyone in these roles uses multi-factor authentication on GitHub and on SignPath.

## Privacy

What the program and the site send over the network, and how to turn off what is on by default, is described in the
[privacy policy](PRIVACY.md). In short: nothing goes to the authors; the only connection that the user did not set
up is a daily question to GitHub about a new version, which can be turned off.

## Reporting a problem

If you think that a signed CUSTOS file is harmful or was not built from this repository, please report it privately as
described in [SECURITY.md](SECURITY.md), or to SignPath at support@signpath.io.

## Checking a signature

Right-click the file in Explorer → Properties → Digital Signatures. The signer is SignPath Foundation.

## По-русски

Бесплатную подпись кода предоставляет [SignPath.io](https://about.signpath.io/), сертификат выдан
[SignPath Foundation](https://signpath.org/).

**Состояние:** проект подал заявку в SignPath Foundation. Выпуски до 1.0.0 включительно не подписаны; подпись начнётся
с первого выпуска после одобрения заявки. В описании выпуска сказано, подписаны ли его файлы.

**Что подписывается.** Установщик `CUSTOS-Setup-<версия>.exe` и файлы программы, которые собираются из этого
репозитория: `Custos.exe`, `CustosService.exe`, `Custos.Updater.exe`, `Custos.Core.dll` (в установщике и в пакете
обновления `custos-<версия>.zip`). Файлы других авторов этот проект не подписывает: `7z.dll` из официального
установщика 7-Zip, библиотеки из [client/THIRD-PARTY-NOTICES.txt](client/THIRD-PARTY-NOTICES.txt) и установщик .NET
Desktop Runtime от Microsoft; у них остаются подписи их авторов, если они есть. Сайт мониторинга и custos-helper — это
скрипты (PHP и bash), их проверяют по `SHA256SUMS.txt`.

**Как получаются подписанные файлы.** Каждый выпуск собирает GitHub Actions из исходного кода этого репозитория
([.github/workflows/release.yml](.github/workflows/release.yml)): сборка, тесты, установщик, пакет обновления,
черновик выпуска. На подпись уходят только файлы этой сборки; собранное на чьём-то компьютере не подписывается. SignPath
проверяет, что файл получен именно этой сборкой этого репозитория. Каждый запрос на подпись одобряется вручную.

**Роли.** Авторы изменений и проверяющие: [openpass1](https://github.com/openpass1), сопровождающий проекта; изменения
от других людей приходят как pull request и проверяются до слияния. Одобряет подпись: [openpass1](https://github.com/openpass1).
Все, у кого есть эти роли, входят в GitHub и SignPath с двухфакторной проверкой.

**Конфиденциальность.** Что программа и сайт отправляют по сети и как выключить то, что включено по умолчанию, — в
[политике конфиденциальности](PRIVACY.md#по-русски).

**Сообщить о проблеме.** Если вы считаете, что подписанный файл CUSTOS вредоносный или собран не из этого репозитория,
сообщите не публично, как описано в [SECURITY.md](SECURITY.md), или в SignPath: support@signpath.io.

**Проверить подпись.** Правой кнопкой по файлу в Проводнике → «Свойства» → «Цифровые подписи». Подписант — SignPath
Foundation.
