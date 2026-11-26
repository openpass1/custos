using System.Globalization;
using System.Runtime.InteropServices;
using Custos.Core;
using Microsoft.Win32.SafeHandles;

namespace Custos.Service.Platform;

/// <summary>Confines files read and written by Linux restores requested by the monitoring server.</summary>
internal static class MonitorRestoreFiles
{
    private const int PathOnly = 0x200000;
    private const int CloseOnExec = 0x80000;
    private const int NonBlocking = 0x800;
    // Linux arm64 overrides the asm-generic O_DIRECTORY and O_NOFOLLOW values.
    private static int DirectoryOnly => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x4000 : 0x10000;
    private static int NoFollow => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x8000 : 0x20000;
    private static int DirectoryFlags => PathOnly | CloseOnExec | DirectoryOnly | NoFollow;

    private const uint TypeBits = 0xf000;
    private const uint Regular = 0x8000;
    private const uint PrivateMode = 0x1c0; // 0700
    private const uint GroupAndOtherWrite = 0x12;
    private const uint Sticky = 0x200;

    public static string Output(Guid jobId) => Path.Combine(CustosInfo.DataDirectory, "restores", jobId.ToString("D"));

    public static string RecordFolder(Guid jobId) => Path.Combine(DataFolders.Database, "restore-jobs", jobId.ToString("D"));

    public static void Reserve(Guid jobId)
    {
        // Reserve the record first, so a job cannot be repeated after its output is moved away.
        MakePrivateDirectory(RecordFolder(jobId));
        MakePrivateDirectory(Output(jobId));
    }

    private static void MakePrivateDirectory(string jobFolder)
    {
        string parent = Path.GetDirectoryName(jobFolder)!;
        using var trustedParent = OpenDirectory(Path.GetDirectoryName(parent)!, requireProtected: true);
        _ = Native.mkdir(parent, PrivateMode);
        using var root = OpenDirectory(parent, requireProtected: true);
        if (Native.Stat(root) is not { } stat || stat.Uid != Native.geteuid() || (stat.Mode & 0xfff) != PrivateMode
            || Native.mkdir(jobFolder, PrivateMode) != 0)
        {
            throw new IOException("Cannot reserve a new private restore directory.");
        }
    }

    public static void CheckSource(string root)
    {
        using var directory = OpenDirectory(root);
    }

    public static FileStream OpenSource(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative is "." or ".." || relative.StartsWith("../", StringComparison.Ordinal))
        {
            throw new IOException("A restore source path is outside its destination.");
        }

        string directoryPath = Path.Combine(root, Path.GetDirectoryName(relative) ?? string.Empty);
        using var directory = OpenDirectory(directoryPath);
        using var original = Native.openat(directory, Path.GetFileName(relative), PathOnly | NoFollow | CloseOnExec);
        if (original.IsInvalid || Native.Stat(original) is not { } stat || (stat.Mode & TypeBits) != Regular)
        {
            throw new IOException("A restore source is not a regular file.");
        }

        // An O_PATH handle can inspect a FIFO or a device without opening it for I/O. Reopen only the pinned regular
        // file through procfs so a name changed concurrently cannot turn the open into a link or special-file read.
        string descriptor = original.DangerousGetHandle().ToInt64().ToString(CultureInfo.InvariantCulture);
        var readable = Native.open("/proc/self/fd/" + descriptor, CloseOnExec | NonBlocking);
        try
        {
            if (readable.IsInvalid)
            {
                throw new IOException("A restore source could not be opened.");
            }

            return new FileStream(readable, FileAccess.Read);
        }
        catch
        {
            readable.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenDirectory(string path, bool requireProtected = false)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new IOException("A restore directory must be absolute.");
        }

        var current = Native.open("/", DirectoryFlags);
        try
        {
            CheckDirectory(current, requireProtected);
            foreach (string name in Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = Native.openat(current, name, DirectoryFlags);
                current.Dispose();
                current = next;
                CheckDirectory(current, requireProtected);
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static void CheckDirectory(SafeFileHandle path, bool requireProtected)
    {
        if (path.IsInvalid || (requireProtected && (Native.Stat(path) is not { } stat
            || (stat.Uid != 0 && stat.Uid != Native.geteuid())
            || ((stat.Mode & GroupAndOtherWrite) != 0 && (stat.Mode & Sticky) == 0))))
        {
            throw new IOException("A restore directory is inaccessible, a link, or has unsafe ownership or permissions.");
        }
    }
}
