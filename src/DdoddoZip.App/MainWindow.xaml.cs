using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DdoddoZip.App.Services;
using DdoddoZip.App.ViewModels;
using DdoddoZip.App.Views;
using DdoddoZip.Core;
using Microsoft.Win32;

namespace DdoddoZip.App;

public sealed record RecentItem(string Name, string Detail, string Path);

/// <summary>S1 (empty) and S2 (archive opened).</summary>
public partial class MainWindow : Window
{
    private const ulong DragOutWithoutProgressLimit = 64UL * 1024 * 1024;

    private ArchiveSession? _session;
    private FolderNode? _root;
    private FolderNode? _current;
    private readonly Stack<FolderNode> _back = new();
    private int? _codePage;
    private ImageSource? _folderIcon;

    private Point _dragStart;
    private bool _dragCandidate;
    private bool _draggingOut;

    public MainWindow()
    {
        InitializeComponent();
        ShowRecent();
        var support = SupportLinks.Any ? Visibility.Visible : Visibility.Collapsed;
        ToolSupport.Visibility = support;
        EmptySupport.Visibility = support;
        Loaded += async (_, _) => await StartupUpdateCheckAsync();
    }

    // ───────────────────────── update (F20) ─────────────────────────

    private enum BannerMode { None, NewVersion, JustUpdated }

    private BannerMode _banner;
    private List<ReleaseInfo>? _newer;
    private Version? _updatedFrom;

    /// <summary>
    /// After the window shows: once after an update, say so; otherwise, at most once a day,
    /// ask GitHub for a newer version. Failures stay silent (no internet is normal).
    /// </summary>
    private async Task StartupUpdateCheckAsync()
    {
        var settings = AppSettings.Current;
        var current = UpdateService.Current;
        var lastRun = Version.TryParse(settings.LastRunVersion, out var v) ? v : null;
        settings.LastRunVersion = current.ToString(3);
        settings.Save();

        if (lastRun is not null && lastRun < current)
        {
            _updatedFrom = lastRun;
            ShowBanner(BannerMode.JustUpdated, $"{current.ToString(3)} 버전으로 업데이트했습니다.");
            return;
        }

        if (!UpdateService.DueForCheck(settings)) return;
        try
        {
            var newer = await UpdateService.GetNewerAsync();
            settings.LastUpdateCheckUtc = DateTime.UtcNow;
            settings.Save();
            if (newer.Count > 0 && newer[0].Display != settings.SkippedVersion)
            {
                _newer = newer;
                ShowBanner(BannerMode.NewVersion, $"새 버전이 나왔습니다: 또또집 {newer[0].Display}");
            }
        }
        catch (Exception e) when (e is UpdateException or System.Text.Json.JsonException or OperationCanceledException) { }
    }

    private void ShowBanner(BannerMode mode, string text)
    {
        _banner = mode;
        BannerText.Text = text;
        BannerText.FontWeight = FontWeights.SemiBold;
        if (mode == BannerMode.NewVersion)
        {
            BannerPrimary.Content = "업데이트";
            BannerPrimary.Visibility = Visibility.Visible;
            BannerSecondary.Content = "이 버전 건너뛰기";
            BannerSecondary.Style = (Style)FindResource("SmallButton");
        }
        else
        {
            BannerPrimary.Visibility = Visibility.Collapsed;
            BannerSecondary.Content = "바뀐 점 보기";
        }
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private void HideBanner()
    {
        _banner = BannerMode.None;
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private async void BannerPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (_newer is { Count: > 0 } newer && NotesWindow.AskToUpdate(this, newer))
            await StartUpdateAsync(newer);
    }

    private async void BannerSecondary_Click(object sender, RoutedEventArgs e)
    {
        if (_banner == BannerMode.NewVersion && _newer is { Count: > 0 } newer)
        {
            AppSettings.Current.SkippedVersion = newer[0].Display;
            AppSettings.Current.Save();
            HideBanner();
        }
        else if (_banner == BannerMode.JustUpdated && _updatedFrom is { } from)
        {
            HideBanner();
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                var installed = await UpdateService.GetInstalledSinceAsync(from);
                Mouse.OverrideCursor = null;
                if (installed.Count > 0) NotesWindow.ShowInstalled(this, installed);
                else ShellNative.OpenWithShell(UpdateService.RepositoryUrl + "/releases");
            }
            catch (Exception ex) when (ex is UpdateException or System.Text.Json.JsonException)
            {
                Mouse.OverrideCursor = null;
                ErrorWindow.Show(this, "바뀐 점", "바뀐 점을 불러오지 못했습니다. 인터넷 연결을 확인해 주세요.", ex.Message);
            }
        }
    }

    private void BannerClose_Click(object sender, RoutedEventArgs e) => HideBanner();

    /// <summary>Downloads the newest release (progress window), checks it, starts the setup file and closes.</summary>
    private async Task StartUpdateAsync(IReadOnlyList<ReleaseInfo> newer)
    {
        var release = newer[0];
        string? setup;
        try
        {
            setup = await Operation.RunAsync(this, $"또또집 {release.Display} 받는 중",
                (p, _, ct) => UpdateService.DownloadAsync(release, p, ct).GetAwaiter().GetResult(), "업데이트하지 못했습니다");
        }
        catch (UpdateException ex)
        {
            ErrorWindow.Show(this, "업데이트하지 못했습니다", ex.Message, ex.Details,
                "지금 쓰는 또또집은 그대로 쓸 수 있습니다.");
            return;
        }
        if (setup is null) return; // cancelled
#if DEBUG
        // Development check of the download and hash steps without installing anything.
        if (Environment.GetEnvironmentVariable("DDZ_NO_INSTALL") == "1")
        {
            MessageBox.Show(this, $"받기·해시 확인 완료 (설치는 건너뜀)\n{setup}\n{new FileInfo(setup).Length:N0} bytes", "DDZ_NO_INSTALL");
            return;
        }
#endif
        try
        {
            UpdateService.StartInstaller(setup);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // the person said no to the admin prompt
        {
            return;
        }
        catch (Win32Exception ex)
        {
            ErrorWindow.Show(this, "업데이트하지 못했습니다", "설치 파일을 실행하지 못했습니다.", ex.Message);
            return;
        }
        HideBanner();
        Application.Current.Shutdown();
    }

    private void About_Click(object sender, RoutedEventArgs e) =>
        AboutWindow.Show(this, _newer, focusSupport: false, StartUpdateAsync);

    private void Support_Click(object sender, RoutedEventArgs e) =>
        AboutWindow.Show(this, _newer, focusSupport: true, StartUpdateAsync);

    // ───────────────────────── opening ─────────────────────────

    /// <summary>"x.tar.gz" bigger than this opens with the progress window (its TAR is unpacked first).</summary>
    private const long CompoundTarProgressLimit = 32L * 1024 * 1024;

    public async void OpenArchive(string path, int? codePage = null)
    {
        var options = new ArchiveOpenOptions { CodePage = codePage };
        _folderIcon ??= ShellNative.GetTypeInfo("folder", isFolder: true).Icon;
        var folderIcon = _folderIcon;

        // Opening and building the folder tree both run off the UI thread, so archives with
        // hundreds of thousands of entries do not freeze the window.
        Opened Load(ArchiveSession s) =>
            new(s, FolderNode.Build(Path.GetFileName(s.ArchivePath), s.Entries, folderIcon));

        if (ArchiveSession.IsCompoundTar(path) && SafeLength(path) > CompoundTarProgressLimit)
        {
            var opened = await Operation.RunAsync(this, $"{Path.GetFileName(path)} 여는 중",
                (p, ia, ct) => Load(ArchiveSession.Open(path, options, ia, ct, p)), "열 수 없습니다");
            if (opened is not null) Show(opened, codePage);
            return;
        }

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var interaction = new WpfInteraction(this);
            var opened = await Task.Run(() => Load(ArchiveSession.Open(path, options, interaction)));
            Show(opened, codePage);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ArchiveException e)
        {
            Mouse.OverrideCursor = null;
            ErrorWindow.Show(this, "열 수 없습니다", Operation.Friendly(e), e.ToString(),
                e.Kind == ArchiveErrorKind.NotArchive ? Path.GetFileName(path) : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Mouse.OverrideCursor = null;
            ErrorWindow.Show(this, "열 수 없습니다", "파일을 읽을 수 없습니다. " + e.Message, e.ToString());
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private sealed record Opened(ArchiveSession Session, FolderNode Root);

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    private void Show(Opened opened, int? codePage)
    {
        var old = _session;
        _session = opened.Session;
        _codePage = codePage;
        if (old is not null) _ = Task.Run(old.Dispose);

        BuildView(opened.Root);
        AppSettings.Current.AddRecent(opened.Session.ArchivePath);
    }

    private void BuildView(FolderNode root)
    {
        var session = _session!;
        var archiveName = Path.GetFileName(session.ArchivePath);
        _root = root;
        FolderTree.ItemsSource = new[] { _root };
        _back.Clear();
        _current = null;
        Navigate(_root, push: false);

        Title = $"{archiveName} - 또또집";
        EmptyView.Visibility = Visibility.Collapsed;
        ArchiveView.Visibility = Visibility.Visible;

        var files = session.Entries.Count(e => !e.IsDirectory);
        var archiveSize = (ulong)new FileInfo(session.ArchivePath).Length;
        var total = session.TotalSize;
        FormatText.Text = session.FormatName;
        CountText.Text = $"파일 {files:N0}개";
        RatioText.Text = total > 0
            ? $"{Fmt.Bytes(total)} → {Fmt.Bytes(archiveSize)} ({100.0 * archiveSize / total:N0}%)"
            : Fmt.Bytes(archiveSize);

        if (session.NameEncoding is { Kind: not NameEncodingKind.Unknown } enc)
        {
            EncodingTag.Visibility = Visibility.Visible;
            EncodingText.Text = _codePage is int cp ? $"인코딩: CP{cp} (직접 지정)" : $"인코딩: 자동 ({enc.Display})";
        }
        else
        {
            EncodingTag.Visibility = Visibility.Collapsed;
        }
        EncodingButton.IsEnabled = session.NameEncoding is not null;
        CryptText.Text = session.HasEncryptedEntries ? "🔒 암호 있음" : "암호 없음";
    }

    // ───────────────────────── navigation ─────────────────────────

    private void Navigate(FolderNode node, bool push)
    {
        if (ReferenceEquals(node, _current)) return;
        if (push && _current is not null) _back.Push(_current);
        _current = node;

        for (var p = node.Parent; p is not null; p = p.Parent) p.IsExpanded = true;
        node.IsSelected = true;

        var rows = new List<ItemRow>(node.Children.Count + node.Files.Count);
        foreach (var f in node.Children)
        {
            rows.Add(new ItemRow
            {
                Name = f.Name, IsFolder = true, Icon = _folderIcon, TypeName = "파일 폴더",
                Modified = f.Entry?.Modified, Folder = f, Entry = f.Entry,
            });
        }
        foreach (var e in node.Files)
        {
            var (icon, type) = ShellNative.GetTypeInfo(e.Name, isFolder: false);
            rows.Add(new ItemRow
            {
                Name = e.Name, IsFolder = false, Icon = icon, TypeName = type, Size = e.Size,
                PackedSize = e.PackedSize, Modified = e.Modified, IsEncrypted = e.IsEncrypted, Entry = e,
            });
        }

        var view = new ListCollectionView(rows) { CustomSort = new RowComparer("Name", ListSortDirection.Ascending) };
        FileList.ItemsSource = view;
        foreach (var c in FileList.Columns) c.SortDirection = null;
        FileList.Columns[0].SortDirection = ListSortDirection.Ascending;

        var parts = new List<string>();
        for (var p = node; p is not null; p = p.Parent) parts.Insert(0, p.Name);
        AddressText.Text = string.Join("  ›  ", parts);
        BackButton.IsEnabled = _back.Count > 0;
        UpButton.IsEnabled = node.Parent is not null;
        UpdateSelectionText();
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNode node && !ReferenceEquals(node, _current))
            Navigate(node, push: true);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_back.Count > 0) Navigate(_back.Pop(), push: false);
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.Parent is { } parent) Navigate(parent, push: true);
    }

    private void FileList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<DataGridRow>(d) is { Item: ItemRow row })
            Activate(row);
    }

    private void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && FileList.SelectedItem is ItemRow row)
        {
            Activate(row);
            e.Handled = true;
        }
    }

    private void Activate(ItemRow row)
    {
        if (row.IsFolder && row.Folder is not null) Navigate(row.Folder, push: true);
        else if (row.Entry is not null) OpenEntry(row.Entry);
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionText();

    private void UpdateSelectionText()
    {
        var rows = SelectedRows();
        if (rows.Count == 0)
        {
            SelectionText.Text = "";
            return;
        }
        ulong size = 0;
        foreach (var i in CollectEntries(rows)) size += i.Size;
        SelectionText.Text = $"{rows.Count:N0}개 선택 ({Fmt.Bytes(size)})";
    }

    private void FileList_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var dir = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var c in FileList.Columns) c.SortDirection = null;
        e.Column.SortDirection = dir;
        if (FileList.ItemsSource is ListCollectionView view)
            view.CustomSort = new RowComparer(e.Column.SortMemberPath, dir);
    }

    // ───────────────────────── extract ─────────────────────────

    private List<ItemRow> SelectedRows() => FileList.SelectedItems.OfType<ItemRow>().ToList();

    private List<ArchiveEntry> CollectEntries(IEnumerable<ItemRow> rows)
    {
        var list = new List<ArchiveEntry>();
        foreach (var r in rows)
        {
            if (r.IsFolder && r.Folder is not null)
            {
                foreach (var n in r.Folder.SelfAndDescendants())
                {
                    if (n.Entry is not null) list.Add(n.Entry);
                    list.AddRange(n.Files);
                }
            }
            else if (r.Entry is not null)
            {
                list.Add(r.Entry);
            }
        }
        return list;
    }

    private void Extract_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        var (indices, baseFolder, entries) = CurrentSelection();
        var totalFiles = _session.Entries.Count(x => !x.IsDirectory);
        var selectedFiles = entries.Count(x => !x.IsDirectory);
        var req = ExtractWindow.Ask(this, _session.ArchivePath, selectedFiles, totalFiles, _session.HasSingleRoot(entries, baseFolder));
        if (req is null) return;
        RunExtract(new ExtractOptions
        {
            Destination = req.Destination, FolderMode = req.FolderMode, Overwrite = req.Overwrite,
            Indices = indices, BaseFolder = baseFolder,
        }, req.OpenAfter);
    }

    private void ExtractHere_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        var (indices, baseFolder, _) = CurrentSelection();
        RunExtract(new ExtractOptions
        {
            Destination = Path.GetDirectoryName(_session.ArchivePath)!, FolderMode = FolderMode.Auto,
            Indices = indices, BaseFolder = baseFolder,
        }, AppSettings.Current.OpenFolderAfterExtract);
    }

    /// <summary>F15: reads every file and checks it without writing anything.</summary>
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        var session = _session;
        var name = Path.GetFileName(session.ArchivePath);
        var result = await Operation.RunAsync(this, $"{name} 검사 중",
            (p, ia, ct) => session.Test(ia, p, ct), "검사를 끝내지 못했습니다");
        if (result is null) return;

        var total = session.Entries.Count(x => !x.IsDirectory);
        if (result.HasProblems)
            ErrorWindow.ShowProblems(this, "검사 결과", result, total);
        else
            MessageBox.Show(this, $"이상 없습니다.\n파일 {result.FilesTested:N0}개를 모두 확인했습니다.", "검사 결과",
                MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Selected rows, or the whole archive when nothing is selected.</summary>
    private (IReadOnlyCollection<int>? Indices, string? BaseFolder, List<ArchiveEntry> Entries) CurrentSelection()
    {
        var rows = SelectedRows();
        if (rows.Count == 0) return (null, null, [.. _session!.Entries]);
        var entries = CollectEntries(rows);
        return (entries.Select(x => x.Index).ToList(), _current?.Path, entries);
    }

    private async void RunExtract(ExtractOptions options, bool openAfter)
    {
        var session = _session!;
        var name = Path.GetFileName(session.ArchivePath);
        var result = await Operation.RunAsync(this, $"{name} 푸는 중",
            (p, ia, ct) => session.Extract(options, ia, p, ct), "풀기를 끝내지 못했습니다");
        if (result is null) return;

        if (result.HasProblems)
            ErrorWindow.ShowProblems(this, "풀기를 끝내지 못했습니다", result, result.FilesWritten + result.Skipped + result.Failures.Count + result.BlockedPaths.Count);
        if (openAfter && Directory.Exists(result.OutputDirectory))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{result.OutputDirectory}\"") { UseShellExecute = true });
    }

    /// <summary>Double-click on a file: extract it to a temp folder and open it with its program.</summary>
    private async void OpenEntry(ArchiveEntry entry)
    {
        var session = _session!;
        var temp = TempFiles.NewFolder();
        var parent = Path.GetDirectoryName(entry.Path) ?? "";
        var result = await Operation.RunAsync(this, $"{entry.Name} 여는 중",
            (p, ia, ct) => session.Extract(new ExtractOptions
            {
                Destination = temp, Indices = [entry.Index], BaseFolder = parent,
                FolderMode = FolderMode.Never, Overwrite = OverwriteMode.Overwrite,
            }, ia, p, ct), "파일을 열 수 없습니다");
        if (result is null) return;
        if (result.HasProblems)
        {
            ErrorWindow.ShowProblems(this, "파일을 열 수 없습니다", result, 1);
            return;
        }

        var file = Directory.EnumerateFiles(temp).FirstOrDefault();
        if (file is null) return;
        try { ShellNative.OpenWithShell(file); }
        catch (Win32Exception ex) { ErrorWindow.Show(this, "파일을 열 수 없습니다", "이 파일을 열 프로그램이 없습니다.", ex.Message); }
    }

    // ───────────────────────── drag out ─────────────────────────

    private void FileList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(FileList);
        _dragCandidate = e.OriginalSource is DependencyObject d && FindParent<DataGridRow>(d) is not null;
    }

    private async void FileList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragCandidate || e.LeftButton != MouseButtonState.Pressed || _session is null) return;
        var pos = e.GetPosition(FileList);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragCandidate = false;
        var rows = SelectedRows();
        if (rows.Count == 0) return;

        var entries = CollectEntries(rows);
        var options = new ExtractOptions
        {
            Destination = TempFiles.NewFolder(), Indices = entries.Select(x => x.Index).ToList(),
            BaseFolder = _current?.Path, FolderMode = FolderMode.Never, Overwrite = OverwriteMode.Overwrite,
        };
        var session = _session;
        ulong size = 0;
        foreach (var x in entries) size += x.Size;

        ExtractResult? result;
        if (size <= DragOutWithoutProgressLimit && !entries.Any(x => x.IsEncrypted))
        {
            try { result = await Task.Run(() => session.Extract(options)); }
            catch (ArchiveException ex) { ErrorWindow.Show(this, "꺼내지 못했습니다", Operation.Friendly(ex), ex.ToString()); return; }
        }
        else
        {
            result = await Operation.RunAsync(this, "끌어 놓을 파일 준비 중",
                (p, ia, ct) => session.Extract(options, ia, p, ct), "꺼내지 못했습니다");
        }
        if (result is null || result.FilesWritten + rows.Count(r => r.IsFolder) == 0) return;

        var paths = rows
            .Select(r => PathGuard.SanitizeRelative(r.Name, out _))
            .Where(n => n is not null)
            .Select(n => Path.Combine(options.Destination, n!))
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .ToArray();
        if (paths.Length == 0) return;

        _draggingOut = true;
        try
        {
            DragDrop.DoDragDrop(FileList, new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy | DragDropEffects.Move);
        }
        finally
        {
            _draggingOut = false;
        }
    }

    // ───────────────────────── drag in / compress ─────────────────────────

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_draggingOut && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (_draggingOut || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        if (files.Length == 1 && ArchiveTypes.LooksLikeArchive(files[0]))
            OpenArchive(files[0]);
        else
            StartCompress(files);
    }

    private void NewArchive_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "압축할 파일 선택 (폴더는 창에 끌어다 놓으세요)",
            Multiselect = true,
            Filter = "모든 파일|*.*",
        };
        if (dlg.ShowDialog(this) == true && dlg.FileNames.Length > 0)
            StartCompress(dlg.FileNames);
    }

    public async void StartCompress(IReadOnlyList<string> sources)
    {
        var req = CompressWindow.Ask(this, sources);
        if (req is null) return;
        var result = await Operation.RunAsync(this, $"{Path.GetFileName(req.Destination)} 압축 중",
            (p, ia, ct) => Compressor.Compress(sources, req.Destination, req.Options, p, ct), "압축하지 못했습니다");
        if (result is not null)
            ShellNative.SelectInExplorer(result.OutputPath);
    }

    // ───────────────────────── misc ─────────────────────────

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "압축 파일 열기", Filter = ArchiveTypes.OpenFilter };
        if (dlg.ShowDialog(this) == true)
            OpenArchive(dlg.FileName);
    }

    private void Encoding_Click(object sender, RoutedEventArgs e)
    {
        var current = _codePage?.ToString() ?? "0";
        foreach (var item in EncodingMenu.Items.OfType<MenuItem>())
            item.IsChecked = (string)item.Tag == current;
        EncodingMenu.PlacementTarget = EncodingButton;
        EncodingMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        EncodingMenu.IsOpen = true;
    }

    private void EncodingItem_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || sender is not MenuItem { Tag: string tag }) return;
        var cp = int.Parse(tag);
        OpenArchive(_session.ArchivePath, cp == 0 ? null : cp);
    }

    private void ShowRecent()
    {
        var items = AppSettings.Current.RecentFiles
            .Where(File.Exists)
            .Select(p => new RecentItem(
                Path.GetFileName(p),
                $"{Path.GetDirectoryName(p)} · {Fmt.Relative(File.GetLastAccessTime(p))}",
                p))
            .ToList();
        RecentList.ItemsSource = items;
        RecentPanel.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path }) OpenArchive(path);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_session is { } s) Task.Run(s.Dispose).Wait(2000);
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var d = child; d is not null; d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is T t) return t;
        return null;
    }

    /// <summary>Folders first, then the chosen column, with Explorer-style number ordering.</summary>
    private sealed class RowComparer(string member, ListSortDirection direction) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var a = (ItemRow)x!;
            var b = (ItemRow)y!;
            if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
            int r = member switch
            {
                "Size" => Nullable.Compare(a.Size, b.Size),
                "PackedSize" => Nullable.Compare(a.PackedSize, b.PackedSize),
                "TypeName" => string.Compare(a.TypeName, b.TypeName, StringComparison.CurrentCultureIgnoreCase),
                "Modified" => Nullable.Compare(a.Modified, b.Modified),
                _ => 0,
            };
            if (r == 0) r = ShellNative.CompareNames(a.Name, b.Name);
            return direction == ListSortDirection.Descending ? -r : r;
        }
    }
}
