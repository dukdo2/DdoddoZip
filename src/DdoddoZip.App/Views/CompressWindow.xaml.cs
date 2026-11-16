using System.Windows;
using System.Windows.Controls;
using DdoddoZip.App.Services;
using DdoddoZip.Core;
using Microsoft.Win32;

namespace DdoddoZip.App.Views;

public sealed record CompressRequest(string Destination, CompressOptions Options);

/// <summary>S3: name, location, format and level for a new archive.</summary>
public partial class CompressWindow : Window
{
    private ArchiveKind _kind = ArchiveKind.Zip;
    private CompressRequest? _result;

    private CompressWindow()
    {
        InitializeComponent();
    }

    public static CompressRequest? Ask(Window? owner, IReadOnlyList<string> sources)
    {
        var w = new CompressWindow();
        if (owner is { IsVisible: true }) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.ShowInTaskbar = w.Owner is null;

        var settings = AppSettings.Current;
        var first = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sources[0]));
        w.FolderBox.Text = Path.GetDirectoryName(first) ?? first;
        w.NameBox.Text = NamePolicy.QuickArchiveBaseName(sources);

        SelectByTag(w.FormatGroup, Enum.TryParse<ArchiveKind>(settings.LastFormat, out var k) ? k.ToString() : nameof(ArchiveKind.Zip));
        SelectByTag(w.LevelGroup, Enum.TryParse<CompressLevel>(settings.LastLevel, out var l) ? l.ToString() : nameof(CompressLevel.Normal));
        w.CountAsync(sources);
        w.Loaded += (_, _) => { w.NameBox.Focus(); w.NameBox.SelectAll(); };

        w.ShowDialog();
        return w._result;
    }

    private static void SelectByTag(Panel group, string tag)
    {
        foreach (var rb in group.Children.OfType<RadioButton>())
            rb.IsChecked = (string)rb.Tag == tag;
    }

    private static string? SelectedTag(Panel group) =>
        group.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;

    private async void CountAsync(IReadOnlyList<string> sources)
    {
        var (files, bytes) = await Task.Run(() =>
        {
            long count = 0, size = 0;
            foreach (var s in sources)
            {
                try
                {
                    if (File.Exists(s)) { count++; size += new FileInfo(s).Length; }
                    else if (Directory.Exists(s))
                    {
                        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                        foreach (var f in new DirectoryInfo(s).EnumerateFiles("*", opts)) { count++; size += f.Length; }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            return (count, size);
        });
        SummaryText.Text = $"담길 파일 {files:N0}개 · 전체 {Fmt.Bytes((ulong)bytes)}";
    }

    private void Format_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && Enum.TryParse(tag, out ArchiveKind kind))
        {
            _kind = kind;
            ExtText.Text = ArchiveKindInfo.Extension(kind);
            bool hasLevel = kind != ArchiveKind.Tar; // plain TAR does not compress
            LevelBox.IsEnabled = hasLevel;
            LevelBox.Opacity = hasLevel ? 1 : 0.4;
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "저장할 폴더 선택", InitialDirectory = FolderBox.Text };
        if (dlg.ShowDialog(this) == true)
            FolderBox.Text = dlg.FolderName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var ext = ArchiveKindInfo.Extension(_kind);
        if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            name = name[..^ext.Length];
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, "압축 파일 이름에 쓸 수 없는 글자가 있습니다.", "압축하기", MessageBoxButton.OK, MessageBoxImage.Warning);
            NameBox.Focus();
            return;
        }
        var folder = FolderBox.Text.Trim();
        if (!Directory.Exists(folder))
        {
            MessageBox.Show(this, "저장 위치 폴더가 없습니다.", "압축하기", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dest = Path.Combine(folder, name + ext);
        if (File.Exists(dest))
        {
            var answer = MessageBox.Show(this, $"{Path.GetFileName(dest)} 파일이 이미 있습니다.\n바꿀까요?", "압축하기",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }

        var level = Enum.Parse<CompressLevel>(SelectedTag(LevelGroup) ?? nameof(CompressLevel.Normal));
        AppSettings.Current.LastFormat = _kind.ToString();
        AppSettings.Current.LastLevel = level.ToString();
        AppSettings.Current.Save();

        _result = new CompressRequest(dest, new CompressOptions { Kind = _kind, Level = level });
        Close();
    }
}
