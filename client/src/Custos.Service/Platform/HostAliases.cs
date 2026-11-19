// The agent code (Agent/, Ipc/) is shared with the macOS client (src/Custos.Mac), which compiles the same files
// against its own platform classes. These names say which ones a build uses.
global using HostPlatform = Custos.Service.Platform.WindowsBackupPlatform;
global using HostSecretProtector = Custos.Service.Platform.DpapiProtector;
