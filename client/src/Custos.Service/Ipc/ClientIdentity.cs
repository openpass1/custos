using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Custos.Service.Ipc;

/// <summary>Who is on the other end of an interface connection, as Windows reports it (not as the interface claims).</summary>
/// <param name="Sid">The SID of the user: tasks they save take their profile for %USERPROFILE% (section 6.2).</param>
internal sealed partial record ClientIdentity(string User, bool IsAdmin, int? SessionId, int? ProcessId, string? Sid = null)
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>Reads the identity of the connected client. The client must have sent at least one message.</summary>
    public static ClientIdentity Read(NamedPipeServerStream pipe)
    {
        WindowsIdentity? identity = null;

        // The interface connects at the Identification level: while "impersonating" we may only read its token.
        // Copy the token here and look up names afterwards, back in the service's own security context.
        pipe.RunAsClient(() => identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query));

        using (identity)
        {
            if (identity is null)
            {
                throw new InvalidOperationException("Windows returned no token for the pipe client.");
            }

            return new ClientIdentity(
                identity.Name,
                IsAdministrator(identity),
                GetNamedPipeClientSessionId(pipe.SafePipeHandle, out uint sessionId) ? (int)sessionId : null,
                GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint processId) ? (int)processId : null,
                identity.User?.Value);
        }
    }

    /// <summary>
    /// Member of BUILTIN\Administrators. With UAC the group is present but "deny only" in a non-elevated token;
    /// that still counts (section 4), so an administrator does not need "Run as administrator".
    /// </summary>
    private static bool IsAdministrator(WindowsIdentity identity) =>
        identity.Groups?.Contains(Administrators) == true
        || identity.Claims.Any(c => c.Type == ClaimTypes.DenyOnlySid && c.Value == Administrators.Value);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint clientSessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
