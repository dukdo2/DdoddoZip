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
        else { w.WindowStartupLocation = WindowStartupLocation.CenterScreen; w.ShowInTaskbar = true; }
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
        var (files, bytes) = await Task.Run(() => Compressor.Measure(sources)); // same rules as the real run
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
            UpdatePasswordUi();
        }
    }

    // Only ZIP and 7Z can be encrypted; ZIP passwords must be English letters, digits and symbols.
    private static bool CanEncrypt(ArchiveKind kind) => kind is ArchiveKind.Zip or ArchiveKind.SevenZip;

    private static bool IsAsciiPassword(string pw) => pw.All(c => c is >= ' ' and <= '~');

    private void UsePassword_Changed(object sender, RoutedEventArgs e)
    {
        UpdatePasswordUi();
        if (UsePassword.IsChecked == true) Password1.Focus();
    }

    private void Password_Changed(object sender, RoutedEventArgs e) => UpdatePasswordUi();

    private void UpdatePasswordUi()
    {
        if (UsePassword is null) return; // during InitializeComponent
        bool can = CanEncrypt(_kind);
        UsePassword.IsEnabled = can;
        UsePassword.Content = can ? "암호 걸기" : "암호 걸기 (ZIP, 7Z만 가능)";
        if (!can) UsePassword.IsChecked = false;
        PasswordPanel.Visibility = UsePassword.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        EncryptNames.IsEnabled = _kind == ArchiveKind.SevenZip;
        if (_kind != ArchiveKind.SevenZip) EncryptNames.IsChecked = false;

        var pw = Password1.Password;
        string hint;
        if (_kind == ArchiveKind.Zip && pw.Length > 0 && !IsAsciiPassword(pw))
            hint = "ZIP 암호에는 영문, 숫자, 기호만 쓸 수 있습니다. 한글 암호는 7Z를 고르세요.";
        else if (Password2.Password.Length > 0 && Password2.Password != pw)
            hint = "두 암호가 다릅니다.";
        else if (_kind == ArchiveKind.Zip)
            hint = "ZIP은 AES-256으로 암호화합니다. 파일 이름은 암호 없이도 보입니다.";
        else
            hint = "7Z는 AES-256으로 암호화합니다.";
        PasswordHint.Text = hint;
        PasswordHint.Foreground = hint.StartsWith("ZIP 암호에는") || hint.StartsWith("두 암호")
            ? (System.Windows.Media.Brush)Application.Current.Resources["Danger"]
            : (System.Windows.Media.Brush)Application.Current.Resources["Sub"];
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

        string? password = null;
        if (UsePassword.IsChecked == true && CanEncrypt(_kind))
        {
            password = Password1.Password;
            string? problem =
                password.Length == 0 ? "암호를 입력해 주세요." :
                password != Password2.Password ? "두 암호가 다릅니다. 다시 확인해 주세요." :
                _kind == ArchiveKind.Zip && !IsAsciiPassword(password) ? "ZIP 암호에는 영문, 숫자, 기호만 쓸 수 있습니다.\n한글 암호를 쓰려면 형식을 7Z로 바꿔 주세요." :
                null;
            if (problem is not null)
            {
                MessageBox.Show(this, problem, "압축하기", MessageBoxButton.OK, MessageBoxImage.Warning);
                Password1.Focus();
                return;
            }
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

        _result = new CompressRequest(dest, new CompressOptions
        {
            Kind = _kind,
            Level = level,
            Password = password,
            EncryptFileNames = password is not null && EncryptNames.IsChecked == true,
        });
        Close();
    }
}
