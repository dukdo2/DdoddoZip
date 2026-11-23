using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace DdoddoZip.App.Services;

/// <summary>Small user settings kept in %APPDATA%\DdoddoZip\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DdoddoZip", "settings.json");

    public List<string> RecentFiles { get; set; } = [];
    public string LastFormat { get; set; } = "Zip";
    public string LastLevel { get; set; } = "Normal";
    public bool OpenFolderAfterExtract { get; set; }

    // F20 auto update
    public bool AutoCheckUpdates { get; set; } = true;
    public DateTime? LastUpdateCheckUtc { get; set; }
    /// <summary>Version the person chose to skip ("이 버전 건너뛰기"), e.g. "1.1.1".</summary>
    public string? SkippedVersion { get; set; }
    /// <summary>Version that ran last time: tells the first run after an update apart.</summary>
    public string? LastRunVersion { get; set; }

    public static AppSettings Current { get; } = Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 5) RecentFiles.RemoveRange(5, RecentFiles.Count - 5);
        Save();
    }
}

public static class ArchiveTypes
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz", ".tbz2", ".xz", ".txz", ".zst", ".tzst",
        ".lzma", ".iso", ".cab", ".wim", ".lzh", ".lha", ".arj", ".cpio", ".rpm", ".deb", ".z", ".jar", ".apk",
        ".001", ".udf",
    };

    public const string OpenFilter =
        "압축 파일|*.zip;*.7z;*.rar;*.tar;*.gz;*.tgz;*.bz2;*.tbz2;*.xz;*.txz;*.zst;*.iso;*.cab;*.wim;*.lzh;*.arj;*.cpio;*.rpm;*.deb;*.jar;*.apk;*.001|모든 파일|*.*";

    public static bool LooksLikeArchive(string path) => File.Exists(path) && Extensions.Contains(Path.GetExtension(path));
}

/// <summary>Temporary files for "double-click to open" and drag-out. Removed when the app exits.</summary>
public static class TempFiles
{
    public static string Root { get; } = Path.Combine(Path.GetTempPath(), "DdoddoZip", "app-" + Environment.ProcessId);

    public static string NewFolder()
    {
        var dir = Path.Combine(Root, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Cleanup()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Removes what earlier runs left in %TEMP%\DdoddoZip after a crash or a forced close:
    /// "app-&lt;pid&gt;" folders of processes that are gone, and unpacked TAR files and right-click
    /// lists older than a day (an unpacked .tar.gz can be many gigabytes).
    /// </summary>
    public static void SweepStale()
    {
        try
        {
            var root = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "DdoddoZip"));
            if (!root.Exists) return;
            var alive = Process.GetProcessesByName("DdoddoZip").Select(p => p.Id).ToHashSet();
            var cutoff = DateTime.UtcNow.AddDays(-1);

            foreach (var dir in root.EnumerateDirectories("app-*"))
            {
                if (int.TryParse(dir.Name.AsSpan(4), out var pid) && alive.Contains(pid)) continue;
                try { dir.Delete(recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            foreach (var file in root.EnumerateFiles())
            {
                if (file.LastWriteTimeUtc > cutoff) continue;
                try { file.Delete(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

public static class Fmt
{
    public static string Bytes(ulong b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:N0} KB",
        < 1024UL * 1024 * 1024 => $"{b / 1024.0 / 1024:N1} MB",
        _ => $"{b / 1024.0 / 1024 / 1024:N2} GB",
    };

    public static string Date(DateTime? utc) => utc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";

    public static string Relative(DateTime local)
    {
        var days = (DateTime.Today - local.Date).Days;
        return days switch
        {
            0 => "오늘",
            1 => "어제",
            < 7 => $"{days}일 전",
            _ => local.ToString("M월 d일"),
        };
    }

    public static string Duration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"약 {Math.Max(1, (int)t.TotalSeconds)}초" :
        t.TotalMinutes < 60 ? $"약 {(int)t.TotalMinutes}분 {t.Seconds}초" :
        $"약 {(int)t.TotalHours}시간 {t.Minutes}분";
}

/// <summary>Windows shell helpers: file-type icons and names, showing a file in Explorer.</summary>
public static class ShellNative
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_TYPENAME = 0x400, SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr apidl, uint flags);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private static readonly Dictionary<string, (ImageSource? Icon, string TypeName)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Icon and type name for a file name (by extension) or a folder. UI thread only.</summary>
    public static (ImageSource? Icon, string TypeName) GetTypeInfo(string name, bool isFolder)
    {
        var key = isFolder ? "<folder>" : Path.GetExtension(name);
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var info = new SHFILEINFO();
        var probe = isFolder ? "folder" : "file" + key;
        SHGetFileInfo(probe, isFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL, ref info,
            (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON | SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);

        ImageSource? icon = null;
        if (info.hIcon != IntPtr.Zero)
        {
            icon = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            icon.Freeze();
            DestroyIcon(info.hIcon);
        }
        var typeName = string.IsNullOrEmpty(info.szTypeName) ? (isFolder ? "파일 폴더" : key.TrimStart('.').ToUpperInvariant() + " 파일") : info.szTypeName;
        return Cache[key] = (icon, typeName);
    }

    /// <summary>Opens the folder in Explorer with the file selected (reuses an open window when possible).</summary>
    public static void SelectInExplorer(string path)
    {
        if (SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) == 0 && pidl != IntPtr.Zero)
        {
            try { SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0); }
            finally { Marshal.FreeCoTaskMem(pidl); }
        }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    /// <summary>Explorer-style name order ("file2" before "file10").</summary>
    public static int CompareNames(string a, string b) => StrCmpLogicalW(a, b);

    public static void OpenWithShell(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    public static void NotifyAssociationsChanged() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
}
