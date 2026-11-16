using System.Diagnostics;
using System.Windows;
using System.Windows.Shell;
using System.Windows.Threading;
using DdoddoZip.App.Services;
using DdoddoZip.Core;

namespace DdoddoZip.App.Views;

/// <summary>S5: progress, speed, time left, cancel. Report() may be called from any thread.</summary>
public partial class ProgressWindow : Window, IProgress<ArchiveProgress>
{
    private readonly string _title;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lock = new();
    private ArchiveProgress _latest;
    private bool _hasNew;
    private bool _closingByCode;
    private bool _cancelRequested;

    // speed is averaged over a sliding window of recent samples
    private readonly Queue<(double Seconds, ulong Bytes)> _samples = new();

    public event Action? CancelRequested;

    public ProgressWindow(string title)
    {
        InitializeComponent();
        _title = title;
        Title = title;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        _timer.Start();
        Closing += (_, e) =>
        {
            if (_closingByCode) return;
            e.Cancel = true;
            AskCancel();
        };
    }

    public void Report(ArchiveProgress value)
    {
        lock (_lock)
        {
            _latest = value;
            _hasNew = true;
        }
    }

    private void Refresh()
    {
        ArchiveProgress p;
        lock (_lock)
        {
            if (!_hasNew) return;
            p = _latest;
            _hasNew = false;
        }

        var percent = p.Fraction * 100;
        Title = $"{_title} ({percent:N0}%)";
        Bar.Value = p.Fraction * 1000;
        Taskbar.ProgressValue = p.Fraction;
        if (!string.IsNullOrEmpty(p.CurrentItem))
            CurrentItem.Text = Path.GetFileName(p.CurrentItem);
        SizeText.Text = p.TotalBytes > 0 ? $"{Fmt.Bytes(p.CompletedBytes)} / {Fmt.Bytes(p.TotalBytes)}" : "";

        var now = _clock.Elapsed.TotalSeconds;
        _samples.Enqueue((now, p.CompletedBytes));
        while (_samples.Count > 2 && now - _samples.Peek().Seconds > 3) _samples.Dequeue();
        var first = _samples.Peek();
        var seconds = now - first.Seconds;
        if (seconds > 0.5 && p.CompletedBytes > first.Bytes)
        {
            var speed = (p.CompletedBytes - first.Bytes) / seconds;
            var left = p.TotalBytes > p.CompletedBytes ? TimeSpan.FromSeconds((p.TotalBytes - p.CompletedBytes) / speed) : TimeSpan.Zero;
            SpeedText.Text = $"{Fmt.Bytes((ulong)speed)}/s · 남은 시간 {Fmt.Duration(left)}";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => AskCancel();

    private void AskCancel()
    {
        if (_cancelRequested) return;
        var answer = MessageBox.Show(this, "작업을 취소할까요?\n만들던 압축 파일은 지워집니다.", "또또집",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        _cancelRequested = true;
        CancelButton.IsEnabled = false;
        CancelButton.Content = "취소하는 중…";
        Taskbar.ProgressState = TaskbarItemProgressState.Paused;
        CancelRequested?.Invoke();
    }

    public void CloseQuietly()
    {
        if (_closingByCode) return;
        _closingByCode = true;
        _timer.Stop();
        Close();
    }
}
