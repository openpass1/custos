# CUSTOS Linux desktop preview

[По-русски](README.ru.md)

The desktop preview adds a graphical window to the CUSTOS Linux backup service. The service runs scheduled tasks
with the window closed. CUSTOS Monitor is optional for local backups.

## Download and install

Open **Assets** in a [GitHub release](https://github.com/openpass1/custos/releases) that includes Linux desktop and
choose the package for your distribution and processor. Both packages use systemd for the backup service and X11
for the window. Wayland sessions need XWayland.

| Distribution | 64-bit Intel or AMD | 64-bit ARM |
| --- | --- | --- |
| Debian 12/13, Ubuntu 22.04/24.04/26.04 and compatible Linux Mint | `custos-desktop_<version>_amd64.deb` | `custos-desktop_<version>_arm64.deb` |
| Fedora 44 | `custos-desktop-<version>-1.x86_64.rpm` | `custos-desktop-<version>-1.aarch64.rpm` |

For a test build, sign in to GitHub and open a successful
[Linux desktop preview run](https://github.com/openpass1/custos/actions/workflows/linux-desktop-preview.yml) for
the desired revision. Under **Artifacts**, download `custos-linux-desktop-preview-deb-amd64` or
`custos-linux-desktop-preview-deb-arm64` for Debian-based systems, or `custos-linux-desktop-preview-rpm-x86_64` or
`custos-linux-desktop-preview-rpm-aarch64` for Fedora. Extract the package from the ZIP.

Open the package in your graphical package installer and select **Install**. In a terminal, run the command for
your distribution from the download folder, using the actual filename.

```sh
sudo apt install ./filename.deb  # Debian, Ubuntu, Linux Mint
sudo dnf install ./filename.rpm  # Fedora
```

Open **CUSTOS** from the applications menu as your own user. On a fresh installation the package starts the
backup service. In a live session without persistence, the package and settings last until reboot.

To change the window language, choose **Settings → Interface language** and reopen CUSTOS. A desktop panel with
tray support can display the CUSTOS icon while the window is open.

## Update or remove the package

Install a newer package of the same format to update the window and its packaged agent. If Monitor installed the
agent earlier, the window connects to that existing service, which Monitor can continue to update separately.
Use matching versions of the window and the agent for all desktop features. On Debian-based systems, the
`custos-desktop` package replaces the earlier `custos-desktop-preview` package and keeps its data. The first
install under the new name enables the service. To keep it stopped through this migration, first run
`sudo systemctl mask --now custos.service`.

Run `sudo apt remove custos-desktop` or `sudo dnf remove custos-desktop` to remove the package. Tasks, settings, run
history and existing backups are retained.

If you previously ran `add-menu-entry.sh` from a standalone archive, remove the launcher it created under
`${XDG_DATA_HOME:-$HOME/.local/share}/applications` so the menu opens the packaged copy.

## Create your first backup task

1. Open CUSTOS from a Linux administrator account in the `sudo` or `wheel` group and choose **Create**.
2. Enter a name. Add the folder containing your files under **Sources**. Under **Destinations**, choose a separate
   backup folder outside the source.
3. Choose **Save**, select the task in the main window and choose **Run**. Check the result under **Run history**.

New Linux tasks request a filesystem snapshot by default. If the volume cannot be snapshotted, as with some live
VMs, the agent continues with a warning. If your task requires a snapshot, enable the option to stop on failure under
**Advanced**.

## Manage tasks

Select a task to edit, clone, switch on or off, or delete it. A clone starts switched off. The menu next to
**Create** imports and exports CUSTOS task files. Export requires a Linux administrator account. If an imported
task has incompatible paths, CUSTOS keeps those paths for editing and switches the task off.

In **Settings → All settings…** you can change service settings such as permissions, log retention and the
program password. The service has a separate language setting for its logs.

## Connect to CUSTOS Monitor

In CUSTOS, go to **Settings → Monitoring server**. Copy the connection string from **Install** in Monitor, paste it
into the connection field and choose **Connect**. Monitor 1.4.2 shows the connection string under
**Windows and macOS**. Once the machine registers, compare its key fingerprint with the one shown in Monitor and
approve it under **Machines**.

## Restore files

Choose **Restore**, select a task, a folder with backups, or a server or cloud location, then choose **Find backups**.
Select a backup date and a destination folder, and choose **Restore** again. The default skips files that already
exist. Use the archive password when the backup is encrypted and the service has no saved password for it. Restore
requires a Linux administrator account.

## Connect a cloud drive

Use a Linux administrator account. In **Settings → All settings…**, find **Accounts for network folders, servers and
cloud drives**, choose Google Drive, Yandex Disk or Dropbox, and open the sign-in page in your browser. Google Drive
returns the result to CUSTOS. For Yandex Disk or Dropbox, paste the code shown in the browser into the CUSTOS dialog.
Then select the connected account in the task's **Destinations**.

If the build has no built-in app for the provider, register your own and add it under **Cloud drives → Own applications…**.
See the [cloud drive guide](../../README.md#cloud-drives-google-drive-yandex-disk-dropbox) for supported formats and setup.

## Standalone GUI archive

If the Linux agent is already installed, use the standalone `custos-linux-desktop-<version>-x64.tar.gz` or
`custos-linux-desktop-<version>-arm64.tar.gz` from a desktop release. To build and open the archive from the repository
root, use the .NET 10 SDK and GNU `tar`. Choose a version and use `arm64` in place of `x64` if needed.

```sh
version=1.5.0
bash client/linux/build-desktop.sh x64 client/artifacts-linux/desktop "$version"
tar -xzf "client/artifacts-linux/desktop/custos-linux-desktop-$version-x64.tar.gz"
"./custos-linux-desktop-$version-x64/custos-gui"
```

The bundle includes its .NET runtime and uses the host's glibc, ICU and graphical system libraries. Install the
[Linux backup agent](../../README.md#custos-for-linux) separately. Start the window as your own user and keep the
extracted files together. It connects to the agent at `/var/run/custos.sock` and uses the agent's access rules. If you
run a test agent on a different socket, set `CUSTOS_SOCKET` to its trusted absolute path before opening the window.

Run `custos-gui --demo` to open the window with sample data when testing without an agent.

For a launcher in your application menu, run `./add-menu-entry.sh` from the extracted folder. It registers
the extracted copy for your user. Run it again if you move the folder. To remove the entry, delete
`org.custos.Desktop.desktop` from `${XDG_DATA_HOME:-$HOME/.local/share}/applications`. Early test builds used
`org.custos.DesktopPreview.desktop` instead.

The build script takes the architecture, an output directory and a version. By default it uses `x64`,
`client/artifacts-linux/desktop` and the first version in the [client changelog](../../CHANGELOG.md).
The bundled PNG is the 256 px image from `client/src/Custos.Desktop/Assets/custos.ico`.

## Build the release packages

In GitHub, open **Actions → Release → Run workflow**, select `client` and enter the top version in the client
changelog. Selecting `linux` builds the headless archives and Monitor update ZIP. Leave `linux_desktop` selected
to include the `.deb`, `.rpm` and standalone GUI archive, or clear it for an agent-only Linux release. Review the
draft release and its `SHA256SUMS.txt` before publishing.

## Compatibility checks

| Environment | Check |
| --- | --- |
| Debian 13 amd64, Wayland (Weston 14.0.2 and XWayland 24.1.6) | Upgraded from the 1.4 preview, kept service data and enablement, opened 1.5.0 from the installed launcher and connected to the running service as a desktop user. |
| Ubuntu 24.04, x64 and ARM64 | The **Linux desktop preview** workflow exercises `.deb` installation, reinstallation, removal and coexistence with a preinstalled agent. |
| Fedora 44 container, x86_64 and aarch64 | The workflow checks RPM installation, the launcher and binaries, and package removal. |
