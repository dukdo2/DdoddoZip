using System.Diagnostics;
using System.Text;
using DdoddoZip.Core;

namespace DdoddoZip.Cli;

/// <summary>Asks the person at the console for passwords and overwrite decisions.</summary>
internal sealed class ConsoleInteraction : IArchiveInteraction
{
    public string? AskPassword(string archivePath, bool previousWasWrong)
    {
        if (Console.IsInputRedirected)
            return null;

        Console.Error.WriteLine();
        if (previousWasWrong)
            Console.Error.WriteLine("암호가 맞지 않습니다.");
        Console.Error.Write($"{Path.GetFileName(archivePath)} 암호 (취소: 빈 칸으로 Enter): ");

        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar))
                sb.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return sb.Length == 0 ? null : sb.ToString();
    }

    public OverwriteDecision AskOverwrite(OverwriteRequest r)
    {
        if (Console.IsInputRedirected)
            return new OverwriteDecision(OverwriteChoice.Skip);

        Console.Error.WriteLine();
        Console.Error.WriteLine($"같은 이름의 파일이 있습니다: {r.ExistingPath}");
        Console.Error.WriteLine($"  지금 있는 파일: {r.ExistingSize:N0} 바이트, {r.ExistingModified.ToLocalTime():yyyy-MM-dd HH:mm}");
        Console.Error.WriteLine($"  압축 파일 안:   {r.NewSize:N0} 바이트, {r.NewModified?.ToLocalTime():yyyy-MM-dd HH:mm}");
        Console.Error.Write("[y]덮어쓰기 [n]건너뛰기 [r]이름 바꾸기 / 모두: [A]덮어쓰기 [N]건너뛰기 [R]이름 바꾸기 / [q]취소 > ");
        while (true)
        {
            var c = Console.ReadKey(intercept: true).KeyChar;
            OverwriteDecision? d = c switch
            {
                'y' => new(OverwriteChoice.Overwrite),
                'n' => new(OverwriteChoice.Skip),
                'r' => new(OverwriteChoice.Rename),
                'A' => new(OverwriteChoice.Overwrite, true),
                'N' => new(OverwriteChoice.Skip, true),
                'R' => new(OverwriteChoice.Rename, true),
                'q' => new(OverwriteChoice.Cancel),
                _ => null,
            };
            if (d is { } decision)
            {
                Console.Error.WriteLine(c);
                return decision;
            }
        }
    }
}

/// <summary>One-line progress bar, redrawn at most 10 times a second.</summary>
internal sealed class ConsoleProgress(string label) : IProgress<ArchiveProgress>, IDisposable
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastDraw = -1000;
    private bool _drawn;

    public void Report(ArchiveProgress p)
    {
        if (Console.IsErrorRedirected) return;
        var now = _clock.ElapsedMilliseconds;
        if (now - _lastDraw < 100 && p.Fraction < 1) return;
        _lastDraw = now;

        const int width = 30;
        int filled = (int)(p.Fraction * width);
        var name = p.CurrentItem ?? "";
        if (name.Length > 30) name = "…" + name[^29..];
        var line = $"\r{label} [{new string('#', filled)}{new string('.', width - filled)}] {p.Fraction * 100,5:N1}%  {name}";
        int max = Math.Max(20, Console.BufferWidth - 1);
        Console.Error.Write(line.Length > max ? line[..max] : line.PadRight(max));
        _drawn = true;
    }

    public void Finish()
    {
        if (_drawn)
        {
            Console.Error.WriteLine();
            _drawn = false;
        }
    }

    public void Dispose() => Finish();
}
