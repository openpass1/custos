using System.IO.Pipes;
using Custos.Service.Platform;

namespace Custos.Service.Ipc;

/// <summary>Who is on the other end of a connection of the commands (custos status, run …), as Linux reports it for
/// the socket (SO_PEERCRED), not as the program claims.</summary>
/// <param name="Sid">The name of the account: tasks it saves take its home folder for %HOME% (section 6.2).</param>
internal sealed record ClientIdentity(string User, bool IsAdmin, int? SessionId, int? ProcessId, string? Sid = null)
{
    /// <summary>The groups whose members administer the computer (sudo on Debian and Ubuntu, wheel on RHEL and the others).</summary>
    private static readonly string[] AdminGroups = ["sudo", "wheel", "admin"];

    /// <summary>Reads the identity of the connected client. The client must have sent at least one message.</summary>
    public static ClientIdentity Read(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        string user = pipe.GetImpersonationUserName();
        var account = Native.Account(user) ?? throw new InvalidOperationException($"No account named {user}.");
        return new ClientIdentity(user, IsAdministrator(account), SessionId: null, ProcessId: null, Sid: account.Name);
    }

    /// <summary>root, or a member of sudo or wheel: the accounts that may become root and so change the service anyway.</summary>
    internal static bool IsAdministrator(UnixAccount account) =>
        account.Uid == 0 || AdminGroups.Select(Native.GroupId).OfType<uint>().Any(group => Native.IsMember(account, group));
}
