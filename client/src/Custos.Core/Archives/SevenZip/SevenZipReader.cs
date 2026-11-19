using System.Runtime.InteropServices.Marshalling;

namespace Custos.Core.Archives.SevenZip;

/// <summary>Reads a 7z archive through 7z.dll: the list of items and their data, with the CRC of every item checked.</summary>
public sealed unsafe partial class SevenZipReader : IArchiveReader
{
    private readonly IInArchive _archive;
    private readonly CallbackErrors _errors;
    private readonly InStreamAdapter _input;
    private readonly string? _password;
    private bool _disposed;

    private SevenZipReader(IInArchive archive, InStreamAdapter input, CallbackErrors errors, string? password, List<ArchiveEntry> entries)
    {
        _archive = archive;
        _errors = errors;
        _input = input;
        _password = password;
        Entries = entries;
    }

    public IReadOnlyList<ArchiveEntry> Entries { get; }

    /// <summary>Opens an archive. The stream must be able to seek; it is disposed with the reader (or at once if the
    /// archive cannot be opened).</summary>
    /// <param name="searchLimit">How far from the start to look for the archive (a self-extracting .exe); 0 = it starts
    /// right at the beginning.</param>
    /// <exception cref="ArchiveException">Not a 7z archive, damaged, or the password does not fit.</exception>
    public static SevenZipReader Open(SevenZipLibrary library, Stream stream, string? password, long searchLimit = 0)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(stream);
        var errors = new CallbackErrors();
        var input = new InStreamAdapter(stream, errors, CancellationToken.None);
        var archive = library.CreateReader();
        var callback = new OpenCallback(password);
        bool opened = false;
        try
        {
            ulong limit = (ulong)Math.Max(0, searchLimit);
            int hr = archive.Open(input, searchLimit > 0 ? (IntPtr)(&limit) : IntPtr.Zero, callback);
            errors.ThrowIfAny();
            if (hr != SevenZipConst.S_OK)
            {
                if (callback.PasswordAsked)
                {
                    throw new ArchiveException(password is null ? Messages.ArchiveNeedsPassword : Messages.ArchiveWrongPassword) { WrongPassword = true };
                }

                throw new ArchiveException(Messages.Format(Messages.ArchiveUnreadable, SevenZipLibrary.Hresult(hr)));
            }

            var entries = ReadEntries(archive);
            opened = true;
            return new SevenZipReader(archive, input, errors, password, entries);
        }
        finally
        {
            if (!opened)
            {
                _ = archive.Close();
                SevenZipLibrary.Release(archive);
                stream.Dispose();
            }
        }
    }

    public void Extract(IReadOnlyCollection<int> indexes, Func<ArchiveEntry, Stream?> target, Action<ArchiveEntry, ExtractOutcome> done, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(done);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (indexes.Count == 0)
        {
            return;
        }

        uint[] sorted = [.. indexes.Distinct().Order().Select(i => (uint)i)];
        var errors = _errors;
        var callback = new ExtractCallback(Entries, target, done, _password, errors, cancellationToken);
        fixed (uint* pointer = sorted)
        {
            int hr = _archive.Extract((IntPtr)pointer, (uint)sorted.Length, 0, callback);
            callback.Finish();
            errors.ThrowIfAny();
            if (hr == SevenZipConst.E_ABORT)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (hr != SevenZipConst.S_OK)
            {
                if (callback.PasswordMissing)
                {
                    throw new ArchiveException(Messages.ArchiveNeedsPassword) { WrongPassword = true };
                }

                throw new ArchiveException(Messages.Format(Messages.ArchiveUnreadable, SevenZipLibrary.Hresult(hr)));
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = _archive.Close();
        SevenZipLibrary.Release(_archive);
        _input.Stream.Dispose();
    }

    private static List<ArchiveEntry> ReadEntries(IInArchive archive)
    {
        if (archive.GetNumberOfItems(out uint count) != SevenZipConst.S_OK)
        {
            throw new ArchiveException(Messages.Format(Messages.ArchiveUnreadable, "GetNumberOfItems"));
        }

        var entries = new List<ArchiveEntry>((int)Math.Min(count, 1_000_000));
        for (uint i = 0; i < count; i++)
        {
            entries.Add(new ArchiveEntry
            {
                Index = (int)i,
                Path = WindowsSeparators(Get(archive, i, SevenZipConst.KpidPath) as string ?? string.Empty),
                IsFolder = Get(archive, i, SevenZipConst.KpidIsDir) is true,
                Size = Get(archive, i, SevenZipConst.KpidSize) is ulong size ? (long)size : 0,
                ModifiedUtc = Get(archive, i, SevenZipConst.KpidMTime) as DateTime?,
                Encrypted = Get(archive, i, SevenZipConst.KpidEncrypted) is true,
                Method = Get(archive, i, SevenZipConst.KpidMethod) as string,
            });
        }

        return entries;
    }

    /// <summary>Paths of entries use "\" everywhere, as from 7z.dll on Windows; 7z.so gives "/".</summary>
    private static string WindowsSeparators(string path) => OperatingSystem.IsWindows() ? path : path.Replace('/', '\\');

    private static object? Get(IInArchive archive, uint index, uint propId)
    {
        PropVariant value = default;
        return archive.GetProperty(index, propId, (IntPtr)(&value)) == SevenZipConst.S_OK ? Props.Take(&value) : null;
    }

    /// <summary>Gives 7-Zip the password when the list of names is encrypted.</summary>
    [GeneratedComClass]
    private sealed partial class OpenCallback(string? password) : IArchiveOpenCallback, ICryptoGetTextPassword
    {
        public bool PasswordAsked { get; private set; }

        public int SetTotal(IntPtr files, IntPtr bytes) => SevenZipConst.S_OK;

        public int SetCompleted(IntPtr files, IntPtr bytes) => SevenZipConst.S_OK;

        public int CryptoGetTextPassword(IntPtr passwordOut)
        {
            PasswordAsked = true;
            if (password is null)
            {
                *(IntPtr*)passwordOut = IntPtr.Zero;
                return SevenZipConst.E_ABORT;
            }

            *(IntPtr*)passwordOut = NativeText.AllocBstr(password);
            return SevenZipConst.S_OK;
        }
    }

    /// <summary>Hands 7-Zip a stream for each requested item and collects the results.</summary>
    [GeneratedComClass]
    private sealed partial class ExtractCallback(
        IReadOnlyList<ArchiveEntry> entries,
        Func<ArchiveEntry, Stream?> target,
        Action<ArchiveEntry, ExtractOutcome> done,
        string? password,
        CallbackErrors errors,
        CancellationToken cancellationToken) : IArchiveExtractCallback, ICryptoGetTextPassword
    {
        private ArchiveEntry? _current;

        public bool PasswordMissing { get; private set; }

        public int SetTotal(ulong total) => SevenZipConst.S_OK;

        public int SetCompleted(IntPtr completeValue) => cancellationToken.IsCancellationRequested ? SevenZipConst.E_ABORT : SevenZipConst.S_OK;

        public int GetStream(uint index, out ISequentialOutStream? outStream, int askExtractMode)
        {
            outStream = null;
            _current = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (askExtractMode != SevenZipConst.AskExtract || index >= entries.Count)
                {
                    return SevenZipConst.S_OK;
                }

                _current = entries[(int)index];
                if (_current.IsFolder)
                {
                    return SevenZipConst.S_OK;
                }

                // A null target still gets a sink: then 7-Zip reads the data and checks its CRC.
                outStream = new OutStreamAdapter(target(_current) ?? Stream.Null, errors, cancellationToken);
                return SevenZipConst.S_OK;
            }
#pragma warning disable CA1031 // Nothing may escape into 7z.dll; the error is thrown again after it returns.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return errors.Record(ex);
            }
        }

        public int PrepareOperation(int askExtractMode) => SevenZipConst.S_OK;

        public int SetOperationResult(int operationResult)
        {
            var entry = _current;
            _current = null;
            if (entry is null)
            {
                return SevenZipConst.S_OK;
            }

            try
            {
                done(entry, operationResult switch
                {
                    SevenZipConst.ResultOk => ExtractOutcome.Ok,
                    SevenZipConst.ResultWrongPassword => ExtractOutcome.WrongPassword,
                    SevenZipConst.ResultUnsupportedMethod => ExtractOutcome.Unsupported,
                    SevenZipConst.ResultUnexpectedEnd or SevenZipConst.ResultUnavailable => ExtractOutcome.Truncated,
                    _ => ExtractOutcome.Damaged,
                });
                return SevenZipConst.S_OK;
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return errors.Record(ex);
            }
        }

        public int CryptoGetTextPassword(IntPtr passwordOut)
        {
            if (password is null)
            {
                PasswordMissing = true;
                *(IntPtr*)passwordOut = IntPtr.Zero;
                return SevenZipConst.E_ABORT;
            }

            *(IntPtr*)passwordOut = NativeText.AllocBstr(password);
            return SevenZipConst.S_OK;
        }

        public void Finish() => _current = null;
    }
}
