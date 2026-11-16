using System.Security.Cryptography;
using DdoddoZip.Core;

namespace DdoddoZip.Tests;

/// <summary>Temporary folder deleted after the test.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ddz-test-" + Guid.NewGuid().ToString("N")[..8]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string this[string relative] => System.IO.Path.Combine(Path, relative);

    public string WriteFile(string relative, byte[] content)
    {
        var full = this[relative];
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public string WriteText(string relative, string text) => WriteFile(relative, System.Text.Encoding.UTF8.GetBytes(text));

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Scripted answers for password / overwrite questions.</summary>
internal sealed class FakeInteraction : IArchiveInteraction
{
    public Queue<string?> Passwords { get; } = new();
    public OverwriteDecision Overwrite { get; set; } = new(OverwriteChoice.Skip);
    public int PasswordQuestions { get; private set; }
    public int OverwriteQuestions { get; private set; }

    public string? AskPassword(string archivePath, bool previousWasWrong)
    {
        PasswordQuestions++;
        return Passwords.Count > 0 ? Passwords.Dequeue() : null;
    }

    public OverwriteDecision AskOverwrite(OverwriteRequest request)
    {
        OverwriteQuestions++;
        return Overwrite;
    }
}

internal static class SampleData
{
    /// <summary>A folder with Korean names, nesting, an empty folder, an empty file and random data.</summary>
    public static string CreateSampleTree(TempDir dir, string name = "자료")
    {
        var root = dir[name];
        dir.WriteText($"{name}\\한글 문서.txt", "안녕하세요, 또또집입니다.");
        dir.WriteText($"{name}\\하위 폴더\\data.csv", string.Concat(Enumerable.Repeat("a,b,c\n", 5000)));
        dir.WriteFile($"{name}\\하위 폴더\\random.bin", RandomNumberGenerator.GetBytes(300_000));
        dir.WriteFile($"{name}\\빈 파일.txt", []);
        Directory.CreateDirectory(System.IO.Path.Combine(root, "빈 폴더"));
        return root;
    }

    /// <summary>Relative path → SHA-256 of every file and "&lt;dir&gt;" for every folder under root.</summary>
    public static Dictionary<string, string> Snapshot(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            result[System.IO.Path.GetRelativePath(root, d)] = "<dir>";
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            result[System.IO.Path.GetRelativePath(root, f)] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)));
        return result;
    }
}
