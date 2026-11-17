using DdoddoZip.Core;

namespace DdoddoZip.Tests;

public class QuickArchiveTests
{
    [Fact]
    public void Quick7zUsesSelectionNameAndNeverOverwrites()
    {
        using var dir = new TempDir();
        var folder = SampleData.CreateSampleTree(dir);

        var first = Compressor.QuickArchive([folder], ArchiveKind.SevenZip);
        Assert.Equal(dir["자료.7z"], first.OutputPath);
        var second = Compressor.QuickArchive([folder], ArchiveKind.SevenZip);
        Assert.Equal(dir["자료 (2).7z"], second.OutputPath);

        using var session = ArchiveSession.Open(first.OutputPath);
        Assert.Equal("7Z", session.FormatName);
        var test = session.Test();
        Assert.False(test.HasProblems);
        Assert.Equal(4, test.FilesTested); // files only, folders are not counted
    }

    [Theory]
    [InlineData(ArchiveKind.Zip)]
    [InlineData(ArchiveKind.SevenZip)]
    public void ExtractKeepsTimesAndReadOnlyForManyFiles(ArchiveKind kind)
    {
        using var dir = new TempDir();
        var src = Directory.CreateDirectory(dir["src"]).FullName;
        var when = new DateTime(2020, 5, 6, 7, 8, 10, DateTimeKind.Utc);
        for (int i = 0; i < 300; i++)
        {
            var f = Path.Combine(src, $"sub{i % 5}", $"f{i}.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, new string('x', i));
            File.SetLastWriteTimeUtc(f, when.AddMinutes(i));
        }
        File.SetAttributes(Path.Combine(src, "sub0", "f0.txt"), FileAttributes.ReadOnly);

        var arc = dir[kind == ArchiveKind.Zip ? "t.zip" : "t.7z"];
        Compressor.Compress([src], arc, new CompressOptions { Kind = kind });
        using var session = ArchiveSession.Open(arc);
        var result = session.Extract(new ExtractOptions { Destination = dir["out"], FolderMode = FolderMode.Never });

        Assert.False(result.HasProblems);
        Assert.Equal(300, result.FilesWritten);
        for (int i = 0; i < 300; i++)
        {
            var f = Path.Combine(dir["out"], "src", $"sub{i % 5}", $"f{i}.txt");
            Assert.Equal(i, new FileInfo(f).Length);
            Assert.Equal(when.AddMinutes(i), File.GetLastWriteTimeUtc(f));
        }
        var ro = Path.Combine(dir["out"], "src", "sub0", "f0.txt");
        Assert.True(File.GetAttributes(ro).HasFlag(FileAttributes.ReadOnly));
        File.SetAttributes(ro, FileAttributes.Normal); // so TempDir can delete it
        File.SetAttributes(Path.Combine(src, "sub0", "f0.txt"), FileAttributes.Normal);
    }

    [Fact]
    public void CompressSkipsFolderLinksButKeepsRealFolders()
    {
        using var dir = new TempDir();
        var src = Directory.CreateDirectory(dir["src"]).FullName;
        Directory.CreateDirectory(Path.Combine(src, "real"));
        File.WriteAllText(Path.Combine(src, "real", "a.txt"), "a");
        File.WriteAllText(Path.Combine(src, "b.txt"), "b");

        // A junction that points back at its own parent would loop forever if followed.
        var link = Path.Combine(src, "loop");
        var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{src}\"")
            { CreateNoWindow = true, UseShellExecute = false })!;
        mk.WaitForExit();
        Assert.True(Directory.Exists(link), "junction was not created");

        try
        {
            Assert.Equal((2L, 2L), Compressor.Measure([src]));
            var result = Compressor.Compress([src], dir["t.zip"], new CompressOptions());
            Assert.Equal(2, result.FileCount);
            using var session = ArchiveSession.Open(dir["t.zip"]);
            Assert.Contains(session.Entries, e => e.Path == @"src\real\a.txt");
            Assert.DoesNotContain(session.Entries, e => e.Path.Contains("loop"));
        }
        finally
        {
            Directory.Delete(link); // removes the junction only, not what it points at
        }
    }

    [Fact]
    public void TarGzOpensAndExtracts()
    {
        using var dir = new TempDir();
        var folder = SampleData.CreateSampleTree(dir);
        Compressor.Compress([folder], dir["t.tar.gz"], new CompressOptions { Kind = ArchiveKind.TarGz });

        var threadsBefore = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;
        for (int i = 0; i < 5; i++)
        {
            using var session = ArchiveSession.Open(dir["t.tar.gz"]);
            Assert.Equal("TAR.GZ", session.FormatName);
            Assert.Null(session.NameEncoding); // only ZIP has a name encoding
            Assert.False(session.Test().HasProblems);
        }
        // Opening used to leave 4 writer threads behind each time.
        Assert.True(System.Diagnostics.Process.GetCurrentProcess().Threads.Count < threadsBefore + 15);
    }

    [Fact]
    public void SessionRemembersPasswordThatWorked()
    {
        using var dir = new TempDir();
        var f = dir.WriteText("a.txt", "비밀");
        Compressor.Compress([f], dir["s.7z"], new CompressOptions { Kind = ArchiveKind.SevenZip, Password = "pw1", EncryptFileNames = true });

        var ui = new FakeInteraction();
        ui.Passwords.Enqueue("pw1");
        using var session = ArchiveSession.Open(dir["s.7z"], null, ui);
        Assert.Equal("pw1", session.Password);

        // A second session given that password does not ask again.
        var ui2 = new FakeInteraction();
        using var again = ArchiveSession.Open(dir["s.7z"], new ArchiveOpenOptions { Password = session.Password }, ui2);
        Assert.Equal(0, ui2.PasswordQuestions);
        Assert.False(again.Extract(new ExtractOptions { Destination = dir["x"] }, ui2).HasProblems);
    }
}
