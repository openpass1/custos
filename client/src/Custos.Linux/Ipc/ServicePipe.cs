using System.IO.Pipes;
using Custos.Core.Ipc;

namespace Custos.Service.Ipc;

/// <summary>
/// The server end of the connection of the commands on Linux: a Unix socket in /run (/var/run), where only root may
/// create files, so nobody can pose as the service. Every local user may connect; what they may do is decided per
/// request from who they are (<see cref="ClientIdentity"/>). It takes the place of the pipe of Windows in the shared
/// PipeServer, which finds this class before the one of Custos.Core.
/// </summary>
internal static class ServicePipe
{
    private const UnixFileMode Everyone = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead
        | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite;

    /// <param name="pipeName"><see cref="PipeProtocol.PipeName"/> for the socket of the service; tests give a full path.</param>
    /// <param name="firstInstance">No instance of ours exists yet: a file left by an earlier process is removed.</param>
    public static NamedPipeServerStream CreateServer(string pipeName, bool firstInstance)
    {
        string path = pipeName == PipeProtocol.PipeName ? PipeProtocol.UnixSocketPath : pipeName;
        if (firstInstance && File.Exists(path))
        {
            File.Delete(path);
        }

        var server = new NamedPipeServerStream(path, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        if (firstInstance)
        {
            File.SetUnixFileMode(path, Everyone);
        }

        return server;
    }
}
