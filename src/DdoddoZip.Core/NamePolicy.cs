namespace DdoddoZip.Core;

/// <summary>Naming rules for new archives and extracted files (F10, F08).</summary>
public static class NamePolicy
{
    private static readonly string[] MultiPartExtensions =
        [".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst", ".tar.lzma"];

    /// <summary>
    /// Base name for "○○.zip으로 압축하기": one item → its name (files lose their extension);
    /// several items → the name of the folder they are in.
    /// </summary>
    public static string QuickArchiveBaseName(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            throw new ArgumentException("선택한 항목이 없습니다.", nameof(paths));

        if (paths.Count == 1)
            return SingleItemName(paths[0]);

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths[0])));
        var parentName = parent is null ? "" : Path.GetFileName(parent);
        return string.IsNullOrEmpty(parentName) ? SingleItemName(paths[0]) : parentName;
    }

    private static string SingleItemName(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Directory.Exists(full))
        {
            var name = Path.GetFileName(full);
            return string.IsNullOrEmpty(name) ? "압축" : name; // drive root such as "E:\"
        }

        var stem = Path.GetFileNameWithoutExtension(full);
        if (string.IsNullOrEmpty(stem))
            stem = Path.GetFileName(full).TrimStart('.'); // ".gitignore" → "gitignore"
        return string.IsNullOrEmpty(stem) ? "압축" : stem;
    }

    /// <summary>"docs.zip", or "docs (2).zip", "docs (3).zip"… when taken.</summary>
    public static string GetUniquePath(string directory, string baseName, string extension)
    {
        var candidate = Path.Combine(directory, baseName + extension);
        for (int n = 2; Exists(candidate); n++)
            candidate = Path.Combine(directory, $"{baseName} ({n}){extension}");
        return candidate;
    }

    /// <summary>Unique variant of an existing file path, keeping its extension.</summary>
    public static string GetUniqueFilePath(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath) ?? "";
        var (stem, ext) = SplitExtension(Path.GetFileName(fullPath));
        return GetUniquePath(dir, stem, ext);
    }

    /// <summary>Archive name without its extension: "사진.zip" → "사진", "a.tar.gz" → "a", "b.part1.rar" → "b".</summary>
    public static string ArchiveBaseName(string archivePath)
    {
        var (stem, _) = SplitExtension(Path.GetFileName(archivePath));
        var part = stem.LastIndexOf(".part", StringComparison.OrdinalIgnoreCase);
        if (part > 0 && stem[(part + 5)..].All(char.IsAsciiDigit) && stem.Length > part + 5)
            stem = stem[..part];
        return string.IsNullOrWhiteSpace(stem) ? "압축 풀기" : stem;
    }

    private static (string Stem, string Ext) SplitExtension(string fileName)
    {
        foreach (var multi in MultiPartExtensions)
        {
            if (fileName.Length > multi.Length && fileName.EndsWith(multi, StringComparison.OrdinalIgnoreCase))
                return (fileName[..^multi.Length], fileName[^multi.Length..]);
        }
        var ext = Path.GetExtension(fileName);
        return (fileName[..^ext.Length], ext);
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
