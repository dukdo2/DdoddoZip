using System.Runtime.InteropServices;

namespace DdoddoZip.Core.Interop;

// COM-style interfaces exported by 7z.dll (see 7-Zip source: IArchive.h, IStream.h, IPassword.h).
// Interface IDs follow the pattern 23170F69-40C1-278A-0000-00GG00II0000 (group GG, interface II).
// .NET does not carry base-interface methods into a ComImport vtable, so every interface
// repeats its base methods (IProgress, ISequentialInStream...) in order.

internal static class HResult
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_ABORT = unchecked((int)0x80004004);
    public const int E_FAIL = unchecked((int)0x80004005);
}

internal enum ItemPropId : uint
{
    NoProperty = 0,
    Path = 3,
    Name = 4,
    Extension = 5,
    IsDir = 6,
    Size = 7,
    PackSize = 8,
    Attrib = 9,
    CTime = 10,
    ATime = 11,
    MTime = 12,
    Solid = 13,
    Encrypted = 15,
    Method = 22,
    IsAnti = 21,
    PhySize = 44,
}

/// <summary>Result codes passed to IArchiveExtractCallback.SetOperationResult.</summary>
internal enum OperationResult
{
    Ok = 0,
    UnsupportedMethod = 1,
    DataError = 2,
    CrcError = 3,
    Unavailable = 4,
    UnexpectedEnd = 5,
    DataAfterEnd = 6,
    IsNotArc = 7,
    HeadersError = 8,
    WrongPassword = 9,
}

internal enum AskMode
{
    Extract = 0,
    Test = 1,
    Skip = 2,
    ReadExternal = 3,
}

/// <summary>Windows PROPVARIANT (24 bytes on x64).</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    public const ushort VT_EMPTY = 0;
    public const ushort VT_I4 = 3;
    public const ushort VT_BSTR = 8;
    public const ushort VT_BOOL = 11;
    public const ushort VT_UI1 = 17;
    public const ushort VT_UI2 = 18;
    public const ushort VT_UI4 = 19;
    public const ushort VT_I8 = 20;
    public const ushort VT_UI8 = 21;
    public const ushort VT_FILETIME = 64;

    [FieldOffset(0)] public ushort VarType;
    [FieldOffset(8)] public IntPtr Pointer;
    [FieldOffset(8)] public short Bool;
    [FieldOffset(8)] public uint UInt32;
    [FieldOffset(8)] public ulong UInt64;
    [FieldOffset(8)] public long Int64;

    public static PropVariant FromUInt32(uint v) => new() { VarType = VT_UI4, UInt32 = v };
    public static PropVariant FromUInt64(ulong v) => new() { VarType = VT_UI8, UInt64 = v };
    public static PropVariant FromBool(bool v) => new() { VarType = VT_BOOL, Bool = (short)(v ? -1 : 0) };
    public static PropVariant FromString(string v) => new() { VarType = VT_BSTR, Pointer = Marshal.StringToBSTR(v) };
    public static PropVariant FromFileTime(DateTime utc) => new() { VarType = VT_FILETIME, Int64 = utc.ToFileTimeUtc() };

    public readonly string? AsString() => VarType == VT_BSTR && Pointer != IntPtr.Zero ? Marshal.PtrToStringBSTR(Pointer) : null;

    public readonly bool AsBool() => VarType == VT_BOOL && Bool != 0;

    public readonly ulong AsUInt64() => VarType switch
    {
        VT_UI1 => (byte)UInt32,
        VT_UI2 => (ushort)UInt32,
        VT_UI4 => UInt32,
        VT_I4 => (ulong)(int)UInt32,
        VT_UI8 or VT_I8 => UInt64,
        _ => 0,
    };

    public readonly DateTime? AsDateTime()
    {
        if (VarType != VT_FILETIME || Int64 <= 0) return null;
        try { return DateTime.FromFileTimeUtc(Int64); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    public void Clear()
    {
        if (VarType != VT_EMPTY)
            PropVariantClear(ref this);
        this = default;
    }
}

[ComImport, Guid("23170F69-40C1-278A-0000-000300010000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISequentialInStream
{
    [PreserveSig] int Read(IntPtr data, uint size, IntPtr processedSize);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000300020000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISequentialOutStream
{
    [PreserveSig] int Write(IntPtr data, uint size, IntPtr processedSize);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000300030000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInStream
{
    [PreserveSig] int Read(IntPtr data, uint size, IntPtr processedSize);
    [PreserveSig] int Seek(long offset, uint seekOrigin, IntPtr newPosition);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000300040000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOutStream
{
    [PreserveSig] int Write(IntPtr data, uint size, IntPtr processedSize);
    [PreserveSig] int Seek(long offset, uint seekOrigin, IntPtr newPosition);
    [PreserveSig] int SetSize(ulong newSize);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000500100000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICryptoGetTextPassword
{
    [PreserveSig] int CryptoGetTextPassword(out IntPtr password); // BSTR allocated by us, freed by 7-Zip
}

[ComImport, Guid("23170F69-40C1-278A-0000-000500110000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICryptoGetTextPassword2
{
    [PreserveSig] int CryptoGetTextPassword2(out int passwordIsDefined, out IntPtr password);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000600030000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISetProperties
{
    [PreserveSig]
    int SetProperties(
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] names,
        IntPtr values,
        uint numProps);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000600100000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IArchiveOpenCallback
{
    [PreserveSig] int SetTotal(IntPtr files, IntPtr bytes);
    [PreserveSig] int SetCompleted(IntPtr files, IntPtr bytes);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000600200000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IArchiveExtractCallback
{
    // IProgress
    [PreserveSig] int SetTotal(ulong total);
    [PreserveSig] int SetCompleted(IntPtr completeValue);
    // IArchiveExtractCallback
    [PreserveSig] int GetStream(uint index, out ISequentialOutStream? outStream, AskMode askExtractMode);
    [PreserveSig] int PrepareOperation(AskMode askExtractMode);
    [PreserveSig] int SetOperationResult(OperationResult result);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000600600000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInArchive
{
    [PreserveSig] int Open(IInStream stream, ref ulong maxCheckStartPosition, IArchiveOpenCallback? openCallback);
    [PreserveSig] int Close();
    [PreserveSig] int GetNumberOfItems(out uint numItems);
    [PreserveSig] int GetProperty(uint index, ItemPropId propId, ref PropVariant value);
    [PreserveSig] int Extract([MarshalAs(UnmanagedType.LPArray)] uint[]? indices, uint numItems, int testMode, IArchiveExtractCallback extractCallback);
    [PreserveSig] int GetArchiveProperty(ItemPropId propId, ref PropVariant value);
    [PreserveSig] int GetNumberOfProperties(out uint numProperties);
    [PreserveSig] int GetPropertyInfo(uint index, out IntPtr name, out uint propId, out ushort varType);
    [PreserveSig] int GetNumberOfArchiveProperties(out uint numProperties);
    [PreserveSig] int GetArchivePropertyInfo(uint index, out IntPtr name, out uint propId, out ushort varType);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000600800000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IArchiveUpdateCallback
{
    // IProgress
    [PreserveSig] int SetTotal(ulong total);
    [PreserveSig] int SetCompleted(IntPtr completeValue);
    // IArchiveUpdateCallback
    [PreserveSig] int GetUpdateItemInfo(uint index, out int newData, out int newProps, out uint indexInArchive);
    [PreserveSig] int GetProperty(uint index, ItemPropId propId, IntPtr value);
    [PreserveSig] int GetStream(uint index, out ISequentialInStream? inStream);
    [PreserveSig] int SetOperationResult(OperationResult result);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000600A00000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOutArchive
{
    [PreserveSig] int UpdateItems(ISequentialOutStream outStream, uint numItems, IArchiveUpdateCallback updateCallback);
    [PreserveSig] int GetFileTimeType(out uint type);
}
