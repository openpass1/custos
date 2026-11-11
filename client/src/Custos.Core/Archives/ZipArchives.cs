using ICSharpCode.SharpZipLib.Checksum;
using ICSharpCode.SharpZipLib.Zip;

namespace Custos.Core.Archives;

/// <summary>
/// Writes ZIP archives with SharpZipLib (section 6.5): Deflate at the chosen level, AES-256 (WinZip AE-2) when there
/// is a password. The output is written strictly forward (sizes go into data descriptors after each item), so it can
/// go straight to a destination that cannot seek. Zip64 is used for every item, as sizes are not known in advance.
/// </summary>
public sealed class ZipWriter : IArchiveWriter
{
    private const int BufferSize = 1024 * 1024;

    public void Write(IReadOnlyList<ArchiveItem> items, Stream output, ArchiveWriteOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        var buffer = new byte[BufferSize];
        try
        {
            using var zip = new ZipOutputStream(new ForwardOnlyStream(output)) { IsStreamOwner = false, UseZip64 = UseZip64.Dynamic };
            zip.SetLevel(Math.Clamp(options.Level, 0, 9));
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.IsFolder)
                {
                    // Folders carry no data: never encrypted, stored.
                    zip.Password = null;
                    zip.PutNextEntry(new ZipEntry(ArchivePaths.ToZip(item.Path).TrimEnd('/') + "/")
                    {
                        CompressionMethod = CompressionMethod.Stored,
                        Size = 0,
                        Crc = 0,
                        DateTime = LocalTime(item.ModifiedUtc),
                        HostSystem = (int)HostSystemID.Msdos,
                        ExternalFileAttributes = (int)(FileAttributes.Directory | (item.Attributes & KeptAttributes)),
                        ExtraData = Times(item),
                    });
                    zip.CloseEntry();
                    continue;
                }

                using var data = item.Open?.Invoke();
                if (data is null)
                {
                    continue;
                }

                zip.Password = options.Password;
                var entry = new ZipEntry(ArchivePaths.ToZip(item.Path))
                {
                    CompressionMethod = CompressionMethod.Deflated,
                    DateTime = LocalTime(item.ModifiedUtc),
                    HostSystem = (int)HostSystemID.Msdos,
                    ExternalFileAttributes = (int)(item.Attributes & KeptAttributes),
                    ExtraData = Times(item),
                };
                if (options.Password is not null)
                {
                    entry.AESKeySize = 256;
                }

                zip.PutNextEntry(entry);
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = data.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        break;
                    }

                    zip.Write(buffer, 0, read);
                }

                zip.CloseEntry();
            }

            zip.Finish();
        }
        catch (ZipException ex)
        {
            throw new ArchiveException(Messages.Format(Messages.ZipWriteFailed, ex.Message), ex);
        }
    }

    internal const FileAttributes KeptAttributes = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive;

    private static DateTime LocalTime(DateTime? utc) =>
        utc is { } time && time.Year >= 1980 ? DateTime.SpecifyKind(time, DateTimeKind.Utc).ToLocalTime() : new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);

    /// <summary>The NTFS extra field: exact UTC times (the DOS time of ZIP is local and has 2-second steps).</summary>
    private static byte[]? Times(ArchiveItem item)
    {
        if (item.ModifiedUtc is not { } modified || !NTTaggedData.IsValidValue(modified))
        {
            return null;
        }

        var created = item.CreatedUtc is { } c && NTTaggedData.IsValidValue(c) ? c : modified;
        var extra = new ZipExtraData();
        extra.AddEntry(new NTTaggedData
        {
            LastModificationTime = DateTime.SpecifyKind(modified, DateTimeKind.Utc),
            CreateTime = DateTime.SpecifyKind(created, DateTimeKind.Utc),
            LastAccessTime = DateTime.SpecifyKind(modified, DateTimeKind.Utc),
        });
        return extra.GetEntryData();
    }
}

/// <summary>Reads ZIP archives with SharpZipLib, checking the CRC (or for AES the authentication code) of every item.</summary>
public sealed class ZipReader : IArchiveReader
{
    private const int BufferSize = 1024 * 1024;

    private readonly ZipFile _zip;

    private ZipReader(ZipFile zip, List<ArchiveEntry> entries)
    {
        _zip = zip;
        Entries = entries;
    }

    public IReadOnlyList<ArchiveEntry> Entries { get; }

    /// <summary>Opens an archive; the stream must be able to seek and is disposed with the reader (or at once if the
    /// archive cannot be opened).</summary>
    /// <exception cref="ArchiveException">Not a ZIP archive or damaged.</exception>
    public static ZipReader Open(Stream stream, string? password)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ZipFile? zip = null;
        bool done = false;
        try
        {
            zip = new ZipFile(stream, leaveOpen: false) { Password = password };
            var entries = new List<ArchiveEntry>();
            for (int i = 0; i < zip.Count; i++)
            {
                var entry = zip[i];
                entries.Add(new ArchiveEntry
                {
                    Index = i,
                    Path = ArchivePaths.ToWindows(entry.Name).TrimEnd('\\'),
                    IsFolder = entry.IsDirectory,
                    Size = entry.Size,
                    ModifiedUtc = ModifiedUtc(entry),
                    Encrypted = entry.IsCrypted,
                });
            }

            var reader = new ZipReader(zip, entries);
            done = true;
            return reader;
        }
        catch (Exception ex) when (ex is ZipException or InvalidDataException or EndOfStreamException)
        {
            throw new ArchiveException(Messages.Format(Messages.ArchiveUnreadable, ex.Message), ex);
        }
        finally
        {
            if (!done)
            {
                if (zip is not null)
                {
                    ((IDisposable)zip).Dispose();
                }
                else
                {
                    stream.Dispose();
                }
            }
        }
    }

    public void Extract(IReadOnlyCollection<int> indexes, Func<ArchiveEntry, Stream?> target, Action<ArchiveEntry, ExtractOutcome> done, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(done);
        var buffer = new byte[BufferSize];
        foreach (int index in indexes.Distinct().Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = Entries[index];
            if (entry.IsFolder)
            {
                done(entry, ExtractOutcome.Ok);
                continue;
            }

            var zipEntry = _zip[index];
            ExtractOutcome outcome;
            try
            {
                var output = target(entry) ?? Stream.Null;
                using var input = _zip.GetInputStream(zipEntry);
                var crc = new Crc32();
                long total = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = input.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        break;
                    }

                    crc.Update(new ArraySegment<byte>(buffer, 0, read));
                    output.Write(buffer, 0, read);
                    total += read;
                }

                // AE-2 items carry no CRC: their authentication code was checked at the end of the data.
                bool crcOk = zipEntry.AESKeySize > 0 || !zipEntry.HasCrc || (uint)crc.Value == (uint)zipEntry.Crc;
                outcome = crcOk && total == zipEntry.Size ? ExtractOutcome.Ok : ExtractOutcome.Damaged;
            }
            catch (ZipException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                outcome = ExtractOutcome.WrongPassword;
            }
            catch (Exception ex) when (ex is ZipException or InvalidDataException or EndOfStreamException or ICSharpCode.SharpZipLib.SharpZipBaseException)
            {
                outcome = ExtractOutcome.Damaged;
            }

            done(entry, outcome);
        }
    }

    public void Dispose() => ((IDisposable)_zip).Dispose();

    private static DateTime? ModifiedUtc(ZipEntry entry)
    {
        if (entry.ExtraData is { Length: > 0 } data)
        {
            var extra = new ZipExtraData(data);
            if (extra.GetData<NTTaggedData>() is { } nt && nt.LastModificationTime.Year > 1601)
            {
                return DateTime.SpecifyKind(nt.LastModificationTime, DateTimeKind.Utc);
            }
        }

        return entry.DateTime.ToUniversalTime();
    }
}

/// <summary>Hides the ability to seek, so that SharpZipLib writes strictly forward, as it will have to on FTP or S3.</summary>
internal sealed class ForwardOnlyStream(Stream inner) : Stream
{
    private long _position;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        _position += count;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        _position += buffer.Length;
    }
}
