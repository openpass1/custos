// The agent code of the Windows service (src/Custos.Service/Agent, Ipc) is compiled here as it is; these names point
// it at the classes of macOS.
global using HostPlatform = Custos.Service.Platform.MacBackupPlatform;
global using HostSecretProtector = Custos.Service.Platform.KeychainProtector;
global using NetworkLogon = Custos.Service.Platform.SmbSession;
