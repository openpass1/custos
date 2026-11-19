using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;

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
[GeneratedComClass]
internal sealed unsafe partial class InStreamAdapter(Stream stream, CallbackErrors errors, CancellationToken cancellationToken) : ISequentialInStream, IInStream
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
[GeneratedComClass]
internal sealed unsafe partial class OutStreamAdapter(Stream stream, CallbackErrors errors, CancellationToken cancellationToken) : ISequentialOutStream, IOutStream
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

/// <summary>
/// PROPVARIANT helpers. On Windows strings are BSTRs from OLE Automation, which 7z.dll frees with SysFreeString.
/// The 7z.so of other systems has its own BSTR (CPP/Common/MyWindows.cpp): wchar_t there is UTF-32, the length in
/// bytes (UInt32) stands before the characters, and the memory comes from malloc; <see cref="NativeText"/> makes them
/// the same way.
/// </summary>
internal static unsafe partial class Props
{
    /// <summary>The size of a PROPVARIANT in an array: 24 bytes on 64-bit Windows, 16 in 7-Zip of other systems.</summary>
    public static int Stride => OperatingSystem.IsWindows() ? 24 : 16;

    public static void SetString(IntPtr target, string value)
    {
        var p = (PropVariant*)target;
        p->Pointer = NativeText.AllocBstr(value);
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

    /// <summary>Reads a value 7-Zip put into a PROPVARIANT and frees it.</summary>
    public static object? Take(PropVariant* value)
    {
        try
        {
            return value->VarType switch
            {
                PropVariant.VtBstr => value->Pointer == IntPtr.Zero ? string.Empty : NativeText.ReadBstr(value->Pointer),
                PropVariant.VtBool => value->BoolValue != 0,
                PropVariant.VtUi4 => (ulong)value->UInt32Value,
                PropVariant.VtUi8 => (ulong)value->Int64Value,
                PropVariant.VtFiletime => DateTime.FromFileTimeUtc(value->Int64Value),
                _ => null,
            };
        }
        finally
        {
            Clear(value);
        }
    }

    /// <summary>Frees what a PROPVARIANT holds and empties it.</summary>
    public static void Clear(PropVariant* value)
    {
        if (OperatingSystem.IsWindows())
        {
            _ = PropVariantClear(value);
            return;
        }

        if (value->VarType == PropVariant.VtBstr)
        {
            NativeText.FreeBstr(value->Pointer);
        }

        value->VarType = PropVariant.VtEmpty;
        value->Int64Value = 0;
    }

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(PropVariant* value);
}

/// <summary>
/// Strings for 7-Zip: <c>wchar_t</c> is UTF-16 on Windows and UTF-32 on macOS and Linux, and BSTRs are made the way
/// the 7-Zip library frees them (see <see cref="Props"/>).
/// </summary>
internal static unsafe class NativeText
{
    public static IntPtr AllocBstr(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (OperatingSystem.IsWindows())
        {
            return Marshal.StringToBSTR(value);
        }

        int length = Encoding.UTF32.GetByteCount(value);
        byte* block = (byte*)NativeMemory.Alloc((nuint)(sizeof(uint) + length + sizeof(uint)));
        *(uint*)block = (uint)length;
        Encoding.UTF32.GetBytes(value, new Span<byte>(block + sizeof(uint), length));
        *(uint*)(block + sizeof(uint) + length) = 0;
        return (IntPtr)(block + sizeof(uint));
    }

    public static void FreeBstr(IntPtr bstr)
    {
        if (bstr == IntPtr.Zero)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Marshal.FreeBSTR(bstr);
            return;
        }

        NativeMemory.Free((byte*)bstr - sizeof(uint));
    }

    public static string ReadBstr(IntPtr bstr)
    {
        if (OperatingSystem.IsWindows())
        {
            return Marshal.PtrToStringBSTR(bstr);
        }

        uint length = *((uint*)bstr - 1);
        return Encoding.UTF32.GetString((byte*)bstr, (int)length);
    }

    /// <summary>A zero-terminated <c>wchar_t</c> string; free it with <see cref="FreeWide"/>.</summary>
    public static IntPtr AllocWide(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (OperatingSystem.IsWindows())
        {
            return Marshal.StringToHGlobalUni(value);
        }

        int length = Encoding.UTF32.GetByteCount(value);
        byte* text = (byte*)NativeMemory.Alloc((nuint)(length + sizeof(uint)));
        Encoding.UTF32.GetBytes(value, new Span<byte>(text, length));
        *(uint*)(text + length) = 0;
        return (IntPtr)text;
    }

    public static void FreeWide(IntPtr text)
    {
        if (text == IntPtr.Zero)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Marshal.FreeHGlobal(text);
        }
        else
        {
            NativeMemory.Free((void*)text);
        }
    }
}
