namespace DdoddoZip.Core;

/// <summary>
/// Turns a path stored inside an archive into a safe path on disk (F09).
/// Blocks "Zip Slip" (entries that climb out of the destination with "..") and
/// fixes names Windows cannot create.
/// </summary>
public static class PathGuard
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Returns a cleaned relative path, or null when the entry must be refused.
    /// </summary>
    public static string? SanitizeRelative(string entryPath, out string? reason)
    {
        reason = null;
        var p = entryPath.Replace('/', '\\');

        // Drop drive letters ("C:"), UNC/device prefixes and leading slashes: always extract relative.
        if (p.Length >= 2 && p[1] == ':' && char.IsAsciiLetter(p[0]) && (p.Length == 2 || p[2] == '\\'))
            p = p[2..];
        p = p.TrimStart('\\');

        var parts = new List<string>();
        foreach (var raw in p.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw == ".")
                continue;
            if (raw == "..")
            {
                reason = "압축 파일 밖의 폴더로 나가는 경로(..)라서 차단했습니다";
                return null;
            }
            parts.Add(SanitizeSegment(raw));
        }

        if (parts.Count == 0)
        {
            reason = "이름이 비어 있습니다";
            return null;
        }
        return string.Join('\\', parts);
    }

    /// <summary>Full destination path, or null when it would end up outside <paramref name="root"/>.</summary>
    public static string? Resolve(string root, string entryPath, out string? reason)
    {
        var rel = SanitizeRelative(entryPath, out reason);
        if (rel is null)
            return null;

        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + '\\';
        var full = Path.GetFullPath(Path.Combine(rootFull, rel));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            reason = "풀 위치 밖을 가리키는 경로라서 차단했습니다";
            return null;
        }
        return full;
    }

    internal static string SanitizeSegment(string s)
    {
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(InvalidChars, chars[i]) >= 0)
                chars[i] = '_';
        }
        s = new string(chars);

        // Windows silently drops trailing dots and spaces, which could merge two entries into one file.
        int end = s.Length;
        while (end > 0 && (s[end - 1] == '.' || s[end - 1] == ' '))
            end--;
        if (end < s.Length)
            s = s[..end] + new string('_', s.Length - end);

        var dot = s.IndexOf('.');
        var stem = (dot < 0 ? s : s[..dot]).TrimEnd(' ');
        if (ReservedNames.Contains(stem))
            s = "_" + s;

        return s;
    }
}
