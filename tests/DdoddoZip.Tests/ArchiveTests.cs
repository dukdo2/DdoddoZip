using System.IO.Compression;
using System.Text;
using DdoddoZip.Core;

namespace DdoddoZip.Tests;

public class RoundTripTests
{
    [Theory]
    [InlineData(ArchiveKind.Zip)]
    [InlineData(ArchiveKind.SevenZip)]
    [InlineData(ArchiveKind.Tar)]
    [InlineData(ArchiveKind.TarGz)]
    [InlineData(ArchiveKind.TarXz)]
    [InlineData(ArchiveKind.TarBz2)]
    public void CompressThenExtractGivesIdenticalFiles(ArchiveKind kind)
    {
        using var dir = new TempDir();
        var source = SampleData.CreateSampleTree(dir);
        var archive = dir["out" + ArchiveKindInfo.Extension(kind)];

        var made = Compressor.Compress([source], archive, new CompressOptions { Kind = kind });
        Assert.True(File.Exists(archive));
        Assert.Equal(4, made.FileCount);

        using var session = ArchiveSession.Open(archive);
        Assert.Equal(ArchiveKindInfo.DisplayName(kind), session.FormatName);
        Assert.Contains(session.Entries, e => e.Path == "자료\\한글 문서.txt");
        Assert.Contains(session.Entries, e => e.Path == "자료\\빈 폴더" && e.IsDirectory);

        var result = session.Extract(new ExtractOptions { Destination = dir["x"] });
        Assert.False(result.HasProblems);
        Assert.Equal(dir["x"], result.OutputDirectory); // single top folder → no extra folder (Auto)
        Assert.Equal(SampleData.Snapshot(dir.Path + "\\자료"), SampleData.Snapshot(dir["x\\자료"]));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.ddz-tmp"));
    }

    [Theory]
    [InlineData(CompressLevel.Store)]
    [InlineData(CompressLevel.Fast)]
    [InlineData(CompressLevel.Maximum)]
    public void AllZipLevelsWork(CompressLevel level)
    {
        using var dir = new TempDir();
        var source = SampleData.CreateSampleTree(dir);
        Compressor.Compress([source], dir["a.zip"], new CompressOptions { Level = level });
        using var session = ArchiveSession.Open(dir["a.zip"]);
        Assert.False(session.Test().HasProblems);
    }

    [Fact]
    public void QuickZipNamesAfterSelectionAndNeverOverwrites()
    {
        using var dir = new TempDir();
        var a = dir.WriteText("docs\\a.txt", "a");
        var b = dir.WriteText("docs\\b.txt", "b");

        var first = Compressor.QuickZip([a, b]);
        Assert.Equal(dir["docs\\docs.zip"], first.OutputPath);

        var second = Compressor.QuickZip([a, b]);
        Assert.Equal(dir["docs\\docs (2).zip"], second.OutputPath);

        var single = Compressor.QuickZip([a]);
        Assert.Equal(dir["docs\\a.zip"], single.OutputPath);
    }

    [Fact]
    public void ExtractCreatesArchiveFolderWhenItemsAreLoose()
    {
        using var dir = new TempDir();
        var a = dir.WriteText("src\\a.txt", "a");
        var b = dir.WriteText("src\\b.txt", "b");
        Compressor.Compress([a, b], dir["loose.zip"], new CompressOptions());

        using var session = ArchiveSession.Open(dir["loose.zip"]);
        var result = session.Extract(new ExtractOptions { Destination = dir["x"] });
        Assert.Equal(dir["x\\loose"], result.OutputDirectory);
        Assert.True(File.Exists(dir["x\\loose\\a.txt"]));
    }

    [Fact]
    public void ExtractSelectedItemsRelativeToBaseFolder()
    {
        using var dir = new TempDir();
        var source = SampleData.CreateSampleTree(dir);
        Compressor.Compress([source], dir["a.7z"], new CompressOptions { Kind = ArchiveKind.SevenZip });

        using var session = ArchiveSession.Open(dir["a.7z"]);
        var csv = session.Entries.Single(e => e.Path == "자료\\하위 폴더\\data.csv");
        var result = session.Extract(new ExtractOptions
        {
            Destination = dir["x"],
            Indices = [csv.Index],
            BaseFolder = "자료\\하위 폴더",
            FolderMode = FolderMode.Never,
        });
        Assert.Equal(1, result.FilesWritten);
        Assert.True(File.Exists(dir["x\\data.csv"]));
    }
}

public class SafetyAndErrorTests
{
    static SafetyAndErrorTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [Fact]
    public void ZipSlipEntryIsBlockedAndOthersStillExtract()
    {
        using var dir = new TempDir();
        var zip = dir["evil.zip"];
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            WriteEntry(z, "../../escaped.txt", "bad");
            WriteEntry(z, "ok.txt", "good");
        }

        using var session = ArchiveSession.Open(zip);
        var result = session.Extract(new ExtractOptions { Destination = dir["x\\y"], FolderMode = FolderMode.Never });

        Assert.Single(result.BlockedPaths);
        Assert.Equal(1, result.FilesWritten);
        Assert.True(File.Exists(dir["x\\y\\ok.txt"]));
        Assert.False(File.Exists(dir["escaped.txt"]));
        Assert.False(File.Exists(dir["x\\escaped.txt"]));
    }

    [Fact]
    public void OldKoreanZipNamesAreReadCorrectly()
    {
        using var dir = new TempDir();
        var zip = dir["cp949.zip"];
        using (var fs = File.Create(zip))
        using (var z = new ZipArchive(fs, ZipArchiveMode.Create, false, Encoding.GetEncoding(949)))
        {
            WriteEntry(z, "보고서/한글 파일.txt", "내용");
        }

        using var session = ArchiveSession.Open(zip);
        Assert.Equal(NameEncodingKind.Legacy, session.NameEncoding!.Kind);
        Assert.Equal(949, session.NameEncoding.CodePage);
        Assert.Contains(session.Entries, e => e.Path == "보고서\\한글 파일.txt");

        session.Extract(new ExtractOptions { Destination = dir["x"], FolderMode = FolderMode.Never });
        Assert.Equal("내용", File.ReadAllText(dir["x\\보고서\\한글 파일.txt"]));
    }

    [Fact]
    public void NotAnArchiveGivesClearError()
    {
        using var dir = new TempDir();
        var f = dir.WriteText("note.txt", "그냥 글자입니다");
        var e = Assert.Throws<ArchiveException>(() => ArchiveSession.Open(f));
        Assert.Equal(ArchiveErrorKind.NotArchive, e.Kind);
    }

    [Fact]
    public void TruncatedArchiveDoesNotCrash()
    {
        using var dir = new TempDir();
        var source = SampleData.CreateSampleTree(dir);
        Compressor.Compress([source], dir["a.7z"], new CompressOptions { Kind = ArchiveKind.SevenZip });
        var bytes = File.ReadAllBytes(dir["a.7z"]);
        File.WriteAllBytes(dir["cut.7z"], bytes[..(bytes.Length / 2)]);

        try
        {
            using var session = ArchiveSession.Open(dir["cut.7z"]);
            var result = session.Extract(new ExtractOptions { Destination = dir["x"] });
            Assert.True(result.HasProblems);
        }
        catch (ArchiveException e)
        {
            Assert.NotEqual(ArchiveErrorKind.Unknown, e.Kind);
        }
    }

    [Fact]
    public void CorruptedDataIsReportedPerFile()
    {
        using var dir = new TempDir();
        dir.WriteText("src\\a.txt", string.Concat(Enumerable.Repeat("또또집 ", 20000)));
        Compressor.Compress([dir["src\\a.txt"]], dir["a.zip"], new CompressOptions());
        var bytes = File.ReadAllBytes(dir["a.zip"]);
        for (int i = 200; i < 260; i++) bytes[i] ^= 0xFF; // damage compressed data
        File.WriteAllBytes(dir["bad.zip"], bytes);

        using var session = ArchiveSession.Open(dir["bad.zip"]);
        var result = session.Extract(new ExtractOptions { Destination = dir["x"], FolderMode = FolderMode.Never });
        Assert.Single(result.Failures);
        Assert.Equal(ArchiveErrorKind.Corrupted, result.Failures[0].Kind);
        Assert.False(File.Exists(dir["x\\a.txt"])); // broken output is removed
    }

    [Fact]
    public void CancelledCompressionLeavesNothingBehind()
    {
        using var dir = new TempDir();
        var source = SampleData.CreateSampleTree(dir);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            Compressor.Compress([source], dir["a.zip"], new CompressOptions(), null, cts.Token));
        Assert.False(File.Exists(dir["a.zip"]));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.ddz-tmp"));
    }

    [Fact]
    public void CancelDuringExtractStops()
    {
        using var dir = new TempDir();
        var source = SampleData.CreateSampleTree(dir);
        Compressor.Compress([source], dir["a.zip"], new CompressOptions());
        using var session = ArchiveSession.Open(dir["a.zip"]);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(_ => cts.Cancel());
        Assert.ThrowsAny<OperationCanceledException>(() =>
            session.Extract(new ExtractOptions { Destination = dir["x"] }, null, progress, cts.Token));
    }

    private static void WriteEntry(ZipArchive z, string name, string text)
    {
        using var w = new StreamWriter(z.CreateEntry(name).Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    private sealed class SyncProgress(Action<ArchiveProgress> onReport) : IProgress<ArchiveProgress>
    {
        public void Report(ArchiveProgress value) => onReport(value);
    }
}

public class PasswordAndOverwriteTests
{
    [Theory]
    [InlineData(ArchiveKind.Zip, false, "Ddo1234!")]
    [InlineData(ArchiveKind.SevenZip, false, "또또1234")]
    [InlineData(ArchiveKind.SevenZip, true, "또또1234")] // file names hidden too
    public void WrongPasswordThenRightPassword(ArchiveKind kind, bool encryptNames, string password)
    {
        using var dir = new TempDir();
        var source = SampleData.CreateSampleTree(dir);
        var archive = dir["secret" + ArchiveKindInfo.Extension(kind)];
        Compressor.Compress([source], archive, new CompressOptions { Kind = kind, Password = password, EncryptFileNames = encryptNames });

        var ui = new FakeInteraction();
        ui.Passwords.Enqueue("wrong-password");
        ui.Passwords.Enqueue(password);

        using var session = ArchiveSession.Open(archive, null, ui);
        Assert.True(session.HasEncryptedEntries || encryptNames);
        var result = session.Extract(new ExtractOptions { Destination = dir["x"] }, ui);

        Assert.False(result.HasProblems);
        Assert.Equal(2, ui.PasswordQuestions);
        Assert.Equal(SampleData.Snapshot(source), SampleData.Snapshot(dir["x\\자료"]));
    }

    [Fact]
    public void KoreanZipPasswordIsRejectedWithAdvice()
    {
        using var dir = new TempDir();
        var f = dir.WriteText("a.txt", "x");
        var e = Assert.Throws<ArchiveException>(() =>
            Compressor.Compress([f], dir["s.zip"], new CompressOptions { Password = "한글암호" }));
        Assert.Equal(ArchiveErrorKind.Unsupported, e.Kind);
        Assert.Contains("7Z", e.Message);
    }

    [Fact]
    public void NoPasswordGivenReportsWrongPassword()
    {
        using var dir = new TempDir();
        var f = dir.WriteText("a.txt", "비밀");
        Compressor.Compress([f], dir["s.zip"], new CompressOptions { Password = "pw" });

        using var session = ArchiveSession.Open(dir["s.zip"]);
        var result = session.Extract(new ExtractOptions { Destination = dir["x"], Password = "nope", FolderMode = FolderMode.Never });
        Assert.Single(result.Failures);
        Assert.Equal(ArchiveErrorKind.WrongPassword, result.Failures[0].Kind);
    }

    [Theory]
    [InlineData(OverwriteMode.Skip, "old", 1)]
    [InlineData(OverwriteMode.Overwrite, "new", 1)]
    [InlineData(OverwriteMode.Rename, "old", 2)]
    public void OverwriteModes(OverwriteMode mode, string expectedText, int expectedFiles)
    {
        using var dir = new TempDir();
        var f = dir.WriteText("src\\a.txt", "new");
        Compressor.Compress([f], dir["a.zip"], new CompressOptions());
        dir.WriteText("x\\a.txt", "old");

        using var session = ArchiveSession.Open(dir["a.zip"]);
        session.Extract(new ExtractOptions { Destination = dir["x"], Overwrite = mode, FolderMode = FolderMode.Never });

        Assert.Equal(expectedText, File.ReadAllText(dir["x\\a.txt"]));
        Assert.Equal(expectedFiles, Directory.GetFiles(dir["x"]).Length);
        if (mode == OverwriteMode.Rename)
            Assert.Equal("new", File.ReadAllText(dir["x\\a (2).txt"]));
    }

    [Fact]
    public void AskOverwriteApplyToAllAsksOnce()
    {
        using var dir = new TempDir();
        var a = dir.WriteText("src\\a.txt", "new");
        var b = dir.WriteText("src\\b.txt", "new");
        Compressor.Compress([a, b], dir["ab.zip"], new CompressOptions());
        dir.WriteText("x\\a.txt", "old");
        dir.WriteText("x\\b.txt", "old");

        var ui = new FakeInteraction { Overwrite = new OverwriteDecision(OverwriteChoice.Overwrite, ApplyToAll: true) };
        using var session = ArchiveSession.Open(dir["ab.zip"]);
        session.Extract(new ExtractOptions { Destination = dir["x"], FolderMode = FolderMode.Never }, ui);

        Assert.Equal(1, ui.OverwriteQuestions);
        Assert.Equal("new", File.ReadAllText(dir["x\\a.txt"]));
        Assert.Equal("new", File.ReadAllText(dir["x\\b.txt"]));
    }
}
