using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Custos.Core.Ipc;

/// <summary>Creates the server end of the service pipe with its access rules.</summary>
[SupportedOSPlatform("windows")]
public static class ServicePipe
{
    /// <summary>
    /// Creates one listening instance of the pipe.
    /// </summary>
    /// <param name="pipeName">Normally <see cref="PipeProtocol.PipeName"/>; tests use their own names.</param>
    /// <param name="firstInstance">
    /// True when the service does not hold the pipe yet. Creation then fails if any other process already created a
    /// pipe with this name, so a program started before the service cannot pose as it.
    /// </param>
    /// <exception cref="UnauthorizedAccessException">Another process owns a pipe with this name.</exception>
    public static NamedPipeServerStream CreateServer(string pipeName, bool firstInstance)
    {
#if CUSTOS_LEGACY
        return LegacyPipes.CreateServer(pipeName, firstInstance, CreateSecurity());
#else
        var options = PipeOptions.Asynchronous;
        if (firstInstance)
        {
            options |= PipeOptions.FirstPipeInstance;
        }

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            options,
            inBufferSize: 0,
            outBufferSize: 0,
            CreateSecurity());
#endif
    }

    /// <summary>
    /// Who may use the pipe: the service account (normally SYSTEM) has full control, any signed-in local user may
    /// read and write but not create instances of it, and connections from the network are refused.
    /// What a user may actually do is decided per request by the service (section 4).
    /// </summary>
    public static PipeSecurity CreateSecurity()
    {
        using var self = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(
            self.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }
}
