using System.Runtime.InteropServices;
using System.Text;

namespace Custos.Service.Platform;

/// <summary>An account of this computer, from the user database of the system (getpwnam).</summary>
internal sealed record UnixAccount(string Name, uint Uid, uint Gid, string Home);

/// <summary>
/// The few calls into the C library and the frameworks of macOS the service needs. Everything that has a different
/// layout on Linux is read for both, so that the tests can run the same code there.
/// </summary>
internal static unsafe partial class Native
{
    private const string LibC = "libc";
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
    private static partial int lstat(string path, byte* buffer);

    /// <summary>Whether the process runs as root.</summary>
    public static bool IsRoot => geteuid() == 0;

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
            // struct group { char *gr_name; char *gr_passwd; gid_t gr_gid; char **gr_mem; } on macOS and Linux.
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
        byte* buffer = stackalloc byte[512];
        if (lstat(path, buffer) != 0)
        {
            return null;
        }

        // macOS (64-bit inodes): st_dev int32, st_mode uint16, st_nlink uint16, st_ino uint64, st_uid, st_gid.
        // Linux x64: st_dev uint64, st_ino uint64, st_nlink uint64, st_mode uint32, st_uid, st_gid.
        return OperatingSystem.IsMacOS()
            ? (*(uint*)(buffer + 16), *(uint*)(buffer + 20), *(ushort*)(buffer + 4))
            : (*(uint*)(buffer + 28), *(uint*)(buffer + 32), *(uint*)(buffer + 24));
    }

    private static UnixAccount? ReadPasswd(IntPtr entry)
    {
        if (entry == IntPtr.Zero)
        {
            return null;
        }

        // macOS: pw_name, pw_passwd, pw_uid, pw_gid, pw_change (time_t), pw_class, pw_gecos, pw_dir, pw_shell, pw_expire.
        // Linux: pw_name, pw_passwd, pw_uid, pw_gid, pw_gecos, pw_dir, pw_shell.
        byte* p = (byte*)entry;
        string name = Marshal.PtrToStringUTF8(*(IntPtr*)p) ?? string.Empty;
        uint uid = *(uint*)(p + 16);
        uint gid = *(uint*)(p + 20);
        IntPtr dir = *(IntPtr*)(p + (OperatingSystem.IsMacOS() ? 48 : 32));
        return new UnixAccount(name, uid, gid, Marshal.PtrToStringUTF8(dir) ?? string.Empty);
    }

    // ---- macOS only ----------------------------------------------------------------------------------------------

    [LibraryImport(LibC)]
    private static partial int gethostuuid(byte* uuid, void* wait);

    /// <summary>The hardware UUID of the Mac (the one System Information shows), or null.</summary>
    public static string? HostUuid()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        byte* uuid = stackalloc byte[16];
        long* wait = stackalloc long[2];
        wait[0] = 5;
        wait[1] = 0;
        return gethostuuid(uuid, wait) == 0 ? new Guid(new ReadOnlySpan<byte>(uuid, 16), bigEndian: true).ToString().ToUpperInvariant() : null;
    }
}

/// <summary>
/// Strings, numbers and dictionaries of CoreFoundation for the frameworks of macOS (IOKit, Security, NetFS).
/// Every object made here is released by the caller with <see cref="Release"/>.
/// </summary>
internal static unsafe partial class CoreFoundation
{
    public const string Library = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100;
    private static readonly Lazy<IntPtr> Handle = new(() => NativeLibrary.Load(Library));

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);

    [LibraryImport(Library)]
    private static partial IntPtr CFDataCreate(IntPtr allocator, byte* bytes, nint length);

    [LibraryImport(Library)]
    private static partial nint CFDataGetLength(IntPtr data);

    [LibraryImport(Library)]
    private static partial byte* CFDataGetBytePtr(IntPtr data);

    [LibraryImport(Library)]
    private static partial IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr* keys, IntPtr* values, nint count, IntPtr keyCallbacks, IntPtr valueCallbacks);

    [LibraryImport(Library)]
    private static partial IntPtr CFDictionaryCreateMutableCopy(IntPtr allocator, nint capacity, IntPtr dictionary);

    [LibraryImport(Library)]
    private static partial IntPtr CFURLCreateWithString(IntPtr allocator, IntPtr text, IntPtr baseUrl);

    [LibraryImport(Library)]
    public static partial nint CFArrayGetCount(IntPtr array);

    [LibraryImport(Library)]
    public static partial IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

    [LibraryImport(Library)]
    private static partial nint CFStringGetLength(IntPtr text);

    [LibraryImport(Library)]
    private static partial nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFStringGetCString(IntPtr text, byte* buffer, nint size, uint encoding);

    [LibraryImport(Library)]
    public static partial void CFRelease(IntPtr value);

    /// <summary>kCFBooleanTrue.</summary>
    public static IntPtr True => Constant(Library, "kCFBooleanTrue");

    public static IntPtr String(string text) => CFStringCreateWithCString(IntPtr.Zero, text, Utf8);

    public static IntPtr Data(ReadOnlySpan<byte> bytes)
    {
        fixed (byte* pointer = bytes)
        {
            return CFDataCreate(IntPtr.Zero, pointer, bytes.Length);
        }
    }

    public static byte[] Bytes(IntPtr data) => new ReadOnlySpan<byte>(CFDataGetBytePtr(data), (int)CFDataGetLength(data)).ToArray();

    public static string? ToText(IntPtr text)
    {
        if (text == IntPtr.Zero)
        {
            return null;
        }

        nint size = CFStringGetMaximumSizeForEncoding(CFStringGetLength(text), Utf8) + 1;
        byte[] buffer = new byte[size];
        fixed (byte* pointer = buffer)
        {
            return CFStringGetCString(text, pointer, size, Utf8) ? Marshal.PtrToStringUTF8((IntPtr)pointer) : null;
        }
    }

    /// <summary>A URL from its text; null if the text is no URL.</summary>
    public static IntPtr Url(string text)
    {
        IntPtr value = String(text);
        try
        {
            return CFURLCreateWithString(IntPtr.Zero, value, IntPtr.Zero);
        }
        finally
        {
            CFRelease(value);
        }
    }

    /// <summary>A dictionary of CF objects (it retains them; the caller releases what it made itself).</summary>
    public static IntPtr Dictionary(params (IntPtr Key, IntPtr Value)[] entries)
    {
        IntPtr* keys = stackalloc IntPtr[entries.Length];
        IntPtr* values = stackalloc IntPtr[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            keys[i] = entries[i].Key;
            values[i] = entries[i].Value;
        }

        IntPtr cf = Handle.Value;
        return CFDictionaryCreate(
            IntPtr.Zero, keys, values, entries.Length,
            NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks"),
            NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks"));
    }

    /// <summary>A dictionary the receiver may change (NetFS asks for CFMutableDictionaryRef).</summary>
    public static IntPtr MutableDictionary(params (IntPtr Key, IntPtr Value)[] entries)
    {
        IntPtr fixedOne = Dictionary(entries);
        try
        {
            return CFDictionaryCreateMutableCopy(IntPtr.Zero, 0, fixedOne);
        }
        finally
        {
            Release(fixedOne);
        }
    }

    /// <summary>The value of an exported constant (<c>CFStringRef kSecClass</c> and the like) of a framework.</summary>
    public static IntPtr Constant(string library, string name) => *(IntPtr*)NativeLibrary.GetExport(NativeLibrary.Load(library), name);

    public static void Release(IntPtr value)
    {
        if (value != IntPtr.Zero)
        {
            CFRelease(value);
        }
    }
}

/// <summary>Runs a program of the system and gives its output; for the tools of macOS (tmutil, launchctl, mount).</summary>
internal static class Tool
{
    /// <summary>Exit code and output (standard output and errors together).</summary>
    public static (int ExitCode, string Output) Run(string program, IEnumerable<string> arguments, TimeSpan? timeout = null)
    {
        var start = new System.Diagnostics.ProcessStartInfo(program)
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
        return (process.ExitCode, (output.Result + errors.Result).Trim());
    }
}
