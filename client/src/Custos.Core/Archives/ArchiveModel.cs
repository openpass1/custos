namespace Custos.Core.Archives;

/// <summary>One item to put into an archive: a file from disk, data from a program (unknown length) or a folder.</summary>
/// <remarks>
/// The writer asks for the data only when it gets to the item, through <see cref="Open"/>, and reads the stream to its
/// end. So data can come from anywhere: a file, the output of a database dump, a download. The items are written in
/// the order given; the last one may describe all the others (the set manifest).
/// </remarks>
public sealed class ArchiveItem
{
    /// <summary>Path inside the archive, folders separated by "\" or "/".</summary>
    public required string Path { get; init; }

    public bool IsFolder { get; init; }

    /// <summary>Size in bytes if known in advance; null when the data comes from a stream of unknown length.</summary>
    public long? Size { get; init; }

    public DateTime? ModifiedUtc { get; init; }

    public DateTime? CreatedUtc { get; init; }

    public FileAttributes Attributes { get; init; }

    /// <summary>
    /// Opens the data. Returning null leaves the item out of the archive (a file that cannot be read). Exceptions
    /// stop the whole archive. The writer disposes the stream.
    /// </summary>
    public Func<Stream?>? Open { get; init; }

    /// <summary>Keep the item apart from the others (in 7z: a solid block of its own), so that it can be read later
    /// without unpacking anything else. Used for the set manifest.</summary>
    public bool Separate { get; init; }
}

/// <summary>How to write an archive.</summary>
public sealed class ArchiveWriteOptions
{
    /// <summary>0 = store only … 9 = ultra.</summary>
    public int Level { get; init; } = 5;

    /// <summary>AES-256 password; null = no encryption.</summary>
    public string? Password { get; init; }

    /// <summary>7z only: encrypt the list of names too, so nothing can be seen without the password.</summary>
    public bool EncryptNames { get; init; }
}

/// <summary>An item found in an archive.</summary>
public sealed class ArchiveEntry
{
    /// <summary>Position in the archive, for <see cref="IArchiveReader.Extract"/>.</summary>
    public required int Index { get; init; }

    /// <summary>Path inside the archive with "\" separators.</summary>
    public required string Path { get; init; }

    public bool IsFolder { get; init; }

    public long Size { get; init; }

    public DateTime? ModifiedUtc { get; init; }

    public bool Encrypted { get; init; }
}

/// <summary>Outcome of unpacking one item.</summary>
public enum ExtractOutcome
{
    Ok,

    /// <summary>The data does not match its checksum: the archive is damaged.</summary>
    Damaged,

    /// <summary>The password does not fit.</summary>
    WrongPassword,

    /// <summary>The archive uses a method this library cannot unpack.</summary>
    Unsupported,

    /// <summary>The archive ends too early: a volume is missing or cut off.</summary>
    Truncated,
}

/// <summary>Writes the items of a set into an archive.</summary>
public interface IArchiveWriter
{
    /// <summary>Writes all items into <paramref name="output"/>. 7z needs a stream that can seek (it goes back to the
    /// start to write the header position); ZIP never seeks.</summary>
    /// <exception cref="ArchiveException">Packing failed.</exception>
    void Write(IReadOnlyList<ArchiveItem> items, Stream output, ArchiveWriteOptions options, CancellationToken cancellationToken);
}

/// <summary>An archive opened for reading.</summary>
public interface IArchiveReader : IDisposable
{
    IReadOnlyList<ArchiveEntry> Entries { get; }

    /// <summary>
    /// Unpacks the items with the given indexes, in archive order. For each one <paramref name="target"/> gives the
    /// stream to write into (null = only check it), and <paramref name="done"/> is told how it went. The target
    /// stream is not disposed by the reader.
    /// </summary>
    void Extract(IReadOnlyCollection<int> indexes, Func<ArchiveEntry, Stream?> target, Action<ArchiveEntry, ExtractOutcome> done, CancellationToken cancellationToken);
}

/// <summary>An archive cannot be written or read; the message is for the run log.</summary>
public sealed class ArchiveException : Exception
{
    public ArchiveException()
    {
    }

    public ArchiveException(string message)
        : base(message)
    {
    }

    public ArchiveException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The password did not fit (or the archive is damaged where the password is checked).</summary>
    public bool WrongPassword { get; init; }
}

/// <summary>Helpers shared by the writers and readers.</summary>
public static class ArchivePaths
{
    /// <summary>"a\b.txt" or "a/b.txt" → "a/b.txt" (the separator inside ZIP; 7-Zip converts it itself).</summary>
    public static string ToZip(string path) => path.Replace('\\', '/').TrimStart('/');

    /// <summary>Any separator → "\".</summary>
    public static string ToWindows(string path) => path.Replace('/', '\\').TrimStart('\\');

    /// <summary>
    /// Whether a path from an archive is safe to unpack below a folder: relative, without ".." or a drive.
    /// Archives are written by CUSTOS, but a damaged or foreign one must never write outside the target.
    /// </summary>
    public static bool IsSafeRelative(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string windows = ToWindows(path);
        if (windows.Length == 0 || windows.Contains(':', StringComparison.Ordinal) || System.IO.Path.IsPathRooted(windows))
        {
            return false;
        }

        foreach (string part in windows.Split('\\'))
        {
            if (part is ".." or "." || part.Length == 0)
            {
                return false;
            }
        }

        return true;
    }
}
