namespace Custos.Core.Destinations;

/// <summary>A file or folder found in a destination.</summary>
/// <param name="Path">Its full path in the store.</param>
/// <param name="ModifiedUtc">When the store says it was written; <see cref="DateTime.MinValue"/> when it does not say.</param>
public readonly record struct StoreEntry(string Path, string Name, bool IsFolder, long Size, DateTime ModifiedUtc);

/// <summary>How much a destination holds and how much is free; null where the server does not tell.</summary>
public readonly record struct StoreSpace(long? TotalBytes, long? FreeBytes);

/// <summary>
/// The place a task writes its copies to (section 6.3): a folder of this computer, of a share or of a USB disk, or a
/// folder on an FTP, SFTP or WebDAV server or in an S3 bucket. The backup engine, retention, verification, restore and
/// the inventory only use this class, so they work the same way everywhere.
/// </summary>
/// <remarks>
/// Paths are strings in the store's own form (<c>D:\Backup\Set</c>, <c>/backup/Set</c>, <c>backup/Set</c>); they are put
/// together and taken apart only with <see cref="Combine"/>, <see cref="Parent"/>, <see cref="Name"/> and
/// <see cref="Relative"/>. Every failure of the store is an <see cref="IOException"/> or an
/// <see cref="UnauthorizedAccessException"/>; a missing file is a <see cref="FileNotFoundException"/>.
/// A store is used by one run at a time and never from two threads at once.
/// </remarks>
public abstract class DestinationStore : IDisposable
{
    /// <summary>One of <see cref="Model.DestinationKinds"/>.</summary>
    public abstract string Kind { get; }

    /// <summary>The folder of the destination.</summary>
    public abstract string Root { get; }

    /// <summary>The destination for people: its folder, or "sftp://nas.example.org/backup".</summary>
    public virtual string Label => Root;

    /// <summary>The account in use, for the log; null for the service's own account.</summary>
    public virtual string? Account => null;

    /// <summary>Tells the stores of two servers apart in caches.</summary>
    public virtual string CacheKey => Kind;

    /// <summary>A file being written can go back and change what it wrote, which 7z needs (section 6.5).</summary>
    public virtual bool CanSeek => false;

    /// <summary>A folder gets a new name in one step, so a set is written as <c>name.partial</c> and renamed when it is
    /// complete. Where this is false (S3) a set is complete when its manifest, written last, is there.</summary>
    public virtual bool RenamesFolders => true;

    /// <summary>Folders exist on their own (not in S3, where a folder is only a part of the names).</summary>
    public virtual bool HasFolders => true;

    /// <summary>Files keep the time of the original (<see cref="SetFileTimes"/>), so a mirror can compare by it.</summary>
    public virtual bool KeepsTimes => false;

    /// <summary>Files and folders can carry NTFS permissions (<see cref="WriteSecurity"/>).</summary>
    public virtual bool KeepsSecurity => false;

    /// <summary>Stops long network operations when the run is stopped.</summary>
    public CancellationToken Cancellation { get; set; }

    /// <summary>
    /// Files are written with the protection from deletion the destination asks for (S3 Object Lock, section 6.3).
    /// «Проверить доступ» turns it off for its trial file, which it deletes at once. Means something for S3 only.
    /// </summary>
    public bool LockWrites { get; set; } = true;

    /// <summary>Files are opened with low I/O priority (section 6.5); only local stores have such a thing.</summary>
    public bool LowPriority { get; set; }

    // ---- Paths -------------------------------------------------------------------------------------------------

    /// <summary><paramref name="relative"/> (with "\" or "/" between its parts) below <paramref name="folder"/>.</summary>
    public abstract string Combine(string folder, string relative);

    /// <summary>The folder a path lies in; null for the top.</summary>
    public abstract string? Parent(string path);

    /// <summary>The last part of a path.</summary>
    public abstract string Name(string path);

    /// <summary><paramref name="path"/> as seen from <paramref name="folder"/> above it.</summary>
    public abstract string Relative(string folder, string path);

    /// <summary>Combines several parts.</summary>
    public string Combine(string folder, string first, string second) => Combine(Combine(folder, first), second);

    // ---- Folders -----------------------------------------------------------------------------------------------

    /// <summary>Creates a folder with the folders above it; nothing happens if it exists.</summary>
    public abstract void CreateFolder(string path);

    public abstract bool FolderExists(string path);

    public abstract bool FileExists(string path);

    /// <summary>What lies directly in a folder; empty if there is no such folder.</summary>
    public abstract List<StoreEntry> List(string folder);

    /// <summary>Every file below a folder, however deep; <paramref name="skipFolder"/> leaves folders out by their path.</summary>
    public virtual IEnumerable<StoreEntry> ListFiles(string folder, Func<string, bool>? skipFolder = null)
    {
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            Cancellation.ThrowIfCancellationRequested();
            foreach (var entry in List(pending.Pop()))
            {
                if (!entry.IsFolder)
                {
                    yield return entry;
                }
                else if (skipFolder is null || !skipFolder(entry.Path))
                {
                    pending.Push(entry.Path);
                }
            }
        }
    }

    /// <summary>Deletes a folder with everything in it; nothing happens if it is not there. Links inside are removed,
    /// never followed.</summary>
    public abstract void DeleteFolder(string path);

    /// <summary>Deletes a folder that is empty; returns false if it is not.</summary>
    public abstract bool RemoveEmptyFolder(string path);

    /// <summary>Gives a folder another name. Only where <see cref="RenamesFolders"/>.</summary>
    public abstract void MoveFolder(string source, string target);

    // ---- Files -------------------------------------------------------------------------------------------------

    /// <summary>Opens a file for reading; the stream can seek.</summary>
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    public abstract Stream OpenRead(string path);

    /// <summary>The size of a file.</summary>
    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    public virtual long GetFileSize(string path)
    {
        using var stream = OpenRead(path);
        return stream.Length;
    }

    /// <summary>
    /// Starts writing a file. It appears under its name only with <see cref="StoreWriter.Commit"/>: until then it is
    /// kept as <c>name + tempSuffix</c>, or not shown at all (an S3 upload in parts).
    /// </summary>
    /// <param name="mode">How the caller writes; <see cref="StoreWriteMode.Seekable"/> only where <see cref="CanSeek"/>.</param>
    public abstract StoreWriter BeginWrite(string path, string tempSuffix, StoreWriteMode mode = StoreWriteMode.Blocks);

    /// <summary>Deletes a file, read-only or not; nothing happens if it is not there.</summary>
    public abstract void DeleteFile(string path);

    /// <summary>Gives a file another name or place, replacing a file that is there.</summary>
    /// <exception cref="ReplaceDeniedException">A folder of this computer or a share: the file that is there may not
    /// be replaced (the other stores report that as any other failure).</exception>
    public abstract void MoveFile(string source, string target);

    /// <summary>Gives a copied file the times and attributes of its original, where the store keeps them.</summary>
    public virtual void SetFileTimes(string path, DateTime createdUtc, DateTime modifiedUtc, FileAttributes attributes)
    {
    }

    /// <summary>Gives a file or folder NTFS permissions (section 6.8).</summary>
    /// <returns>False if there is nothing to keep permissions in.</returns>
    /// <exception cref="Backup.BackupException">They could not be set.</exception>
    public virtual bool WriteSecurity(string path, bool folder, byte[] descriptor) => false;

    // ---- The destination as a whole ----------------------------------------------------------------------------

    /// <summary>Total and free space where the folder lies, as far as the server tells.</summary>
    public virtual StoreSpace GetSpace(string path) => default;

    /// <summary>Until when the store refuses to delete a file (S3 Object Lock, section 6.3); null if it does not.</summary>
    public virtual DateTimeOffset? LockedUntil(string path) => null;

    /// <summary>What must hold before a run writes here and only the server can tell (an S3 bucket has Object Lock
    /// when the destination asks for it).</summary>
    /// <exception cref="StoreException">It does not hold.</exception>
    public virtual void CheckReady()
    {
    }

    /// <summary>Facts about the destination for «Проверить доступ», in words; empty for most kinds.</summary>
    public virtual IReadOnlyList<string> Describe() => [];

    /// <summary>
    /// Tries what a run does here («Проверить доступ»): makes the folder, writes a small file, gives it its name and
    /// deletes it. Nothing is left behind, and the trial file is not protected from deletion.
    /// </summary>
    public virtual void Probe()
    {
        bool locks = LockWrites;
        LockWrites = false;
        try
        {
            CreateFolder(Root);
            string path = Combine(Root, ProbeName());
            using (var writer = BeginWrite(path, Backup.BackupEngine.TempSuffix, StoreWriteMode.Stream))
            {
                writer.Stream.Write("CUSTOS"u8);
                writer.Commit();
            }

            DeleteFile(path);
        }
        finally
        {
            LockWrites = locks;
        }
    }

    /// <summary>A name for the trial file of <see cref="Probe"/>.</summary>
    protected static string ProbeName() => ".custos-test-" + Guid.NewGuid().ToString("N")[..8] + ".tmp";

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}

/// <summary>How a file of a destination is written.</summary>
public enum StoreWriteMode
{
    /// <summary>Strictly forward, in large blocks (a copied file): no buffer of its own is needed.</summary>
    Blocks,

    /// <summary>Strictly forward, in pieces of any size (a ZIP archive).</summary>
    Stream,

    /// <summary>In pieces of any size, going back to change what was written (a 7z archive).</summary>
    Seekable,
}

/// <summary>A file of a destination being written (see <see cref="DestinationStore.BeginWrite"/>).</summary>
public abstract class StoreWriter : IDisposable
{
    private bool _finished;

    /// <summary>Where the data goes.</summary>
    public abstract Stream Stream { get; }

    /// <summary>Where the data can be read between <see cref="Close"/> and <see cref="Commit"/>, to verify it.</summary>
    public abstract string ReadPath { get; }

    /// <summary>The file has its name already after <see cref="Close"/> (no temporary name: S3).</summary>
    public virtual bool VisibleWhenClosed => false;

    /// <summary>The file is on the disk, not in the cache of the system, when <see cref="Close"/> returns (a manifest).
    /// Means something for local stores only.</summary>
    public bool Durable { get; set; }

    /// <summary>Ends the writing: everything is in the destination. May be called more than once.</summary>
    public abstract void Close();

    /// <summary>Opens the closed file again to change what was written (volumes of a 7z archive).</summary>
    public virtual Stream Reopen() => throw new NotSupportedException();

    /// <summary>Closes the file and gives it its name.</summary>
    public void Commit()
    {
        Close();
        CommitCore();
        _finished = true;
    }

    /// <summary>Closes the file and removes what was written. Never throws: an unfinished file does not look like a
    /// copy anyway.</summary>
    public void Abort()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        try
        {
            AbortCore();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or NotSupportedException or InvalidOperationException)
        {
            // The destination is gone.
        }
    }

    public void Dispose()
    {
        Abort();
        GC.SuppressFinalize(this);
    }

    protected abstract void CommitCore();

    protected abstract void AbortCore();
}

/// <summary>A remote destination failed; the message is for the run log.</summary>
public class StoreException : IOException
{
    public StoreException()
    {
    }

    public StoreException(string message)
        : base(message)
    {
    }

    public StoreException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The destination holds a file under the name a new copy is to get and does not let it be replaced: a NAS that
/// protects written files from change (WORM), a file of another account, a file someone has open there (section 6.4).
/// A problem of that one file, not of the destination.
/// </summary>
public sealed class ReplaceDeniedException : IOException
{
    public ReplaceDeniedException()
    {
    }

    public ReplaceDeniedException(string message)
        : base(message)
    {
    }

    public ReplaceDeniedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public ReplaceDeniedException(string message, string path, Exception? innerException)
        : base(message, innerException)
    {
        Path = path;
    }

    /// <summary>The file that stays as it was.</summary>
    public string Path { get; } = string.Empty;
}

/// <summary>
/// The server is not the one the destination trusts: its SSH key or its TLS certificate is unknown or has changed.
/// «Проверить доступ» shows <see cref="Seen"/> and lets the administrator trust it.
/// </summary>
public sealed class ServerTrustException : StoreException
{
    public ServerTrustException()
    {
    }

    public ServerTrustException(string message)
        : base(message)
    {
    }

    public ServerTrustException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public ServerTrustException(string message, string? expected, string seen)
        : base(message)
    {
        Expected = expected;
        Seen = seen;
    }

    /// <summary>The fingerprint the destination trusts; null when none is stored.</summary>
    public string? Expected { get; }

    /// <summary>The fingerprint the server showed.</summary>
    public string Seen { get; } = string.Empty;
}

/// <summary>
/// The usual way to write a file: under a temporary name next to it, renamed when complete.
/// </summary>
public abstract class RenamingWriter(DestinationStore store, string path, string tempSuffix) : StoreWriter
{
    private bool _closed;

    protected DestinationStore Store { get; } = store;

    /// <summary>The name the file gets in the end.</summary>
    public string FinalPath { get; } = path;

    /// <summary>The name it is written under.</summary>
    public string TempPath { get; } = path + tempSuffix;

    public override string ReadPath => TempPath;

    public override void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        CloseCore();
    }

    /// <summary>Flushes and closes the stream; failures of the destination surface here.</summary>
    protected abstract void CloseCore();

    protected override void CommitCore() => Store.MoveFile(TempPath, FinalPath);

    protected override void AbortCore()
    {
        try
        {
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Closing a broken upload fails by its nature; the leftover is removed below.
        }

        Store.DeleteFile(TempPath);
    }
}
