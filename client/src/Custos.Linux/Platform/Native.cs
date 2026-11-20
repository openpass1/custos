using System.Runtime.InteropServices;
using System.Text;

namespace Custos.Service.Platform;

/// <summary>An account of this computer, from the user database of the system (getpwnam).</summary>
internal sealed record UnixAccount(string Name, uint Uid, uint Gid, string Home);

/// <summary>
/// The few calls into the C library of Linux the service needs. Only calls glibc 2.28 and later export under these
/// names (statx, not stat: before 2.33 "stat" and "lstat" are no exported functions), with the layouts of x64 and
/// arm64, which are the same for everything read here.
/// </summary>
internal static unsafe partial class Native
{
    private const string LibC = "libc";
    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxBasicStats = 0x7ff;
    private static readonly Lock PasswdLock = new();

    [LibraryImport(LibC)]
    public static partial uint geteuid();

    [LibraryImport(LibC, StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int chown(string path, uint owner, uint group);

    [LibraryImport(LibC, StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int lchown(string path, uint owner, uint group);

    [LibraryImport(LibC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr getpwnam(string name);

    [LibraryImport(LibC)]
    private static partial IntPtr getpwuid(uint uid);

    [LibraryImport(LibC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr getgrnam(string name);

    [LibraryImport(LibC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int getgrouplist(string name, uint baseGroup, uint* groups, int* count);

    [LibraryImport(LibC, StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int statx(int dirfd, string path, int flags, uint mask, byte* buffer);

    [LibraryImport(LibC, SetLastError = true)]
    private static partial int poll(PollFd* fds, nuint count, int timeout);

    /// <summary>Whether the process runs as root.</summary>
    public static bool IsRoot => geteuid() == 0;

    /// <summary>
    /// Whether something was mounted or unmounted since the last call, for /proc/self/mountinfo open in
    /// <paramref name="mountInfo"/>: the kernel marks that file with POLLPRI on each change (proc(5)). True when it
    /// cannot tell.
    /// </summary>
    public static bool MountsChanged(SafeHandle mountInfo)
    {
        ArgumentNullException.ThrowIfNull(mountInfo);
        bool added = false;
        mountInfo.DangerousAddRef(ref added);
        try
        {
            const short pollPri = 0x002;
            var fd = new PollFd { Fd = (int)mountInfo.DangerousGetHandle(), Events = pollPri };
            return poll(&fd, 1, 0) != 0;
        }
        finally
        {
            if (added)
            {
                mountInfo.DangerousRelease();
            }
        }
    }

    /// <summary>The account with this name, or null if there is none.</summary>
    public static UnixAccount? Account(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (PasswdLock)
        {
            return ReadPasswd(getpwnam(name));
        }
    }

    /// <summary>The account with this number, or null if there is none.</summary>
    public static UnixAccount? Account(uint uid)
    {
        lock (PasswdLock)
        {
            return ReadPasswd(getpwuid(uid));
        }
    }

    /// <summary>The number of the group with this name, or null if there is none.</summary>
    public static uint? GroupId(string name)
    {
        lock (PasswdLock)
        {
            // struct group { char *gr_name; char *gr_passwd; gid_t gr_gid; char **gr_mem; }
            IntPtr group = getgrnam(name);
            return group == IntPtr.Zero ? null : *(uint*)((byte*)group + 16);
        }
    }

    /// <summary>Whether the account is a member of the group (as its main group or as an additional one).</summary>
    public static bool IsMember(UnixAccount account, uint group)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.Gid == group)
        {
            return true;
        }

        int count = 64;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint[] groups = new uint[count];
            int found = count;
            fixed (uint* pointer = groups)
            {
                if (getgrouplist(account.Name, account.Gid, pointer, &found) >= 0)
                {
                    return groups.AsSpan(0, Math.Min(found, count)).Contains(group);
                }
            }

            count *= 4;
        }

        return false;
    }

    /// <summary>Owner, group and permission bits of a file or folder (not following a symbolic link), or null.</summary>
    public static (uint Uid, uint Gid, uint Mode)? Stat(string path)
    {
        // struct statx (the same on every processor): stx_mask u32, stx_blksize u32, stx_attributes u64, stx_nlink u32,
        // stx_uid u32 (20), stx_gid u32 (24), stx_mode u16 (28) … 256 bytes in all.
        byte* buffer = stackalloc byte[256];
        if (statx(AtFdCwd, path, AtSymlinkNoFollow, StatxBasicStats, buffer) != 0)
        {
            return null;
        }

        return (*(uint*)(buffer + 20), *(uint*)(buffer + 24), *(ushort*)(buffer + 28));
    }

    private static UnixAccount? ReadPasswd(IntPtr entry)
    {
        if (entry == IntPtr.Zero)
        {
            return null;
        }

        // struct passwd: pw_name, pw_passwd, pw_uid (16), pw_gid (20), pw_gecos, pw_dir (32), pw_shell.
        byte* p = (byte*)entry;
        string name = Marshal.PtrToStringUTF8(*(IntPtr*)p) ?? string.Empty;
        uint uid = *(uint*)(p + 16);
        uint gid = *(uint*)(p + 20);
        IntPtr dir = *(IntPtr*)(p + 32);
        return new UnixAccount(name, uid, gid, Marshal.PtrToStringUTF8(dir) ?? string.Empty);
    }
}

/// <summary>struct pollfd of poll(2).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PollFd
{
    public int Fd;
    public short Events;
    public short Revents;
}

/// <summary>Runs a program of the system and gives its output: lvs, lvcreate, mount, systemctl and the like.</summary>
internal static class Tool
{
    /// <summary>The folders programs of the system are looked for in (the service of systemd has a short PATH).</summary>
    private static readonly string[] SystemFolders = ["/usr/sbin", "/usr/bin", "/sbin", "/bin", "/usr/local/sbin", "/usr/local/bin"];

    /// <summary>The full path of a program of the system ("lvcreate" → "/usr/sbin/lvcreate"), or null if it is not installed.</summary>
    public static string? Find(string name)
    {
        if (name.Contains('/', StringComparison.Ordinal))
        {
            return File.Exists(name) ? name : null;
        }

        foreach (string folder in SystemFolders)
        {
            string path = Path.Combine(folder, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>Exit code and output (standard output and errors together); 127 with a message when the program is
    /// not installed.</summary>
    public static (int ExitCode, string Output) Run(string program, IEnumerable<string> arguments, TimeSpan? timeout = null) =>
        Run(program, arguments, out _, timeout);

    /// <summary>As <see cref="Run(string, IEnumerable{string}, TimeSpan?)"/>, with standard output alone in <paramref name="standardOutput"/>.</summary>
    public static (int ExitCode, string Output) Run(string program, IEnumerable<string> arguments, out string standardOutput, TimeSpan? timeout = null)
    {
        standardOutput = string.Empty;
        string? path = Find(program);
        if (path is null)
        {
            return (127, $"{program}: not installed");
        }

        var start = new System.Diagnostics.ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // Messages of the tools in English and numbers without thousands separators, whatever the system says.
        start.Environment["LC_ALL"] = "C";
        start.Environment["LVM_SUPPRESS_FD_WARNINGS"] = "1";

        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException(program);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout ?? TimeSpan.FromMinutes(2)))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It ended meanwhile.
            }

            return (-1, $"{Path.GetFileName(program)}: timed out");
        }

        process.WaitForExit();
        standardOutput = output.Result;
        return (process.ExitCode, (output.Result + errors.Result).Trim());
    }

    /// <summary>What a tool said, on one line, for a log: its errors, or its name and exit code when it said nothing.</summary>
    public static string Reason(string output, string program, int code)
    {
        string said = string.Join(" ", output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        return said.Length > 0 ? said.TrimEnd('.') : $"{Path.GetFileName(program)}: {code}";
    }
}
