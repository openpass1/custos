# CUSTOS Linux desktop preview

[По-русски](README.ru.md)

## Install on Debian, Ubuntu or Linux Mint

The package targets Debian 12 and 13, Ubuntu 22.04, 24.04 and 26.04, and compatible Linux Mint versions. It requires systemd and an X11 or XWayland session.

1. Sign in to GitHub. Under **Actions**, open a successful **Linux desktop preview** run for the desired revision. Under **Artifacts**, download the ZIP named `custos-linux-desktop-preview-deb-amd64` or `custos-linux-desktop-preview-deb-arm64`. Choose **amd64** for a typical Intel or AMD PC or VM, or **arm64** for 64-bit ARM.
2. Extract the `.deb`, open it in your package installer and select **Install**. If your live session has no graphical package installer, open a terminal in the folder containing the package and run `sudo apt install ./filename.deb` with the actual filename.
3. Open CUSTOS from the applications menu. On a new machine the package installs and starts the backup service using systemd. In a live session without persistence, the package and settings last until reboot.
4. To manage a new machine through Monitor, copy the connection string from **Install → Windows and macOS**. On Linux, run `sudo custos connect 'connection string'` with that value. Approve the machine in Monitor and create a task. If the agent is already connected, its existing tasks appear in the window.

If you used `add-menu-entry.sh` with the standalone archive before, remove its per-user launcher from `${XDG_DATA_HOME:-$HOME/.local/share}/applications/org.custos.DesktopPreview.desktop` so the menu opens the installed copy.

To update the agent installed by this package, install a newer `.deb`. If Monitor installed the agent earlier, the package uses the existing service and Monitor can continue to update it.

## Standalone GUI archive

Build the optional preview from the repository root on Linux with the .NET 10 SDK and GNU `tar`:

```sh
bash client/linux/build-desktop-preview.sh
tar -xzf client/artifacts-linux/desktop-preview/custos-linux-desktop-preview-x64.tar.gz
./custos-linux-desktop-preview-x64/custos-gui
```

The bundle includes its .NET runtime. It uses the host's glibc, ICU, and graphical system libraries. The preview opens
an X11 window. On Wayland, the session must provide XWayland. The Linux backup agent is installed separately. Start
the window as your own user and keep the extracted files together. The window connects to the local agent at
`/var/run/custos.sock` and the agent applies its usual permissions. For a test agent listening elsewhere, set
`CUSTOS_SOCKET` to its trusted absolute socket path when starting the window.

The preview shows tasks, history and logs, and offers run controls. Use Monitor to configure tasks and settings.
Restore through Monitor or `custos restore`.

Run `custos-gui --demo` to open the window with sample data when testing without an agent.

For a launcher in your application menu, run `./custos-linux-desktop-preview-x64/add-menu-entry.sh`. It registers
the extracted copy for your user. Run it again if you move the folder. To remove the entry, delete
`org.custos.DesktopPreview.desktop` from `${XDG_DATA_HOME:-$HOME/.local/share}/applications`.

The build script accepts `x64` or `arm64` as its first argument and a custom output directory as its second argument.
The bundled PNG is the 256 px image from `client/src/Custos.Desktop/Assets/custos.ico`.

| Architecture and session | Verified |
| --- | --- |
| x64, Debian 13, X11 (Xorg) | Demo window, application-menu launcher, and a task listed from a local test agent |
| ARM64, cross-built on x64 | AArch64 apphost and native libraries. Runtime check pending |
| XWayland | Runtime check pending |
