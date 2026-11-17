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
    }

    // ───────────────────────── opening ─────────────────────────

    public async void OpenArchive(string path, int? codePage = null)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var interaction = new WpfInteraction(this);
            var options = new ArchiveOpenOptions { CodePage = codePage };
            var session = await Task.Run(() => ArchiveSession.Open(path, options, interaction));

            var old = _session;
            _session = session;
            _codePage = codePage;
            if (old is not null) _ = Task.Run(old.Dispose);

            BuildView();
            AppSettings.Current.AddRecent(session.ArchivePath);
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

    private void BuildView()
    {
        var session = _session!;
        _folderIcon ??= ShellNative.GetTypeInfo("folder", isFolder: true).Icon;
        var archiveName = Path.GetFileName(session.ArchivePath);
        _root = FolderNode.Build(archiveName, session.Entries, _folderIcon);
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
