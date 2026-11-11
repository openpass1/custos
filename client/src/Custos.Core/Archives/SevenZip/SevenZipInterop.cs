using System.Runtime.InteropServices;

namespace Custos.Core.Archives.SevenZip;

// The COM-style interfaces of 7z.dll (CPP/7zip/IStream.h, IProgress.h, IPassword.h, Archive/IArchive.h of the 7-Zip
// sources). They have been stable since 7-Zip 9.20, which is why any 7z.dll an administrator drops in works.
//
// .NET COM interop does not lay out the methods of a base interface in the vtable of a derived [ComImport]
// interface, so every interface repeats the methods of its base first, in the same order as in 7-Zip.
// IIDs are {23170F69-40C1-278A-0000-00gg00ss0000}: gg = group (0 progress, 3 streams, 5 password, 6 archive),
// ss = number of the interface in the group.

[ComImport]
[Guid("23170F69-40C1-278A-0000-000000050000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IProgress
{
    [PreserveSig]
    int SetTotal(ulong total);

    [PreserveSig]
    int SetCompleted(IntPtr completeValue);
}

[ComImport]
[Guid("23170F69-40C1-278A-0000-000300010000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISequentialInStream
{
    [PreserveSig]
    int Read(IntPtr data, uint size, IntPtr processedSize);
}

[ComImport]
[Guid("23170F69-40C1-278A-0000-000300020000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISequentialOutStream
{
    [PreserveSig]
    int Write(IntPtr data, uint size, IntPtr processedSize);
}

[ComImport]
[Guid("23170F69-40C1-278A-0000-000300030000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInStream
{
    [PreserveSig]
    int Read(IntPtr data, uint size, IntPtr processedSize);

    [PreserveSig]
    int Seek(long offset, uint seekOrigin, IntPtr newPosition);
}

[ComImport]
[Guid("23170F69-40C1-278A-0000-000300040000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOutStream
{
    [PreserveSig]
    int Write(IntPtr data, uint size, IntPtr processedSize);

    [PreserveSig]
    int Seek(long offset, uint seekOrigin, IntPtr newPosition);

    [PreserveSig]
    int SetSize(ulong newSize);
}

[ComImport]
[Guid("23170F69-40C1-278A-0000-000300060000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IStreamGetSize
{
    [PreserveSig]
    int GetSize(IntPtr size);
}

[ComImport]
[Guid("23170F69-40C1-278A-0000-000500100000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICryptoGetTextPassword
{
    [PreserveSig]
    int CryptoGetTextPassword(IntPtr password);
}

[ComImport]
[Guid("23170F69-40C1-278A-0000-000500110000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICryptoGetTextPassword2
{
    [PreserveSig]
    int CryptoGetTextPassword2(IntPtr passwordIsDefined, IntPtr password);
}

[ComImport]
[Guid("23170F69-40C1-278A-0000-000600030000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISetProperties
{
    [PreserveSig]
    int SetProperties(IntPtr names, IntPtr values, uint numProps);
}

[ComImport]
[Guid("23170F69-40C1-278A-0000-000600100000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IArchiveOpenCallback
{
    [PreserveSig]
    int SetTotal(IntPtr files, IntPtr bytes);

    [PreserveSig]
    int SetCompleted(IntPtr files, IntPtr bytes);
}

[ComImport]
[Guid("23170F69-40C1-278A-0000-000600200000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IArchiveExtractCallback
{
    // IProgress
    [PreserveSig]
    int SetTotal(ulong total);

    [PreserveSig]
    int SetCompleted(IntPtr completeValue);

    [PreserveSig]
    int GetStream(uint index, out ISequentialOutStream? outStream, int askExtractMode);

    [PreserveSig]
    int PrepareOperation(int askExtractMode);

    [PreserveSig]
    int SetOperationResult(int operationResult);
}

[ComImport]
[Guid("23170F69-40C1-278A-0000-000600600000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInArchive
{
    [PreserveSig]
    int Open(IInStream stream, IntPtr maxCheckStartPosition, IArchiveOpenCallback? openCallback);

    [PreserveSig]
    int Close();

    [PreserveSig]
    int GetNumberOfItems(out uint numItems);

    [PreserveSig]
    int GetProperty(uint index, uint propId, IntPtr value);

    [PreserveSig]
    int Extract(IntPtr indices, uint numItems, int testMode, IArchiveExtractCallback extractCallback);

    [PreserveSig]
    int GetArchiveProperty(uint propId, IntPtr value);

    [PreserveSig]
    int GetNumberOfProperties(out uint numProps);

    [PreserveSig]
    int GetPropertyInfo(uint index, IntPtr name, IntPtr propId, IntPtr varType);

    [PreserveSig]
    int GetNumberOfArchiveProperties(out uint numProps);

    [PreserveSig]
    int GetArchivePropertyInfo(uint index, IntPtr name, IntPtr propId, IntPtr varType);
}

[ComImport]
[Guid("23170F69-40C1-278A-0000-000600800000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IArchiveUpdateCallback
{
    // IProgress
    [PreserveSig]
    int SetTotal(ulong total);

    [PreserveSig]
    int SetCompleted(IntPtr completeValue);

    [PreserveSig]
    int GetUpdateItemInfo(uint index, IntPtr newData, IntPtr newProps, IntPtr indexInArchive);

    [PreserveSig]
    int GetProperty(uint index, uint propId, IntPtr value);

    [PreserveSig]
    int GetStream(uint index, out ISequentialInStream? inStream);

    [PreserveSig]
    int SetOperationResult(int operationResult);
}

[ComImport]
[Guid("23170F69-40C1-278A-0000-000600A00000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOutArchive
{
    [PreserveSig]
    int UpdateItems(ISequentialOutStream outStream, uint numItems, IArchiveUpdateCallback updateCallback);

    [PreserveSig]
    int GetFileTimeType(out uint type);
}

/// <summary>Constants of the 7-Zip interfaces.</summary>
internal static class SevenZipConst
{
    /// <summary>The 7z handler: {23170F69-40C1-278A-1000-000110070000} (format id 7).</summary>
    public static readonly Guid Format7z = new("23170F69-40C1-278A-1000-000110070000");

    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_ABORT = unchecked((int)0x80004004);
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);

    // PROPID (PropID.h)
    public const uint KpidPath = 3;
    public const uint KpidIsDir = 6;
    public const uint KpidSize = 7;
    public const uint KpidAttrib = 9;
    public const uint KpidCTime = 10;
    public const uint KpidATime = 11;
    public const uint KpidMTime = 12;
    public const uint KpidEncrypted = 15;
    public const uint KpidCrc = 19;
    public const uint KpidIsAnti = 21;

    // NExtract::NAskMode
    public const int AskExtract = 0;
    public const int AskTest = 1;
    public const int AskSkip = 2;

    // NExtract::NOperationResult
    public const int ResultOk = 0;
    public const int ResultUnsupportedMethod = 1;
    public const int ResultDataError = 2;
    public const int ResultCrcError = 3;
    public const int ResultUnavailable = 4;
    public const int ResultUnexpectedEnd = 5;
    public const int ResultDataAfterEnd = 6;
    public const int ResultIsNotArc = 7;
    public const int ResultHeadersError = 8;
    public const int ResultWrongPassword = 9;

    // STREAM_SEEK_*
    public const uint SeekSet = 0;
    public const uint SeekCur = 1;
    public const uint SeekEnd = 2;
}

/// <summary>
/// A PROPVARIANT as 7-Zip uses it (24 bytes on x64): the type, three reserved words and the value. Only the types
/// 7-Zip exchanges with an archive handler are needed here.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    public const ushort VtEmpty = 0;
    public const ushort VtBstr = 8;
    public const ushort VtBool = 11;
    public const ushort VtUi4 = 19;
    public const ushort VtUi8 = 21;
    public const ushort VtFiletime = 64;

    [FieldOffset(0)]
    public ushort VarType;

    [FieldOffset(2)]
    public ushort Reserved1;

    [FieldOffset(4)]
    public ushort Reserved2;

    [FieldOffset(6)]
    public ushort Reserved3;

    [FieldOffset(8)]
    public long Int64Value;

    [FieldOffset(8)]
    public uint UInt32Value;

    [FieldOffset(8)]
    public short BoolValue;

    [FieldOffset(8)]
    public IntPtr Pointer;
}
