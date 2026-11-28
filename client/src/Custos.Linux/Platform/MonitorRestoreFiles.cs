using Custos.Core;
using Microsoft.Win32.SafeHandles;

namespace Custos.Service.Platform;

/// <summary>Confines files read and written by Linux restores requested by the monitoring server.</summary>
internal static class MonitorRestoreFiles
{
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
        using var original = Native.openat(directory, Path.GetFileName(relative), Native.PathOnly | Native.NoFollow | Native.CloseOnExec);
        return Native.OpenRegularFile(original, 4096);
    }

    private static SafeFileHandle OpenDirectory(string path, bool requireProtected = false)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new IOException("A restore directory must be absolute.");
        }

        var current = Native.open("/", Native.DirectoryPathFlags);
        try
        {
            CheckDirectory(current, requireProtected);
            foreach (string name in Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = Native.openat(current, name, Native.DirectoryPathFlags);
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
