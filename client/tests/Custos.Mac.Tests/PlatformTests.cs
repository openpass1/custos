using System.Text;
using Custos.Service.Ipc;
using Custos.Service.Platform;

namespace Custos.Mac.Tests;

/// <summary>The platform classes of macOS that also work on Linux: the same code runs there in the tests.</summary>
public sealed class PlatformTests
{
    [Theory]
    [InlineData("//nas/backup/pc1/x", "nas", "backup", "pc1/x")]
    [InlineData(@"\\nas\backup", "nas", "backup", "")]
    [InlineData("//fileserver.office.example/share/a/b/c", "fileserver.office.example", "share", "a/b/c")]
    public void Network_paths_split_into_server_share_and_folder(string path, string server, string share, string rest)
    {
        Assert.True(SmbSession.TryParse(path, out string s, out string sh, out string r));
        Assert.Equal((server, share, rest), (s, sh, r));
    }

    [Theory]
    [InlineData("/Users/Shared/Data")]
    [InlineData("//nas")]
    [InlineData("relative/folder")]
    public void Other_paths_are_not_network_paths(string path) =>
        Assert.False(SmbSession.TryParse(path, out _, out _, out _));

    [Fact]
    public void Owner_and_permissions_come_back_as_they_were()
    {
        string folder = TestPlace.NewFolder("posix");
        string file = Path.Combine(folder, "a.txt");
        File.WriteAllText(file, "a");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        byte[] saved = PosixSecurity.Read(file)!;

        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        Assert.True(PosixSecurity.Write(file, saved));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(file));
        Assert.StartsWith("custos-posix-1:", Encoding.ASCII.GetString(saved), StringComparison.Ordinal);
    }

    [Fact]
    public void Permissions_of_Windows_are_left_alone()
    {
        string folder = TestPlace.NewFolder("ntfs");
        string file = Path.Combine(folder, "a.txt");
        File.WriteAllText(file, "a");
        var before = File.GetUnixFileMode(file);

        // A security descriptor of NTFS starts with revision 1; it means nothing on a Mac.
        Assert.False(PosixSecurity.Write(file, [1, 0, 4, 0x80, 0, 0, 0, 0]));
        Assert.Equal(before, File.GetUnixFileMode(file));
    }

    [Fact]
    public void Stored_passwords_are_encrypted_and_read_back()
    {
        var protector = new KeychainProtector();
        byte[] secret = Encoding.UTF8.GetBytes("пароль-123");

        byte[] stored = protector.Protect(secret);

        Assert.DoesNotContain("пароль", Encoding.UTF8.GetString(stored), StringComparison.Ordinal);
        Assert.Equal(secret, protector.Unprotect(stored));
    }

    [Fact]
    public void Root_is_an_administrator()
    {
        var root = Native.Account(0u);
        Assert.NotNull(root);
        Assert.True(ClientIdentity.IsAdministrator(root));
    }

    [Fact]
    public void An_account_without_admin_groups_is_not_an_administrator()
    {
        // "nobody" exists on macOS and on Linux and is in no group of administrators.
        var nobody = Native.Account("nobody");
        Assert.NotNull(nobody);
        Assert.False(ClientIdentity.IsAdministrator(nobody));
    }
}
