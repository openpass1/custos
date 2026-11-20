using System.IO.Pipes;
using Custos.Service.Platform;

namespace Custos.Service.Ipc;

/// <summary>Who is on the other end of an interface connection, as macOS reports it for the socket (not as the
/// interface claims).</summary>
/// <param name="Sid">The name of the account: tasks it saves take its home folder for %USERPROFILE% (section 6.2).</param>
internal sealed record ClientIdentity(string User, bool IsAdmin, int? SessionId, int? ProcessId, string? Sid = null)
{
    /// <summary>The groups whose members administer the Mac ("admin"; on Linux, for tests, "sudo" and "wheel").</summary>
    private static readonly string[] AdminGroups = OperatingSystem.IsMacOS() ? ["admin"] : ["admin", "sudo", "wheel"];

    /// <summary>Reads the identity of the connected client. The client must have sent at least one message.</summary>
    public static ClientIdentity Read(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        string user = pipe.GetImpersonationUserName();
        var account = Native.Account(user) ?? throw new InvalidOperationException($"No account named {user}.");
        return new ClientIdentity(user, IsAdministrator(account), SessionId: null, ProcessId: null, Sid: account.Name);
    }

    /// <summary>root, or a member of the group admin: the accounts that may use sudo and change the settings of the Mac.</summary>
    internal static bool IsAdministrator(UnixAccount account) =>
        account.Uid == 0 || AdminGroups.Select(Native.GroupId).OfType<uint>().Any(group => Native.IsMember(account, group));
}
