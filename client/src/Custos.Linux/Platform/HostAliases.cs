// The agent code of the Windows service (src/Custos.Service/Agent, Ipc) is compiled here as it is; these names point
// it at the classes of Linux.
global using HostPlatform = Custos.Service.Platform.LinuxBackupPlatform;
global using HostSecretProtector = Custos.Service.Platform.KeyFileProtector;
global using NetworkLogon = Custos.Service.Platform.SmbSession;
