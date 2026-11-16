using DdoddoZip.Core;

namespace DdoddoZip.Tests;

public class PathGuardTests
{
    [Theory]
    [InlineData("a/b/c.txt", "a\\b\\c.txt")]
    [InlineData("./a/./b.txt", "a\\b.txt")]
    [InlineData("/etc/passwd", "etc\\passwd")]
    [InlineData("C:\\Windows\\evil.dll", "Windows\\evil.dll")]
    [InlineData("a:b.txt", "a_b.txt")]            // ':' would create an alternate data stream
    [InlineData("CON.txt", "_CON.txt")]           // reserved device name
    [InlineData("nul", "_nul")]
    [InlineData("name. ", "name__")]              // Windows would drop trailing dot/space
    [InlineData("폴더/파일?.txt", "폴더\\파일_.txt")]
    public void Sanitizes(string input, string expected)
    {
        Assert.Equal(expected, PathGuard.SanitizeRelative(input, out _));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("..\\..\\Windows\\evil.dll")]
    [InlineData("")]
    [InlineData("/")]
    public void BlocksEscapes(string input)
    {
        Assert.Null(PathGuard.SanitizeRelative(input, out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void ResolveStaysInsideRoot()
    {
        using var dir = new TempDir();
        Assert.Equal(dir["a\\b.txt"], PathGuard.Resolve(dir.Path, "a/b.txt", out _));
        Assert.Null(PathGuard.Resolve(dir.Path, "../x.txt", out _));
    }
}

public class NamePolicyTests
{
    [Fact]
    public void SingleFileUsesNameWithoutExtension()
    {
        using var dir = new TempDir();
        var f = dir.WriteText("01_기획서.md", "x");
        Assert.Equal("01_기획서", NamePolicy.QuickArchiveBaseName([f]));
    }

    [Fact]
    public void SingleFolderUsesFolderName()
    {
        using var dir = new TempDir();
        var d = dir["docs"];
        Directory.CreateDirectory(d);
        Assert.Equal("docs", NamePolicy.QuickArchiveBaseName([d]));
    }

    [Fact]
    public void SeveralItemsUseParentFolderName()
    {
        using var dir = new TempDir();
        var a = dir.WriteText("docs\\a.txt", "a");
        var b = dir.WriteText("docs\\b.txt", "b");
        Assert.Equal("docs", NamePolicy.QuickArchiveBaseName([a, b]));
    }

    [Fact]
    public void UniquePathAddsNumber()
    {
        using var dir = new TempDir();
        Assert.Equal(dir["docs.zip"], NamePolicy.GetUniquePath(dir.Path, "docs", ".zip"));
        dir.WriteText("docs.zip", "x");
        Assert.Equal(dir["docs (2).zip"], NamePolicy.GetUniquePath(dir.Path, "docs", ".zip"));
        dir.WriteText("docs (2).zip", "x");
        Assert.Equal(dir["docs (3).zip"], NamePolicy.GetUniquePath(dir.Path, "docs", ".zip"));
    }

    [Theory]
    [InlineData("사진.zip", "사진")]
    [InlineData("backup.tar.gz", "backup")]
    [InlineData("movie.part1.rar", "movie")]
    [InlineData("a.b.7z", "a.b")]
    public void ArchiveBaseName(string file, string expected)
    {
        Assert.Equal(expected, NamePolicy.ArchiveBaseName(file));
    }
}

public class EncodingDetectorTests
{
    private static (ushort, byte[]) Entry(ushort flags, byte[] name) => (flags, name);

    [Fact]
    public void AsciiNamesNeedNothing()
    {
        var info = EncodingDetector.Analyze([Entry(0, "readme.txt"u8.ToArray())]);
        Assert.Equal(NameEncodingKind.Ascii, info.Kind);
        Assert.Null(info.CodePage);
    }

    [Fact]
    public void FlaggedUtf8IsTrusted()
    {
        var info = EncodingDetector.Analyze([Entry(0x0800, "한글.txt"u8.ToArray())]);
        Assert.Equal(NameEncodingKind.Utf8, info.Kind);
        Assert.Null(info.CodePage);
    }

    [Fact]
    public void UnflaggedUtf8IsDetected()
    {
        var info = EncodingDetector.Analyze([Entry(0, "한글.txt"u8.ToArray())]);
        Assert.Equal(NameEncodingKind.Utf8, info.Kind);
        Assert.Equal(65001, info.CodePage);
    }

    [Fact]
    public void Cp949IsDetected()
    {
        // "한글" in CP949
        var info = EncodingDetector.Analyze([Entry(0, [0xC7, 0xD1, 0xB1, 0xDB, (byte)'.', (byte)'t'])]);
        Assert.Equal(NameEncodingKind.Legacy, info.Kind);
        Assert.Equal(949, info.CodePage);
    }
}
