using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Custos.Core.Archives.SevenZip;

/// <summary>
/// Writes a 7z archive through 7z.dll (section 6.5): LZMA2 at the chosen level, AES-256 with an optional encrypted
/// list of names. 7-Zip reads the items one after another through callbacks, so files never need to be copied
/// anywhere first and items of unknown length (program output) work too.
/// </summary>
/// <remarks>
/// Settings passed to 7-Zip: <c>x</c> = level; <c>f=off</c> = no executable filters, which would also read the start of
/// every .exe and .dll ahead of time; <c>tc=on</c> = keep the creation times; <c>he=on</c> = encrypt the names. 7-Zip
/// reads the files sorted by path (case-insensitive), not in the given order. A <see cref="ArchiveItem.Separate"/>
/// item is reported as huge, which puts it into a solid block of its own; the real size is counted while it is read.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SevenZipWriter(SevenZipLibrary library) : IArchiveWriter
{
    /// <summary>The size 7-Zip takes as "unknown" (stdin).</summary>
    internal const ulong UnknownSize = ulong.MaxValue;

    /// <summary>Larger than any solid block, smaller than any sum of sizes could overflow to.</summary>
    internal const ulong SeparateSize = 1UL << 62;

    public void Write(IReadOnlyList<ArchiveItem> items, Stream output, ArchiveWriteOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        if (!output.CanSeek)
        {
            throw new ArgumentException("7z needs an output stream that can seek.", nameof(output));
        }

        var errors = new CallbackErrors();
        var archive = library.CreateWriter();
        try
        {
            SetProperties(archive, options);
            var callback = new UpdateCallback(items, options.Password, errors, cancellationToken);
            var outStream = new OutStreamAdapter(output, errors, cancellationToken);
            int hr = archive.UpdateItems(outStream, (uint)items.Count, callback);
            callback.CloseCurrent();
            errors.ThrowIfAny();
            if (hr != SevenZipConst.S_OK)
            {
                throw new ArchiveException(Messages.Format(Messages.SevenZipWriteFailed, SevenZipLibrary.Hresult(hr)));
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(archive);
        }
    }

    private static unsafe void SetProperties(IOutArchive archive, ArchiveWriteOptions options)
    {
        var settings = new List<(string Name, Action<IntPtr> Set)>
        {
            ("x", p => Props.SetUInt32(p, (uint)Math.Clamp(options.Level, 0, 9))),
            ("f", p => Props.SetBool(p, false)),
            ("tc", p => Props.SetBool(p, true)),
        };
        if (options.Password is not null && options.EncryptNames)
        {
            settings.Add(("he", p => Props.SetBool(p, true)));
        }

        var setter = (ISetProperties)archive;
        var names = new IntPtr[settings.Count];
        var values = new PropVariant[settings.Count];
        try
        {
            fixed (PropVariant* valuePointer = values)
            fixed (IntPtr* namePointer = names)
            {
                for (int i = 0; i < settings.Count; i++)
                {
                    names[i] = Marshal.StringToHGlobalUni(settings[i].Name);
                    settings[i].Set((IntPtr)(valuePointer + i));
                }

                int hr = setter.SetProperties((IntPtr)namePointer, (IntPtr)valuePointer, (uint)settings.Count);
                if (hr != SevenZipConst.S_OK)
                {
                    throw new ArchiveException(Messages.Format(Messages.SevenZipWriteFailed, SevenZipLibrary.Hresult(hr)));
                }
            }
        }
        finally
        {
            foreach (var name in names)
            {
                Marshal.FreeHGlobal(name);
            }
        }
    }

    /// <summary>Answers the questions of 7-Zip about the items and hands out their data.</summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed unsafe class UpdateCallback(IReadOnlyList<ArchiveItem> items, string? password, CallbackErrors errors, CancellationToken cancellationToken)
        : IArchiveUpdateCallback, ICryptoGetTextPassword2
    {
        private InStreamAdapter? _current;

        public int SetTotal(ulong total) => SevenZipConst.S_OK;

        public int SetCompleted(IntPtr completeValue) => cancellationToken.IsCancellationRequested ? SevenZipConst.E_ABORT : SevenZipConst.S_OK;

        public int GetUpdateItemInfo(uint index, IntPtr newData, IntPtr newProps, IntPtr indexInArchive)
        {
            if (newData != IntPtr.Zero)
            {
                *(int*)newData = 1;
            }

            if (newProps != IntPtr.Zero)
            {
                *(int*)newProps = 1;
            }

            if (indexInArchive != IntPtr.Zero)
            {
                *(uint*)indexInArchive = uint.MaxValue;
            }

            return SevenZipConst.S_OK;
        }

        public int GetProperty(uint index, uint propId, IntPtr value)
        {
            try
            {
                var item = items[(int)index];
                switch (propId)
                {
                    case SevenZipConst.KpidPath:
                        Props.SetString(value, ArchivePaths.ToWindows(item.Path));
                        break;
                    case SevenZipConst.KpidIsDir:
                        Props.SetBool(value, item.IsFolder);
                        break;
                    case SevenZipConst.KpidSize:
                        Props.SetUInt64(value, item.IsFolder ? 0 : item.Separate ? SeparateSize : item.Size is { } size ? (ulong)size : UnknownSize);
                        break;
                    case SevenZipConst.KpidAttrib:
                        Props.SetUInt32(value, (uint)(item.Attributes | (item.IsFolder ? FileAttributes.Directory : 0)));
                        break;
                    case SevenZipConst.KpidMTime:
                        Props.SetTime(value, item.ModifiedUtc);
                        break;
                    case SevenZipConst.KpidCTime:
                        Props.SetTime(value, item.CreatedUtc);
                        break;
                    case SevenZipConst.KpidIsAnti:
                        Props.SetBool(value, false);
                        break;
                }

                return SevenZipConst.S_OK;
            }
#pragma warning disable CA1031 // Nothing may escape into 7z.dll; the error is thrown again after it returns.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return errors.Record(ex);
            }
        }

        public int GetStream(uint index, out ISequentialInStream? inStream)
        {
            inStream = null;
            try
            {
                CloseCurrent();
                cancellationToken.ThrowIfCancellationRequested();
                var item = items[(int)index];
                if (item.IsFolder || item.Open is null)
                {
                    return SevenZipConst.S_OK;
                }

                var stream = item.Open();
                if (stream is null)
                {
                    // Left out: 7-Zip drops an item without data.
                    return SevenZipConst.S_FALSE;
                }

                _current = new InStreamAdapter(stream, errors, cancellationToken);
                inStream = _current;
                return SevenZipConst.S_OK;
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return errors.Record(ex);
            }
        }

        public int SetOperationResult(int operationResult)
        {
            CloseCurrent();
            return SevenZipConst.S_OK;
        }

        public int CryptoGetTextPassword2(IntPtr passwordIsDefined, IntPtr passwordOut)
        {
            *(int*)passwordIsDefined = password is null ? 0 : 1;
            *(IntPtr*)passwordOut = password is null ? IntPtr.Zero : Marshal.StringToBSTR(password);
            return SevenZipConst.S_OK;
        }

        /// <summary>Disposes the stream of the item 7-Zip has finished reading.</summary>
        public void CloseCurrent()
        {
            var current = _current;
            _current = null;
            current?.Stream.Dispose();
        }
    }
}
