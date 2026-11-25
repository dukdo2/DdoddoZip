using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DdoddoZip.Core;

namespace DdoddoZip.App.Services;

/// <summary>A published release on GitHub that carries a setup file.</summary>
public sealed record ReleaseInfo(
    Version Version,
    string Tag,
    DateTime? Published,
    string Notes,
    string AssetName,
    string AssetUrl,
    long AssetSize,
    string? Sha256)
{
    public string Display => Version.ToString(3);
}

public sealed class UpdateException(string message, string? details = null, Exception? inner = null)
    : Exception(message, inner)
{
    public string? Details { get; } = details;
}

/// <summary>
/// F20 auto update. Asks GitHub's public release list of the official repository which versions
/// exist, downloads a chosen setup file, checks its SHA-256 against the value GitHub publishes,
/// and starts it. Nothing about the person or the PC is sent; nothing installs without a click.
/// </summary>
public static class UpdateService
{
    // Fixed on purpose: updates come from this repository only.
    private const string ReleasesApi = "https://api.github.com/repos/dukdo2/DdoddoZip/releases?per_page=20";
    private const string DownloadPrefix = "https://github.com/dukdo2/DdoddoZip/releases/download/";
    public const string RepositoryUrl = "https://github.com/dukdo2/DdoddoZip";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    /// <summary>Version of the running program (from the assembly, e.g. 1.1.0).</summary>
    // Declared before Http: static fields start in this order, and CreateClient reads it.
    public static Version Current { get; } = ReadCurrentVersion();

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // GitHub's API rejects requests without a User-Agent. Only the program name and version.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"DdoddoZip/{Current.ToString(3)}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private static Version ReadCurrentVersion()
    {
#if DEBUG
        // Test hook for development builds only: pretend to be an older version so the real
        // releases on GitHub show up as updates.
        if (Version.TryParse(Environment.GetEnvironmentVariable("DDZ_PRETEND_VERSION"), out var pretend))
            return pretend;
#endif
        var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>True when the startup check should run now (setting on, a day since the last one).</summary>
    public static bool DueForCheck(AppSettings s) =>
        s.AutoCheckUpdates && (s.LastUpdateCheckUtc is not { } last || DateTime.UtcNow - last >= CheckInterval);

    /// <summary>Releases newer than this program, newest first. Empty when up to date.</summary>
    public static async Task<List<ReleaseInfo>> GetNewerAsync(CancellationToken ct = default)
    {
        var all = await GetReleasesAsync(ct);
        return all.Where(r => r.Version > Current).ToList();
    }

    /// <summary>Releases after <paramref name="from"/> up to and including this program, newest first.</summary>
    public static async Task<List<ReleaseInfo>> GetInstalledSinceAsync(Version from, CancellationToken ct = default)
    {
        var all = await GetReleasesAsync(ct);
        return all.Where(r => r.Version > from && r.Version <= Current).ToList();
    }

    private static async Task<List<ReleaseInfo>> GetReleasesAsync(CancellationToken ct)
    {
        string json;
        try
        {
            json = await Http.GetStringAsync(ReleasesApi, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new UpdateException("업데이트를 확인하지 못했습니다. 인터넷 연결을 확인해 주세요.", e.Message, e);
        }
        return ParseReleases(json);
    }

    /// <summary>Reads GitHub's release list. Drafts, pre-releases and releases without a setup file are left out.</summary>
    public static List<ReleaseInfo> ParseReleases(string json)
    {
        var list = new List<ReleaseInfo>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (Bool(r, "draft") || Bool(r, "prerelease")) continue;
            var tag = Str(r, "tag_name") ?? "";
            if (!TryParseVersion(tag, out var version)) continue;
            if (!r.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;

            foreach (var a in assets.EnumerateArray())
            {
                var name = Str(a, "name") ?? "";
                var url = Str(a, "browser_download_url") ?? "";
                if (!name.EndsWith("setup.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (!url.StartsWith(DownloadPrefix, StringComparison.Ordinal)) continue;

                var digest = Str(a, "digest");
                var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    ? digest["sha256:".Length..].ToLowerInvariant() : null;
                DateTime? published = DateTime.TryParse(Str(r, "published_at"), null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var p) ? p : null;
                list.Add(new ReleaseInfo(version, tag, published, Str(r, "body") ?? "", name, url,
                    a.TryGetProperty("size", out var size) && size.TryGetInt64(out var n) ? n : 0, sha));
                break;
            }
        }
        return list.OrderByDescending(r => r.Version).ToList();
    }

    /// <summary>"v1.2.3" or "1.2.3" → 1.2.3. Anything else (e.g. "v1.2.3-beta") is not a release version.</summary>
    public static bool TryParseVersion(string tag, out Version version)
    {
        var t = tag.Trim().TrimStart('v', 'V');
        if (Version.TryParse(t, out var v) && v.Major >= 0)
        {
            version = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    /// <summary>The "## 바뀐 점" section of release notes, or all of them when there is none.</summary>
    public static string ChangesSection(string notes)
    {
        var lines = notes.Replace("\r\n", "\n").Split('\n');
        int start = Array.FindIndex(lines, l => l.TrimStart().StartsWith("## ") && l.Contains("바뀐 점"));
        if (start < 0) return notes.Trim();
        var sb = new StringBuilder();
        for (int i = start + 1; i < lines.Length && !lines[i].TrimStart().StartsWith("## "); i++)
            sb.AppendLine(lines[i]);
        var section = sb.ToString().Trim();
        return section.Length > 0 ? section : notes.Trim();
    }

    /// <summary>
    /// Downloads the setup file into %TEMP%\DdoddoZip\update and checks its SHA-256.
    /// Throws <see cref="UpdateException"/> (file removed) when anything is wrong.
    /// </summary>
    public static async Task<string> DownloadAsync(ReleaseInfo release, IProgress<ArchiveProgress>? progress, CancellationToken ct)
    {
        if (!release.AssetUrl.StartsWith(DownloadPrefix, StringComparison.Ordinal))
            throw new UpdateException("공식 저장소가 아닌 주소라서 받지 않았습니다.", release.AssetUrl);
        if (release.Sha256 is not { Length: 64 })
            throw new UpdateException("파일을 확인할 정보(해시)가 없는 릴리스라서 받지 않았습니다. 저장소에서 직접 받아 주세요.");

        var dir = Path.Combine(Path.GetTempPath(), "DdoddoZip", "update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Path.GetFileName(release.AssetName));
        try
        {
            using (var response = await Http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = (ulong)(response.Content.Headers.ContentLength ?? release.AssetSize);
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 16];
                ulong done = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    sha.AppendData(buffer, 0, n);
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += (ulong)n;
                    progress?.Report(new ArchiveProgress(done, total, release.AssetName));
                }
                var actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                if (actual != release.Sha256)
                    throw new UpdateException("받은 파일이 올바르지 않아 지웠습니다. 잠시 후 다시 시도해 주세요.",
                        $"기대한 SHA-256: {release.Sha256}\n받은 파일:     {actual}");
            }
            return path;
        }
        // A timeout also comes as TaskCanceledException: only the person's own cancel stays a cancel.
        catch (Exception e) when (e is HttpRequestException or IOException
                                  || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            TryDelete(path);
            throw new UpdateException("업데이트를 받지 못했습니다. 인터넷 연결을 확인해 주세요.", e.Message, e);
        }
        catch
        {
            TryDelete(path); // wrong hash, cancelled, ...
            throw;
        }
    }

    /// <summary>Starts the verified setup file. "/UPDATE" makes it reopen 또또집 when done.</summary>
    public static void StartInstaller(string setupPath)
    {
        // UseShellExecute so Windows shows the admin (UAC) prompt the setup file asks for.
        // /NORESTART: if the right-click DLL must wait for a restart, say nothing; it is replaced at the next one.
        Process.Start(new ProcessStartInfo(setupPath, "/SILENT /UPDATE /NORESTART") { UseShellExecute = true });
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>Donation links (F23). A null link hides its button; both null hides the section.</summary>
public static class SupportLinks
{
    /// <summary>KakaoPay money-transfer link (qr.kakaopay.com/...). Not used in 1.1 (no link), so its button stays hidden.</summary>
    public static readonly string? KakaoPay = Pick(null);

    /// <summary>GitHub Sponsors page.</summary>
    public static readonly string? GitHubSponsors = Pick("https://github.com/sponsors/dukdo2");

    // Debug builds can show the buttons before the real links exist (screen checks): DDZ_TEST_SUPPORT=1.
    private static string? Pick(string? link)
    {
#if DEBUG
        if (link is null && Environment.GetEnvironmentVariable("DDZ_TEST_SUPPORT") == "1")
            return "https://example.com/";
#endif
        return link;
    }

    public static bool Any => KakaoPay is not null || GitHubSponsors is not null;
}
