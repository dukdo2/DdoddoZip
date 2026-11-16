namespace DdoddoZip.Core;

/// <summary>Formats DdoddoZip can create.</summary>
public enum ArchiveKind
{
    Zip,
    SevenZip,
    Tar,
    TarGz,
    TarXz,
    TarBz2,
}

public static class ArchiveKindInfo
{
    public static string Extension(ArchiveKind kind) => kind switch
    {
        ArchiveKind.Zip => ".zip",
        ArchiveKind.SevenZip => ".7z",
        ArchiveKind.Tar => ".tar",
        ArchiveKind.TarGz => ".tar.gz",
        ArchiveKind.TarXz => ".tar.xz",
        ArchiveKind.TarBz2 => ".tar.bz2",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string DisplayName(ArchiveKind kind) => kind switch
    {
        ArchiveKind.SevenZip => "7Z",
        _ => Extension(kind).TrimStart('.').ToUpperInvariant(),
    };

    /// <summary>7-Zip handler that builds the container ("zip", "7z", "tar").</summary>
    internal static string ContainerHandler(ArchiveKind kind) => kind switch
    {
        ArchiveKind.Zip => "zip",
        ArchiveKind.SevenZip => "7z",
        _ => "tar",
    };

    /// <summary>Single-stream compressor applied on top of a TAR, or null.</summary>
    internal static string? StreamHandler(ArchiveKind kind) => kind switch
    {
        ArchiveKind.TarGz => "gzip",
        ArchiveKind.TarXz => "xz",
        ArchiveKind.TarBz2 => "bzip2",
        _ => null,
    };

    public static bool TryParse(string text, out ArchiveKind kind)
    {
        switch (text.Trim().TrimStart('.').ToLowerInvariant())
        {
            case "zip": kind = ArchiveKind.Zip; return true;
            case "7z": kind = ArchiveKind.SevenZip; return true;
            case "tar": kind = ArchiveKind.Tar; return true;
            case "tar.gz" or "tgz" or "gz": kind = ArchiveKind.TarGz; return true;
            case "tar.xz" or "txz" or "xz": kind = ArchiveKind.TarXz; return true;
            case "tar.bz2" or "tbz2" or "tbz" or "bz2": kind = ArchiveKind.TarBz2; return true;
            default: kind = default; return false;
        }
    }
}
