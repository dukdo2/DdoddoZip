using System.Runtime.InteropServices;

namespace DdoddoZip.Core.Interop;

internal sealed record FormatEntry(string Name, Guid ClassId, string[] Extensions, bool CanUpdate);

/// <summary>Loads 7z.dll once and creates archive handler objects from it.</summary>
internal sealed class SevenZipLibrary
{
    // Handlers tried (after extension matches) when guessing the format of a file.
    // Formats with weak signatures (lzma, z, arj...) are only used when the extension matches.
    private static readonly string[] FallbackOrder =
        ["zip", "7z", "rar5", "rar", "gzip", "bzip2", "xz", "zstd", "tar", "cab", "iso", "udf", "wim"];

    private static readonly Lazy<SevenZipLibrary> LazyInstance = new(() => new SevenZipLibrary());

    public static SevenZipLibrary Instance => LazyInstance.Value;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateObjectFn(ref Guid clsid, ref Guid iid, out IntPtr obj);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetNumberOfFormatsFn(out uint count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetHandlerProperty2Fn(uint formatIndex, uint propId, ref PropVariant value);

    private readonly CreateObjectFn _createObject;

    public IReadOnlyList<FormatEntry> Formats { get; }

    private SevenZipLibrary()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "7z.dll");
        if (!File.Exists(path))
            throw new ArchiveException(ArchiveErrorKind.EngineMissing, $"압축 엔진(7z.dll)을 찾을 수 없습니다: {path}");

        var handle = NativeLibrary.Load(path);
        _createObject = Marshal.GetDelegateForFunctionPointer<CreateObjectFn>(NativeLibrary.GetExport(handle, "CreateObject"));
        var getCount = Marshal.GetDelegateForFunctionPointer<GetNumberOfFormatsFn>(NativeLibrary.GetExport(handle, "GetNumberOfFormats"));
        var getProp = Marshal.GetDelegateForFunctionPointer<GetHandlerProperty2Fn>(NativeLibrary.GetExport(handle, "GetHandlerProperty2"));

        Formats = ReadFormats(getCount, getProp);
    }

    private static List<FormatEntry> ReadFormats(GetNumberOfFormatsFn getCount, GetHandlerProperty2Fn getProp)
    {
        const uint kName = 0, kClassId = 1, kExtension = 2, kUpdate = 4;
        var list = new List<FormatEntry>();
        if (getCount(out var count) != HResult.S_OK) return list;

        for (uint i = 0; i < count; i++)
        {
            var pv = default(PropVariant);
            getProp(i, kName, ref pv);
            var name = pv.AsString() ?? "";
            pv.Clear();

            getProp(i, kClassId, ref pv);
            var guidBytes = new byte[16];
            if (pv.VarType == PropVariant.VT_BSTR && pv.Pointer != IntPtr.Zero)
                Marshal.Copy(pv.Pointer, guidBytes, 0, 16);
            pv.Clear();

            getProp(i, kExtension, ref pv);
            var exts = (pv.AsString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            pv.Clear();

            getProp(i, kUpdate, ref pv);
            var canUpdate = pv.AsBool();
            pv.Clear();

            list.Add(new FormatEntry(name, new Guid(guidBytes), exts, canUpdate));
        }
        return list;
    }

    public FormatEntry GetFormat(string name) =>
        Formats.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArchiveException(ArchiveErrorKind.Unsupported, $"압축 엔진이 '{name}' 형식을 지원하지 않습니다.");

    /// <summary>Formats to try for a file: ones matching its extension first, then common formats.</summary>
    public IEnumerable<FormatEntry> GetOpenCandidates(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (ext.Length > 0)
        {
            foreach (var f in Formats)
                if (f.Extensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)) && seen.Add(f.Name))
                    yield return f;
        }
        foreach (var name in FallbackOrder)
        {
            var f = Formats.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (f is not null && seen.Add(f.Name))
                yield return f;
        }
    }

    public T CreateObject<T>(Guid classId) where T : class
    {
        var iid = typeof(T).GUID;
        int hr = _createObject(ref classId, ref iid, out var ptr);
        if (hr != HResult.S_OK || ptr == IntPtr.Zero)
            throw new ArchiveException(ArchiveErrorKind.Unsupported, $"압축 엔진 객체를 만들 수 없습니다 (0x{hr:X8}).");
        try
        {
            return (T)Marshal.GetObjectForIUnknown(ptr);
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }

    public static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
            Marshal.FinalReleaseComObject(comObject);
    }

    /// <summary>Passes "name=value" settings (level, code page, encryption...) to a handler.</summary>
    public static void SetProperties(object handler, IReadOnlyList<(string Name, PropVariant Value)> props)
    {
        if (props.Count == 0) return;
        if (handler is not ISetProperties setter)
            throw new ArchiveException(ArchiveErrorKind.Unsupported, "이 형식은 설정을 바꿀 수 없습니다.");

        var names = props.Select(p => p.Name).ToArray();
        var values = props.Select(p => p.Value).ToArray();
        var handle = GCHandle.Alloc(values, GCHandleType.Pinned);
        try
        {
            int hr = setter.SetProperties(names, handle.AddrOfPinnedObject(), (uint)values.Length);
            if (hr != HResult.S_OK)
                throw new ArchiveException(ArchiveErrorKind.Unsupported, $"압축 설정을 적용하지 못했습니다 ({string.Join(", ", names)}, 0x{hr:X8}).");
        }
        finally
        {
            handle.Free();
            for (int i = 0; i < values.Length; i++)
                values[i].Clear();
        }
    }
}
