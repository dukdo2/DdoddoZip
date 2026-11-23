using DdoddoZip.App.Services;

namespace DdoddoZip.Tests;

public class UpdateTests
{
    // Shape of GitHub's /releases answer (fields the app reads), with a draft, a pre-release,
    // a release without a setup file and one whose file is hosted elsewhere mixed in.
    private const string Releases = """
    [
      { "tag_name": "v1.2.0-beta", "draft": false, "prerelease": true, "body": "", "published_at": "2026-12-20T00:00:00Z",
        "assets": [ { "name": "DdoddoZip-1.2.0-setup.exe", "size": 1, "digest": "sha256:aa",
                      "browser_download_url": "https://github.com/dukdo2/DdoddoZip/releases/download/v1.2.0-beta/DdoddoZip-1.2.0-setup.exe" } ] },
      { "tag_name": "v1.1.2", "draft": true, "prerelease": false, "body": "", "assets": [] },
      { "tag_name": "v1.1.1", "draft": false, "prerelease": false, "published_at": "2026-12-03T05:00:00Z",
        "body": "## 바뀐 점\r\n- 7Z 압축이 **빨라졌습니다**\r\n- 큰 TAR.GZ 진행률\r\n\r\n## 설치\r\n아래 파일을 받으세요.",
        "assets": [ { "name": "DdoddoZip-1.1.1-setup.exe", "size": 46000000,
                      "digest": "sha256:20EB8B08EFD1E60652D47C6485D687DF59E87EDF0CD985B97378F34EF29E9BC8",
                      "browser_download_url": "https://github.com/dukdo2/DdoddoZip/releases/download/v1.1.1/DdoddoZip-1.1.1-setup.exe" } ] },
      { "tag_name": "v1.1.0", "draft": false, "prerelease": false, "body": "첫 업데이트 기능",
        "assets": [ { "name": "notes.txt", "size": 1, "browser_download_url": "https://github.com/dukdo2/DdoddoZip/releases/download/v1.1.0/notes.txt" } ] },
      { "tag_name": "v1.0.9", "draft": false, "prerelease": false, "body": "",
        "assets": [ { "name": "DdoddoZip-1.0.9-setup.exe", "size": 1, "digest": "sha256:bb",
                      "browser_download_url": "https://evil.example.com/DdoddoZip-1.0.9-setup.exe" } ] },
      { "tag_name": "v1.0.0", "draft": false, "prerelease": false, "body": "첫 공개",
        "assets": [ { "name": "DdoddoZip-1.0.0-setup.exe", "size": 46545822,
                      "browser_download_url": "https://github.com/dukdo2/DdoddoZip/releases/download/v1.0.0/DdoddoZip-1.0.0-setup.exe" } ] }
    ]
    """;

    [Fact]
    public void ParseKeepsOnlyPublishedReleasesWithOfficialSetupFiles()
    {
        var list = UpdateService.ParseReleases(Releases);
        Assert.Equal(["1.1.1", "1.0.0"], list.Select(r => r.Display));

        var r = list[0];
        Assert.Equal("DdoddoZip-1.1.1-setup.exe", r.AssetName);
        Assert.Equal(46000000, r.AssetSize);
        Assert.Equal("20eb8b08efd1e60652d47c6485d687df59e87edf0cd985b97378f34ef29e9bc8", r.Sha256); // lower-cased
        Assert.Equal(new DateTime(2026, 12, 3, 5, 0, 0, DateTimeKind.Utc), r.Published);
        Assert.Null(list[1].Sha256); // no digest: the download refuses it later
    }

    [Theory]
    [InlineData("v1.10.0", "1.9.9", true)]
    [InlineData("1.1.1", "1.1.0", true)]
    [InlineData("v1.1", "1.1.0", false)]
    [InlineData("v1.0.0", "1.1.0", false)]
    public void VersionsCompareAsNumbers(string tag, string current, bool newer)
    {
        Assert.True(UpdateService.TryParseVersion(tag, out var v));
        Assert.Equal(newer, v > Version.Parse(current));
    }

    [Theory]
    [InlineData("v1.2.0-beta")]
    [InlineData("latest")]
    [InlineData("")]
    public void NonReleaseTagsAreRejected(string tag) => Assert.False(UpdateService.TryParseVersion(tag, out _));

    [Fact]
    public void ServiceStartsUp()
    {
        // Touches every static field (version, HTTP client): a bad start-up order once crashed the app.
        Assert.True(UpdateService.Current >= new Version(0, 0, 0));
        Assert.False(UpdateService.DueForCheck(new AppSettings { AutoCheckUpdates = false }));
    }

    [Fact]
    public void ChangesSectionIsCutOutOfTheNotes()
    {
        var notes = UpdateService.ParseReleases(Releases)[0].Notes;
        Assert.Equal("- 7Z 압축이 **빨라졌습니다**\n- 큰 TAR.GZ 진행률", UpdateService.ChangesSection(notes).Replace("\r\n", "\n"));
        Assert.Equal("첫 공개", UpdateService.ChangesSection("첫 공개\n"));
    }

    [Fact]
    public async Task DownloadRefusesReleasesWithoutHashOrFromElsewhere()
    {
        var noHash = UpdateService.ParseReleases(Releases)[1];
        await Assert.ThrowsAsync<UpdateException>(() => UpdateService.DownloadAsync(noHash, null, default));

        var elsewhere = noHash with { AssetUrl = "https://evil.example.com/x-setup.exe", Sha256 = new string('a', 64) };
        await Assert.ThrowsAsync<UpdateException>(() => UpdateService.DownloadAsync(elsewhere, null, default));
    }
}
