using Microsoft.Win32;

namespace DdoddoZip.App.Services;

/// <summary>
/// Windows integration (F10-F12): Explorer right-click items and "open with" registration.
///
/// Machine-wide (HKLM, needs admin) is the default: on the development PC (Windows 11 with
/// StartAllBack) Explorer ignored every per-user (HKCU) context-menu verb, even a plain command
/// verb, while other processes showed them. HKLM registrations appeared immediately.
/// </summary>
public static class ShellIntegration
{
    private sealed record Verb(string Key, string ClsId, string Text, bool FoldersToo);

    // Key names sort the items in the menu; class IDs match ShellCommands.cpp.
    private static readonly Verb[] Verbs =
    [
        new("DdoddoZip.A1Zip", "{BDC64B6B-7E60-401C-A845-CAA4BD6AA71F}", "또또집: ZIP으로 압축", true),
        new("DdoddoZip.A2SevenZip", "{B62DA6EE-8453-4DCD-B67B-6D3E79731602}", "또또집: 7Z로 압축", true),
        new("DdoddoZip.A3Compress", "{A836D04B-F475-4923-BF16-BF5429FD0AB5}", "또또집으로 압축하기...", true),
        new("DdoddoZip.B1ExtractHere", "{10C5A761-3040-4168-9F84-8694004D0108}", "또또집: 여기에 풀기", false),
        new("DdoddoZip.B2ExtractFolder", "{873F9FB5-732E-499E-9B82-DF092B493C0E}", "또또집: 폴더에 풀기", false),
        new("DdoddoZip.B3Extract", "{D95FD301-85B5-413B-BDB9-808CD227E75B}", "또또집으로 풀기...", false),
    ];

    // Verb keys written by earlier builds; removed on register/unregister.
    private static readonly string[] OldVerbKeys =
    [
        @"Software\Classes\AllFilesystemObjects\shell\DdoddoZip.QuickZip",
        @"Software\Classes\*\shell\DdoddoZip.QuickZip",
        @"Software\Classes\Directory\shell\DdoddoZip.QuickZip",
    ];

    private const string ProgId = "DdoddoZip.Archive";
    private const string CapabilitiesKey = @"Software\DdoddoZip\Capabilities";

    /// <summary>Types offered under "연결 프로그램". The current default app is never changed.</summary>
    public static readonly string[] AssociatedExtensions =
    [
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz", ".zst",
        ".iso", ".cab", ".wim", ".lzh", ".arj",
    ];

    private static string AppDir => AppContext.BaseDirectory;
    public static string DllPath => Path.Combine(AppDir, "DdoddoZip.ShellExt.dll");
    private static string ExePath => Path.Combine(AppDir, "DdoddoZip.exe");

    private static RegistryKey Root(bool machine) => machine ? Registry.LocalMachine : Registry.CurrentUser;

    public static bool IsRegistered(bool machine = true)
    {
        using var k = Root(machine).OpenSubKey($@"Software\Classes\CLSID\{Verbs[0].ClsId}\InprocServer32");
        return k?.GetValue(null) is string p && p.Equals(DllPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <param name="machine">True: all users (HKLM, run elevated). False: current user only.</param>
    public static void Register(bool machine = true)
    {
        if (!File.Exists(DllPath))
            throw new FileNotFoundException("우클릭 메뉴 파일(DdoddoZip.ShellExt.dll)을 찾을 수 없습니다.", DllPath);

        var root = Root(machine);
        foreach (var old in OldVerbKeys)
            root.DeleteSubKeyTree(old, throwOnMissingSubKey: false);

        RegisterContextMenu(root);
        RegisterFileTypes(root);
        ShellNative.NotifyAssociationsChanged();
    }

    public static void Unregister(bool machine = true)
    {
        var root = Root(machine);
        foreach (var old in OldVerbKeys)
            root.DeleteSubKeyTree(old, throwOnMissingSubKey: false);
        foreach (var v in Verbs)
        {
            root.DeleteSubKeyTree($@"Software\Classes\*\shell\{v.Key}", throwOnMissingSubKey: false);
            root.DeleteSubKeyTree($@"Software\Classes\Directory\shell\{v.Key}", throwOnMissingSubKey: false);
            root.DeleteSubKeyTree($@"Software\Classes\CLSID\{v.ClsId}", throwOnMissingSubKey: false);
        }

        root.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        root.DeleteSubKeyTree(@"Software\Classes\Applications\DdoddoZip.exe", throwOnMissingSubKey: false);
        root.DeleteSubKeyTree(@"Software\DdoddoZip", throwOnMissingSubKey: false);
        using (var apps = root.OpenSubKey(@"Software\RegisteredApplications", writable: true))
            apps?.DeleteValue("DdoddoZip", throwOnMissingValue: false);
        foreach (var ext in AssociatedExtensions)
        {
            using var k = root.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids", writable: true);
            k?.DeleteValue(ProgId, throwOnMissingValue: false);
        }
        ShellNative.NotifyAssociationsChanged();
    }

    private static void RegisterContextMenu(RegistryKey root)
    {
        foreach (var v in Verbs)
        {
            using (var clsid = root.CreateSubKey($@"Software\Classes\CLSID\{v.ClsId}"))
            {
                clsid.SetValue(null, v.Text);
                using var inproc = clsid.CreateSubKey("InprocServer32");
                inproc.SetValue(null, DllPath);
                inproc.SetValue("ThreadingModel", "Apartment");
            }

            // Files ("*") and, for compress items, folders ("Directory"). Explorer ignored the
            // handler when it was registered once under "AllFilesystemObjects".
            var targets = v.FoldersToo ? new[] { "*", "Directory" } : ["*"];
            foreach (var target in targets)
            {
                using var verb = root.CreateSubKey($@"Software\Classes\{target}\shell\{v.Key}");
                // Same shape as handlers Explorer does show (Notepad++ and others): a display
                // text, an icon, and NeverDefault so double-click never runs this verb.
                verb.SetValue(null, v.Text);
                verb.SetValue("Icon", ExePath + ",0");
                verb.SetValue("NeverDefault", "");
                verb.SetValue("ExplorerCommandHandler", v.ClsId);
                verb.SetValue("MultiSelectModel", "Player"); // any number of selected items
            }
        }
    }

    /// <summary>
    /// Makes 또또집 a choice under "연결 프로그램" and in Settings → 기본 앱 for archive types.
    /// Windows does not let apps make themselves the default; the person picks it there.
    /// </summary>
    private static void RegisterFileTypes(RegistryKey root)
    {
        var open = $"\"{ExePath}\" \"%1\"";
        var icon = Path.Combine(AppDir, "archive.ico");

        using (var prog = root.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            prog.SetValue(null, "또또집 압축 파일");
            using (var di = prog.CreateSubKey("DefaultIcon"))
                di.SetValue(null, File.Exists(icon) ? icon : ExePath + ",0");
            using var cmd = prog.CreateSubKey(@"shell\open\command");
            cmd.SetValue(null, open);
        }

        using (var app = root.CreateSubKey(@"Software\Classes\Applications\DdoddoZip.exe"))
        {
            app.SetValue("FriendlyAppName", "또또집");
            using (var cmd = app.CreateSubKey(@"shell\open\command"))
                cmd.SetValue(null, open);
            using var types = app.CreateSubKey("SupportedTypes");
            foreach (var ext in AssociatedExtensions)
                types.SetValue(ext, "");
        }

        using (var caps = root.CreateSubKey(CapabilitiesKey))
        {
            caps.SetValue("ApplicationName", "또또집");
            caps.SetValue("ApplicationDescription", "광고 없는 가벼운 압축 프로그램");
            using var assoc = caps.CreateSubKey("FileAssociations");
            foreach (var ext in AssociatedExtensions)
                assoc.SetValue(ext, ProgId);
        }
        using (var apps = root.CreateSubKey(@"Software\RegisteredApplications"))
            apps.SetValue("DdoddoZip", CapabilitiesKey);

        foreach (var ext in AssociatedExtensions)
        {
            using var k = root.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids");
            k.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }
    }
}
