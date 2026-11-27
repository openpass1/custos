# The build already publishes stripped, self-contained .NET applications.
# Stripping their apphosts again also strips the embedded single-file agent.
%global __os_install_post %{nil}
%global debug_package %{nil}

Name:           custos-desktop
Version:        %{custos_version}
Release:        1
Summary:        CUSTOS backup agent and desktop client
License:        GPL-3.0-only
URL:            https://github.com/openpass1/custos
Source0:        payload.tar
Source1:        lifecycle.sh
Source2:        postrm
ExclusiveArch:  x86_64 aarch64
# Bundled runtime libraries are private. Only advertise dependencies on system libraries.
AutoReqProv:    no
Requires:       systemd, util-linux, ca-certificates, glibc >= 2.34, libgcc, libstdc++
Requires:       openssl-libs >= 3, libicu, krb5-libs, libz.so.1()(64bit)
Requires:       libX11, libICE, libSM, fontconfig, libXext, libXrandr, libXi, libX11-xcb.so.1()(64bit), libxkbcommon
Requires(post): systemd
Requires(preun): systemd
Requires(postun): systemd
Recommends:     cifs-utils, lvm2, btrfs-progs, xfsprogs, xdg-desktop-portal, gtk3, xorg-x11-server-Xwayland

%description
CUSTOS runs scheduled backups through its systemd service. The desktop client
manages tasks, shows logs and connects the agent to CUSTOS Monitor.

%install
mkdir -p "%{buildroot}"
tar --no-same-owner -xf "%{SOURCE0}" -C "%{buildroot}"
# Fedora installs package-private executables under /usr/libexec for its default SELinux file contexts.
mkdir -p "%{buildroot}/usr/libexec"
mv "%{buildroot}/usr/lib/custos-desktop" "%{buildroot}/usr/libexec/custos-desktop"
ln -sfn ../libexec/custos-desktop/agent/custos "%{buildroot}/usr/bin/custos"
ln -sfn ../libexec/custos-desktop/gui/custos-gui "%{buildroot}/usr/bin/custos-gui"
sed -i 's|^ExecStart=/usr/lib/custos-desktop/|ExecStart=/usr/libexec/custos-desktop/|' \
  "%{buildroot}/usr/lib/systemd/system/custos.service"

%pre
%include %{SOURCE1}
if [ "$1" -eq 1 ]; then
  for path in "$unit_path" /usr/bin/custos /usr/bin/custos-gui; do
    if path_exists "$path"; then
      echo "Cannot install %{name} over an existing file: $path" >&2
      exit 1
    fi
  done
fi

%post
%include %{SOURCE1}
if external_agent; then
  exit 0
fi
if systemd_running; then
  if { systemctl is-active --quiet "$unit" || [ -S /run/custos.sock ]; } && ! package_unit_loaded; then
    exit 0
  fi
  systemctl daemon-reload
fi
if [ "$1" -eq 1 ]; then
  systemctl --no-reload enable "$unit"
fi
if systemd_running && package_unit_loaded; then
  if [ "$1" -eq 1 ]; then
    systemctl start "$unit"
  else
    systemctl try-restart "$unit"
  fi
fi

%preun
%include %{SOURCE1}
if [ "$1" -eq 0 ] && systemd_running && ! external_agent && package_unit_loaded; then
  systemctl stop "$unit"
fi

%postun
%include %{SOURCE1}
if [ "$1" -eq 0 ]; then
  set -- remove
%include %{SOURCE2}
fi

%files
%defattr(-,root,root,-)
/usr/bin/custos
/usr/bin/custos-gui
/usr/libexec/custos-desktop
/usr/lib/systemd/system/custos.service
/usr/share/applications/org.custos.Desktop.desktop
/usr/share/icons/hicolor/256x256/apps/custos-gui.png
/usr/share/doc/custos-desktop
