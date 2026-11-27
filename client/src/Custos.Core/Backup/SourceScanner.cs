using Custos.Core.Model;

namespace Custos.Core.Backup;

/// <summary>A file found in a source.</summary>
public sealed class ScannedFile
{
    public required int SourceIndex { get; init; }

    /// <summary>Path below the source folder; for a file source, the file name.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Full path as the user knows it, for example <c>D:\Data\a.txt</c>. Key of the change tracking.</summary>
    public required string OriginalPath { get; init; }

    /// <summary>Path to open: inside the shadow copy when there is one.</summary>
    public required string ReadPath { get; init; }

    public required long Size { get; init; }

    public required DateTime LastWriteUtc { get; init; }

    public required DateTime CreationUtc { get; init; }

    public required FileAttributes Attributes { get; init; }

    public FileStamp Stamp => new(Size, LastWriteUtc.Ticks);
}

/// <summary>Size and modification time: what tells whether a file changed (section 7).</summary>
public readonly record struct FileStamp(long Size, long ModifiedTicks);

/// <summary>What one source contained.</summary>
public sealed class SourceScan
{
    /// <summary>False when the source folder or file does not exist.</summary>
    public required bool Found { get; init; }

    public required bool IsFile { get; init; }

    public required List<ScannedFile> Files { get; init; }

    /// <summary>Every folder below the source folder that was scanned, relative to it.</summary>
    public required List<string> Folders { get; init; }

    /// <summary>Folders that could not be read.</summary>
    public required int UnreadableFolders { get; init; }
}

/// <summary>Walks a source folder, applying the task filters. Symbolic links and junctions to folders are not
/// followed, so a source never leads outside itself or into a loop.</summary>
public static class SourceScanner
{
    private static readonly EnumerationOptions OneLevel = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
    };

    /// <param name="source">The source as configured.</param>
    /// <param name="index">Its position in the task.</param>
    /// <param name="readRoot">Where to read it: the source path itself or its location inside a shadow copy.</param>
    /// <param name="onProblem">Called for a folder or file that cannot be read: path and reason.</param>
    /// <param name="skipFolder">Why a folder below the source is not read at all (<see cref="IBackupPlatform.SkipFolder"/>), or null.</param>
    /// <param name="onSkipped">Called for such a folder: path and reason.</param>
    /// <param name="isRegularFile">Platform check that a file can be read as ordinary data.</param>
    /// <param name="excludeFolder">Whether an original folder and its corresponding read path are excluded.</param>
    public static SourceScan Scan(
        TaskSource source,
        int index,
        string readRoot,
        FileFilter filter,
        Action<string, string> onProblem,
        CancellationToken cancellationToken,
        Func<string, string?>? skipFolder = null,
        Action<string, string>? onSkipped = null,
        Func<string, bool>? isRegularFile = null,
        Func<string, string, bool>? excludeFolder = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(readRoot);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(onProblem);

        var files = new List<ScannedFile>();
        var folders = new List<string>();
        string originalRoot = TrimSeparator(source.Path);

        if (File.Exists(readRoot))
        {
            if (isRegularFile?.Invoke(readRoot) == false)
            {
                throw new BackupException(Messages.SourceNotRegular);
            }

            var info = new FileInfo(readRoot);
            files.Add(Describe(index, info.Name, originalRoot, info));
            return new SourceScan { Found = true, IsFile = true, Files = files, Folders = folders, UnreadableFolders = 0 };
        }

        if (!Directory.Exists(readRoot))
        {
            return new SourceScan { Found = false, IsFile = false, Files = files, Folders = folders, UnreadableFolders = 0 };
        }

        int unreadable = 0;
        var pending = new Stack<(string ReadPath, string Relative)>();
        pending.Push((readRoot, string.Empty));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (readDir, relativeDir) = pending.Pop();
            List<FileSystemInfo> entries;
            try
            {
                entries = [.. new DirectoryInfo(readDir).EnumerateFileSystemInfos("*", OneLevel)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                unreadable++;
                onProblem(Combine(originalRoot, relativeDir), ex.Message);
                continue;
            }

            foreach (var entry in entries)
            {
                string relative = relativeDir.Length == 0 ? entry.Name : Path.Combine(relativeDir, entry.Name);
                string original = Combine(originalRoot, relative);
                FileAttributes attributes;
                try
                {
                    attributes = entry.Attributes;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    onProblem(original, ex.Message);
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!source.Recursive || !filter.IncludesFolder(entry.Name, relative, original, attributes) || IsLink(entry, attributes))
                    {
                        continue;
                    }

                    if (excludeFolder?.Invoke(entry.FullName, original) == true)
                    {
                        continue;
                    }

                    if (skipFolder?.Invoke(entry.FullName) is { } skipped)
                    {
                        onSkipped?.Invoke(original, skipped);
                        continue;
                    }

                    folders.Add(relative);
                    pending.Push((entry.FullName, relative));
                }
                else if (filter.IncludesFile(entry.Name, relative, original, attributes))
                {
                    try
                    {
                        if (isRegularFile?.Invoke(entry.FullName) != false)
                        {
                            files.Add(Describe(index, relative, original, (FileInfo)entry));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        onProblem(original, ex.Message);
                    }
                }
            }
        }

        files.Sort((a, b) => PathNames.Comparer.Compare(a.RelativePath, b.RelativePath));
        folders.Sort(PathNames.Comparer);
        return new SourceScan { Found = true, IsFile = false, Files = files, Folders = folders, UnreadableFolders = unreadable };
    }

    /// <summary>Name the source gets inside a set when the top folder is kept: <c>D:\Data</c> → <c>Data</c>,
    /// <c>D:\</c> → <c>D</c>, <c>\\nas\share</c> → <c>share</c>.</summary>
    public static string TopFolderName(string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        string trimmed = TrimSeparator(sourcePath);
        string name = Path.GetFileName(trimmed);
        if (name.Length == 0)
        {
            // A root. Windows counts the share as part of a network root, so the last part is taken by hand:
            // "D:\" → "D", "\\nas\share" → "share".
            string[] parts = trimmed.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            name = parts.Length == 0 ? string.Empty : parts[^1].Replace(":", string.Empty, StringComparison.Ordinal);
        }

        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name.Length == 0 ? "Source" : name;
    }

    private static ScannedFile Describe(int index, string relative, string original, FileInfo info) => new()
    {
        SourceIndex = index,
        RelativePath = relative,
        OriginalPath = original,
        ReadPath = info.FullName,
        Size = info.Length,
        LastWriteUtc = info.LastWriteTimeUtc,
        CreationUtc = info.CreationTimeUtc,
        Attributes = info.Attributes,
    };

    // A junction or symbolic link. Other reparse points (deduplicated or cloud files) are ordinary folders here.
    private static bool IsLink(FileSystemInfo entry, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) == 0)
        {
            return false;
        }

        try
        {
            return entry.LinkTarget is not null;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static string Combine(string root, string relative) => relative.Length == 0 ? root : Path.Combine(root, relative);

    private static string TrimSeparator(string path)
    {
        string trimmed = path.TrimEnd('\\', '/');
        // Keep the separator of a drive root: "D:" alone means "current folder on D:".
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + Path.DirectorySeparatorChar : trimmed.Length == 0 ? path : trimmed;
    }
}
