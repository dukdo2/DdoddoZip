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
        Assert.False(session.Test().HasProblems);
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
