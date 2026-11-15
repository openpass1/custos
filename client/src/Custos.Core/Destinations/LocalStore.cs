using System.IO.Enumeration;
using Custos.Core.Backup;
using Custos.Core.Model;

namespace Custos.Core.Destinations;

/// <summary>
/// A folder of this computer, of a share or of a USB disk. Every file operation runs inside the network logon of the
/// destination (section 6.3); a file opened there stays usable afterwards.
/// </summary>
public sealed class LocalStore : DestinationStore
{
    private const int BufferSize = 1024 * 1024;

    private readonly IBackupPlatform? _platform;
    private readonly INetworkSession _session;
    private readonly bool _ownsSession;

    /// <param name="platform">Free space, permissions and I/O priority of Windows; null for plain file access.</param>
    /// <param name="session">The logon to work in; null = the account of the process.</param>
    /// <param name="ownsSession">The logon is closed with the store.</param>
    public LocalStore(string root, string kind = DestinationKinds.Local, IBackupPlatform? platform = null, INetworkSession? session = null, bool ownsSession = false)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        Kind = kind;
        _platform = platform;
        _session = session ?? ProcessSession.Instance;
        _ownsSession = ownsSession;
    }

    /// <summary>Plain access to the files of this computer under the account of the process.</summary>
    public static LocalStore Default { get; } = new(string.Empty);

    public override string Kind { get; }

    public override string Root { get; }

    public override string? Account => _session.Account;

    public override string CacheKey => "file";

    public override bool CanSeek => true;

    public override bool KeepsTimes => true;

    public override bool KeepsSecurity => true;

    /// <summary>The logon the files are opened in.</summary>
    public INetworkSession Session => _session;

    /// <summary>The limit on writing to a share (section 6.3); null = none.</summary>
    public SpeedLimiter? Limiter { get; init; }

    public override string Combine(string folder, string relative) => Path.Combine(folder, Local(relative));

    public override string? Parent(string path) => Path.GetDirectoryName(path);

    public override string Name(string path) => Path.GetFileName(path);

    public override string Relative(string folder, string path) => Path.GetRelativePath(folder, path);

    public override void CreateFolder(string path) => _session.Run(() => Directory.CreateDirectory(path));

    public override bool FolderExists(string path) => _session.Run(() => Directory.Exists(path));

    public override bool FileExists(string path) => _session.Run(() => File.Exists(path));

    public override List<StoreEntry> List(string folder) => _session.Run(() =>
    {
        var result = new List<StoreEntry>();
        var directory = new DirectoryInfo(folder);
        if (!directory.Exists)
        {
            return result;
        }

        foreach (var entry in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            // A link to a folder is a file here: it is removed, never followed.
            result.Add(entry is FileInfo file
                ? new StoreEntry(file.FullName, file.Name, false, file.Length, file.LastWriteTimeUtc)
                : new StoreEntry(entry.FullName, entry.Name, (entry.Attributes & FileAttributes.ReparsePoint) == 0, 0, entry.LastWriteTimeUtc));
        }

        return result;
    });

    public override IEnumerable<StoreEntry> ListFiles(string folder, Func<string, bool>? skipFolder = null) => _session.Run(() =>
    {
        var result = new List<StoreEntry>();
        if (!Directory.Exists(folder))
        {
            return result;
        }

        // Read at once, inside the logon: the enumeration would otherwise run outside it.
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var files = new FileSystemEnumerable<StoreEntry>(folder,
            (ref FileSystemEntry e) => new StoreEntry(e.ToFullPath(), e.FileName.ToString(), false, e.Length, e.LastWriteTimeUtc.UtcDateTime), options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
            ShouldRecursePredicate = (ref FileSystemEntry e) => skipFolder is null || !skipFolder(e.ToFullPath()),
        };
        result.AddRange(files);
        return result;
    });

    public override void DeleteFolder(string path) => _session.Run(() =>
    {
        var root = new DirectoryInfo(path);
        if (root.Exists)
        {
            DeleteTree(root);
        }
    });

    public override bool RemoveEmptyFolder(string path) => _session.Run(() =>
    {
        if (!Directory.Exists(path))
        {
            return true;
        }

        if (Directory.EnumerateFileSystemEntries(path).Any())
        {
            return false;
        }

        Directory.Delete(path);
        return true;
    });

    public override void MoveFolder(string source, string target) => _session.Run(() => Directory.Move(source, target));

    /// <summary>The answer of <see cref="CanRenameFolders"/> without trying (a test); null = try.</summary>
    internal bool? KnownFolderRenames { get; set; }

    /// <summary>
    /// Tries it: makes a folder with a small file, renames the folder and removes it again. The same steps as
    /// «Проверить доступ» plus the rename, so it leaves nothing behind where a run can write at all.
    /// </summary>
    public override bool CanRenameFolders(string folder) => KnownFolderRenames ?? _session.Run(() =>
    {
        string trial = Path.Combine(folder, ProbeName());
        string current = trial;
        Directory.CreateDirectory(trial);
        try
        {
            File.WriteAllBytes(Path.Combine(trial, "custos.tmp"), "CUSTOS"u8.ToArray());
            try
            {
                Directory.Move(trial, trial + ".renamed");
                current = trial + ".renamed";
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        finally
        {
            try
            {
                DeleteTree(new DirectoryInfo(current));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A few bytes stay behind; the run itself does not depend on them.
            }
        }
    });

    public override IReadOnlyList<string> Describe()
    {
        try
        {
            return CanRenameFolders(Root) ? [] : [Messages.StoreNoFolderRename];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public override Stream OpenRead(string path) => Lowered(_session.Run(() =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan)));

    /// <summary>Opens a file that is read here and there (an archive), not from its start to its end.</summary>
    public Stream OpenRandom(string path) => Lowered(_session.Run(() =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess)));

    public override long GetFileSize(string path) => _session.Run(() =>
    {
        var file = new FileInfo(path);
        return file.Exists ? file.Length : throw new FileNotFoundException(null, path);
    });

    public override StoreWriter BeginWrite(string path, string tempSuffix, StoreWriteMode mode = StoreWriteMode.Blocks) =>
        new Writer(this, path, tempSuffix, mode == StoreWriteMode.Blocks ? 1 : BufferSize);

    public override void DeleteFile(string path) => _session.Run(() =>
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return;
        }

        if (file.IsReadOnly)
        {
            file.IsReadOnly = false;
        }

        file.Delete();
    });

    public override void MoveFile(string source, string target) => _session.Run(() =>
    {
        FileAttributes? held = TryGetAttributes(target);
        try
        {
            // Only these keep a file from being replaced. Anything else is left alone: on a share, changing the
            // attributes of a file takes the right to write that file, replacing it only the right to write the folder,
            // so a file another account wrote (Cobian's) can often be replaced but not changed.
            if (held is { } attributes && (attributes & Unreplaceable) != 0)
            {
                File.SetAttributes(target, attributes & ~Unreplaceable);
            }

            File.Move(source, target, overwrite: true);
        }
        catch (Exception ex) when (held is not null && IsRefusal(ex))
        {
            throw new ReplaceDeniedException(ex.Message, target, ex);
        }
    });

    public override void SetFileTimes(string path, DateTime createdUtc, DateTime modifiedUtc, FileAttributes attributes) => _session.Run(() =>
    {
        File.SetCreationTimeUtc(path, createdUtc);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
        var kept = attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive);
        if (kept != 0 && kept != FileAttributes.Archive)
        {
            File.SetAttributes(path, kept);
        }
    });

    public override bool WriteSecurity(string path, bool folder, byte[] descriptor) =>
        _platform is not null && _session.Run(() => _platform.WriteSecurity(path, folder, descriptor));

    public override StoreSpace GetSpace(string path) => _session.Run(() =>
        _platform is not null ? new StoreSpace(_platform.GetTotalSpace(path), _platform.GetFreeSpace(path)) : default);

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsSession)
        {
            _session.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Attributes cleared before a file is replaced: with them Windows refuses to replace it.</summary>
    private const FileAttributes Unreplaceable = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;

    // ERROR_ACCESS_DENIED, ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION as HRESULTs.
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    /// <summary>The attributes of a file; null when there is no file (or a folder) under that name.</summary>
    private static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) == 0 ? attributes : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The server said no to this file (rights, a lock, a file open there), as opposed to being gone or full.</summary>
    private static bool IsRefusal(Exception ex) =>
        ex is UnauthorizedAccessException || (ex is IOException && ex.HResult is AccessDenied or SharingViolation or LockViolation);

    /// <summary>A relative path with any separators → the separators of this system.</summary>
    private static string Local(string relative) =>
        Path.DirectorySeparatorChar == '\\' ? relative.Replace('/', '\\') : relative.Replace('\\', '/');

    private Stream Limited(FileStream stream) => Limiter is null ? stream : new ThrottledStream(stream, Limiter, Cancellation);

    private FileStream Lowered(FileStream stream)
    {
        if (LowPriority)
        {
            _platform?.LowerIoPriority(stream.SafeFileHandle);
        }

        return stream;
    }

    private static void DeleteTree(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            if (entry is DirectoryInfo child && (entry.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                DeleteTree(child);
                continue;
            }

            if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }

            if (entry is DirectoryInfo link)
            {
                link.Delete(recursive: false);
            }
            else
            {
                entry.Delete();
            }
        }

        if ((directory.Attributes & FileAttributes.ReadOnly) != 0)
        {
            directory.Attributes &= ~FileAttributes.ReadOnly;
        }

        directory.Delete(recursive: false);
    }

    private sealed class Writer : RenamingWriter
    {
        private readonly LocalStore _store;
        private readonly FileStream _stream;

        /// <param name="bufferSize">1 when the caller hands over whole megabytes: no second buffer then.</param>
        public Writer(LocalStore store, string path, string tempSuffix, int bufferSize)
            : base(store, path, tempSuffix)
        {
            _store = store;
            _stream = store.Lowered(store._session.Run(() => new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize)));
            Stream = store.Limited(_stream);
        }

        public override Stream Stream { get; }

        public override Stream Reopen() => _store.Limited(
            _store.Lowered(_store._session.Run(() => new FileStream(TempPath, FileMode.Open, FileAccess.Write, FileShare.None, BufferSize))));

        protected override void CloseCore()
        {
            if (Durable)
            {
                _stream.Flush(flushToDisk: true);
            }

            _stream.Dispose();
        }
    }
}
