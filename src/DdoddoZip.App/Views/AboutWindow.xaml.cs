using System.Windows;
using DdoddoZip.App.Services;

namespace DdoddoZip.App.Views;

/// <summary>A1: version, update check, donation, links.</summary>
public partial class AboutWindow : Window
{
    private const string ReportMail = "dukdo123@naver.com"; // the address the owner published in the 1.0.0 release notes

    private readonly Func<IReadOnlyList<ReleaseInfo>, Task> _startUpdate;
    private List<ReleaseInfo>? _newer;
    private CancellationTokenSource? _checking;

    private AboutWindow(Func<IReadOnlyList<ReleaseInfo>, Task> startUpdate)
    {
        InitializeComponent();
        _startUpdate = startUpdate;
        VersionText.Text = $"버전 {UpdateService.Current.ToString(3)} · 광고 없는 가벼운 압축 프로그램";
        AutoCheck.IsChecked = AppSettings.Current.AutoCheckUpdates;

        KakaoButton.Visibility = SupportLinks.KakaoPay is null ? Visibility.Collapsed : Visibility.Visible;
        SponsorsButton.Visibility = SupportLinks.GitHubSponsors is null ? Visibility.Collapsed : Visibility.Visible;
        SupportBox.Visibility = SupportLinks.Any ? Visibility.Visible : Visibility.Collapsed;
        SupportButtons.Columns = SupportLinks.KakaoPay is not null && SupportLinks.GitHubSponsors is not null ? 2 : 1;

        Closed += (_, _) => _checking?.Cancel();
    }

    /// <param name="newer">Releases already found by the startup check, or null to check now.</param>
    /// <param name="focusSupport">Opened from [♥ 후원]: put the keyboard focus on the donation buttons.</param>
    public static void Show(Window owner, IReadOnlyList<ReleaseInfo>? newer, bool focusSupport,
                            Func<IReadOnlyList<ReleaseInfo>, Task> startUpdate)
    {
        var w = new AboutWindow(startUpdate) { Owner = owner };
        w.Loaded += async (_, _) =>
        {
            if (focusSupport && SupportLinks.Any)
                (SupportLinks.KakaoPay is not null ? w.KakaoButton : w.SponsorsButton).Focus();
            if (newer is { Count: > 0 }) w.ShowNewer(newer.ToList());
            else await w.CheckAsync();
        };
        w.ShowDialog();
    }

    private async Task CheckAsync()
    {
        _checking = new CancellationTokenSource();
        SetStatus("확인하는 중…", highlight: false);
        UpdateButton.Content = "업데이트 확인";
        UpdateButton.IsEnabled = false;
        NotesLink.Visibility = Visibility.Collapsed;
        try
        {
            var newer = await UpdateService.GetNewerAsync(_checking.Token);
            AppSettings.Current.LastUpdateCheckUtc = DateTime.UtcNow;
            AppSettings.Current.Save();
            if (newer.Count > 0) ShowNewer(newer);
            else SetStatus("최신 버전입니다.", highlight: false);
        }
        catch (UpdateException)
        {
            SetStatus("확인하지 못했습니다. 인터넷 연결을 확인해 주세요.", highlight: false);
            UpdateButton.Content = "다시 확인";
        }
        catch (OperationCanceledException)
        {
            return; // window closed
        }
        catch (System.Text.Json.JsonException)
        {
            SetStatus("확인하지 못했습니다. 잠시 후 다시 시도해 주세요.", highlight: false);
            UpdateButton.Content = "다시 확인";
        }
        UpdateButton.IsEnabled = true;
    }

    private void ShowNewer(List<ReleaseInfo> newer)
    {
        _newer = newer;
        SetStatus($"새 버전이 나왔습니다: {newer[0].Display}", highlight: true);
        NotesLink.Visibility = Visibility.Visible;
        UpdateButton.Content = "업데이트";
        UpdateButton.Style = (Style)FindResource("SmallPrimaryButton");
        UpdateButton.IsEnabled = true;
    }

    private void SetStatus(string text, bool highlight)
    {
        UpdateStatus.Text = text;
        UpdateStatus.Foreground = (System.Windows.Media.Brush)FindResource(highlight ? "AccentDark" : "Sub");
        UpdateStatus.FontWeight = highlight ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_newer is { Count: > 0 } newer)
        {
            Close();
            await _startUpdate(newer);
        }
        else
        {
            await CheckAsync();
        }
    }

    private void NotesLink_Click(object sender, RoutedEventArgs e)
    {
        if (_newer is not { Count: > 0 } newer) return;
        if (NotesWindow.AskToUpdate(this, newer))
        {
            Close();
            _ = _startUpdate(newer);
        }
    }

    private void AutoCheck_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.AutoCheckUpdates = AutoCheck.IsChecked == true;
        AppSettings.Current.Save();
    }

    private void Kakao_Click(object sender, RoutedEventArgs e) => OpenUrl(SupportLinks.KakaoPay);
    private void Sponsors_Click(object sender, RoutedEventArgs e) => OpenUrl(SupportLinks.GitHubSponsors);
    private void Repo_Click(object sender, RoutedEventArgs e) => OpenUrl(UpdateService.RepositoryUrl);

    private void License_Click(object sender, RoutedEventArgs e)
    {
        var local = Path.Combine(AppContext.BaseDirectory, "LICENSE.txt");
        OpenUrl(File.Exists(local) ? local : UpdateService.RepositoryUrl + "/blob/main/LICENSE.txt");
    }

    private void Report_Click(object sender, RoutedEventArgs e) =>
        OpenUrl($"mailto:{ReportMail}?subject={Uri.EscapeDataString($"[또또집 {UpdateService.Current.ToString(3)}] 문제 신고")}");

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenUrl(string? url)
    {
        if (url is null) return;
        try { ShellNative.OpenWithShell(url); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            ErrorWindow.Show(this, "열 수 없습니다", "링크를 열 프로그램이 없습니다.", $"{url}\n{ex.Message}");
        }
    }
}
