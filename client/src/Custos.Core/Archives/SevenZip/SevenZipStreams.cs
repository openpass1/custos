using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Custos.Core.Archives.SevenZip;

/// <summary>
/// Remembers the first exception thrown inside a callback from 7z.dll. Exceptions must not cross into native code:
/// the callback returns an error code instead, 7-Zip stops, and the exception is thrown again on our side.
/// </summary>
internal sealed class CallbackErrors
{
    private ExceptionDispatchInfo? _first;

    public bool HasError => _first is not null;

    public int Record(Exception ex)
    {
        _first ??= ExceptionDispatchInfo.Capture(ex);
        return ex is OperationCanceledException ? SevenZipConst.E_ABORT : SevenZipConst.E_FAIL;
    }

    /// <summary>Throws the remembered exception, if any.</summary>
    public void ThrowIfAny() => _first?.Throw();
}

/// <summary>A .NET stream as 7-Zip input (the data of one item, or the archive being read).</summary>
[SupportedOSPlatform("windows")]
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed unsafe class InStreamAdapter(Stream stream, CallbackErrors errors, CancellationToken cancellationToken) : ISequentialInStream, IInStream
{
    public Stream Stream { get; } = stream;

    public int Read(IntPtr data, uint size, IntPtr processedSize)
    {
        if (processedSize != IntPtr.Zero)
        {
            *(uint*)processedSize = 0;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = size == 0 ? 0 : Stream.Read(new Span<byte>((void*)data, (int)Math.Min(size, int.MaxValue)));
            if (processedSize != IntPtr.Zero)
            {
                *(uint*)processedSize = (uint)read;
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

    public int Seek(long offset, uint seekOrigin, IntPtr newPosition)
    {
        try
        {
            long position = Stream.Seek(offset, Origin(seekOrigin));
            if (newPosition != IntPtr.Zero)
            {
                *(ulong*)newPosition = (ulong)position;
            }

            return SevenZipConst.S_OK;
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return errors.Record(ex);
        }
    }

    internal static SeekOrigin Origin(uint seekOrigin) => seekOrigin switch
    {
        SevenZipConst.SeekSet => SeekOrigin.Begin,
        SevenZipConst.SeekCur => SeekOrigin.Current,
        SevenZipConst.SeekEnd => SeekOrigin.End,
        _ => throw new ArgumentOutOfRangeException(nameof(seekOrigin)),
    };
}

/// <summary>A .NET stream as 7-Zip output: the archive being written (seekable) or an item being unpacked.</summary>
[SupportedOSPlatform("windows")]
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed unsafe class OutStreamAdapter(Stream stream, CallbackErrors errors, CancellationToken cancellationToken) : ISequentialOutStream, IOutStream
{
    public int Write(IntPtr data, uint size, IntPtr processedSize)
    {
        if (processedSize != IntPtr.Zero)
        {
            *(uint*)processedSize = 0;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (size > 0)
            {
                stream.Write(new ReadOnlySpan<byte>((void*)data, (int)Math.Min(size, int.MaxValue)));
            }

            if (processedSize != IntPtr.Zero)
            {
                *(uint*)processedSize = size;
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

    public int Seek(long offset, uint seekOrigin, IntPtr newPosition)
    {
        try
        {
            long position = stream.Seek(offset, InStreamAdapter.Origin(seekOrigin));
            if (newPosition != IntPtr.Zero)
            {
                *(ulong*)newPosition = (ulong)position;
            }

            return SevenZipConst.S_OK;
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return errors.Record(ex);
        }
    }

    public int SetSize(ulong newSize)
    {
        try
        {
            stream.SetLength((long)newSize);
            return SevenZipConst.S_OK;
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return errors.Record(ex);
        }
    }
}

/// <summary>PROPVARIANT helpers. Strings are BSTRs from OLE Automation, which 7z.dll frees with SysFreeString.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class Props
{
    public static void SetString(IntPtr target, string value)
    {
        var p = (PropVariant*)target;
        p->Pointer = Marshal.StringToBSTR(value);
        p->VarType = PropVariant.VtBstr;
    }

    public static void SetBool(IntPtr target, bool value)
    {
        var p = (PropVariant*)target;
        p->Int64Value = 0;
        p->BoolValue = value ? (short)-1 : (short)0;
        p->VarType = PropVariant.VtBool;
    }

    public static void SetUInt32(IntPtr target, uint value)
    {
        var p = (PropVariant*)target;
        p->Int64Value = 0;
        p->UInt32Value = value;
        p->VarType = PropVariant.VtUi4;
    }

    public static void SetUInt64(IntPtr target, ulong value)
    {
        var p = (PropVariant*)target;
        p->Int64Value = (long)value;
        p->VarType = PropVariant.VtUi8;
    }

    public static void SetTime(IntPtr target, DateTime? utc)
    {
        if (utc is not { } time || time.Year < 1601)
        {
            return;
        }

        var p = (PropVariant*)target;
        p->Int64Value = time.ToFileTimeUtc();
        p->VarType = PropVariant.VtFiletime;
    }

    /// <summary>Reads a value 7z.dll put into a PROPVARIANT and frees it.</summary>
    public static object? Take(PropVariant* value)
    {
        try
        {
            return value->VarType switch
            {
                PropVariant.VtBstr => value->Pointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringBSTR(value->Pointer),
                PropVariant.VtBool => value->BoolValue != 0,
                PropVariant.VtUi4 => (ulong)value->UInt32Value,
                PropVariant.VtUi8 => (ulong)value->Int64Value,
                PropVariant.VtFiletime => DateTime.FromFileTimeUtc(value->Int64Value),
                _ => null,
            };
        }
        finally
        {
            _ = PropVariantClear(value);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(PropVariant* value);
}
