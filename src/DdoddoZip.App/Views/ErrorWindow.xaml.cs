using System.Text;
using System.Windows;
using DdoddoZip.Core;

namespace DdoddoZip.App.Views;

/// <summary>S8: what went wrong in plain words, plus copyable details.</summary>
public partial class ErrorWindow : Window
{
    private ErrorWindow()
    {
        InitializeComponent();
    }

    public static void Show(Window? owner, string title, string headline, string? details = null, string? advice = null)
    {
        var w = new ErrorWindow { Title = title };
        if (owner is { IsVisible: true }) w.Owner = owner;
        else { w.WindowStartupLocation = WindowStartupLocation.CenterScreen; w.ShowInTaskbar = true; }
        w.Headline.Text = headline;
        w.Advice.Text = advice ?? "";
        w.Advice.Visibility = string.IsNullOrEmpty(advice) ? Visibility.Collapsed : Visibility.Visible;
        w.Details.Text = details ?? "";
        w.DetailsButton.Visibility = string.IsNullOrEmpty(details) ? Visibility.Collapsed : Visibility.Visible;
        w.ShowDialog();
    }

    public static void ShowException(Window? owner, Exception e) =>
        Show(owner, "또또집", "예상하지 못한 오류가 났습니다.", e.ToString(), "같은 문제가 계속되면 [자세히] 내용을 복사해 알려 주세요.");

    /// <summary>Summary after an extract/test that finished with some failed or blocked files.</summary>
    public static void ShowProblems(Window? owner, string title, ExtractResult r, int totalFiles)
    {
        var failed = r.Failures.Count + r.BlockedPaths.Count;
        string headline, advice;
        if (r.Failures.Count > 0 && r.Failures.All(f => f.Kind == ArchiveErrorKind.WrongPassword))
        {
            headline = $"암호가 맞지 않아 파일 {r.Failures.Count:N0}개를 풀지 못했습니다.";
            advice = "암호를 확인한 뒤 다시 시도해 주세요.";
        }
        else if (r.Failures.Count == 0)
        {
            headline = $"위험한 경로가 들어 있는 파일 {r.BlockedPaths.Count:N0}개를 차단했습니다.";
            advice = "압축 파일 밖의 폴더에 파일을 쓰려는 항목이었습니다. 나머지 파일은 정상적으로 풀었습니다.";
        }
        else if (r.Failures.All(f => f.Kind == ArchiveErrorKind.Io))
        {
            // Disk-side trouble (no permission, name too long, file in use): the archive itself is fine.
            headline = $"파일 {r.Failures.Count:N0}개를 저장하지 못했습니다.";
            advice = "풀 위치에 쓸 권한이 있는지, 같은 파일이 다른 프로그램에서 열려 있지 않은지 확인해 주세요. 자세한 이유는 [자세히]에 있습니다.";
        }
        else
        {
            headline = $"압축 파일이 손상되어 파일 {failed:N0}개를 풀 수 없습니다.";
            var ok = Math.Max(0, totalFiles - failed);
            advice = $"나머지 {ok:N0}개는 정상적으로 처리했습니다. 파일을 다시 내려받아 보세요.";
        }

        var sb = new StringBuilder();
        foreach (var f in r.Failures) sb.AppendLine($"[실패] {f.Path}: {f.Message}");
        foreach (var b in r.BlockedPaths) sb.AppendLine($"[차단] {b}: 풀 위치 밖으로 나가는 경로");
        Show(owner, title, headline, sb.ToString(), advice);
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        bool show = Details.Visibility != Visibility.Visible;
        Details.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        DetailsButton.Content = show ? "자세히 ▴" : "자세히 ▾";
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
