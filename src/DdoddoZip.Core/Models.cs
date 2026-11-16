namespace DdoddoZip.Core;

/// <summary>One item (file or folder) inside an archive.</summary>
public sealed record ArchiveEntry(
    int Index,
    string Path,
    bool IsDirectory,
    ulong Size,
    ulong PackedSize,
    DateTime? Modified,
    bool IsEncrypted,
    uint Attributes)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

public readonly record struct ArchiveProgress(ulong CompletedBytes, ulong TotalBytes, string? CurrentItem)
{
    public double Fraction => TotalBytes == 0 ? 0 : Math.Min(1.0, (double)CompletedBytes / TotalBytes);
}

public enum ArchiveErrorKind
{
    Unknown,
    EngineMissing,
    NotArchive,
    Corrupted,
    WrongPassword,
    Unsupported,
    Io,
}

public sealed class ArchiveException(ArchiveErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public ArchiveErrorKind Kind { get; } = kind;
}

/// <summary>Compression level. Values are the 7-Zip "x" levels.</summary>
public enum CompressLevel
{
    Store = 0,
    Fast = 1,
    Normal = 5,
    Maximum = 9,
}

public enum OverwriteMode { Ask, Overwrite, Skip, Rename }

public enum OverwriteChoice { Overwrite, Skip, Rename, Cancel }

public readonly record struct OverwriteDecision(OverwriteChoice Choice, bool ApplyToAll = false);

public sealed record OverwriteRequest(
    string ExistingPath,
    long ExistingSize,
    DateTime ExistingModified,
    ulong NewSize,
    DateTime? NewModified);

/// <summary>Whether to put extracted files into a folder named after the archive.</summary>
public enum FolderMode
{
    /// <summary>Create the folder unless everything is already inside a single top-level folder.</summary>
    Auto,
    Always,
    Never,
}

/// <summary>Questions the engine needs a person to answer (console or GUI).</summary>
public interface IArchiveInteraction
{
    /// <summary>Returns the password, or null to cancel.</summary>
    string? AskPassword(string archivePath, bool previousWasWrong);

    OverwriteDecision AskOverwrite(OverwriteRequest request);
}

public sealed class ArchiveOpenOptions
{
    /// <summary>Force a code page for ZIP file names (e.g. 949, 65001). Null = detect automatically.</summary>
    public int? CodePage { get; init; }

    public string? Password { get; init; }
}

public sealed class ExtractOptions
{
    public required string Destination { get; init; }

    public FolderMode FolderMode { get; init; } = FolderMode.Auto;

    public OverwriteMode Overwrite { get; init; } = OverwriteMode.Ask;

    /// <summary>Entry indices to extract. Null = everything.</summary>
    public IReadOnlyCollection<int>? Indices { get; init; }

    /// <summary>
    /// Folder inside the archive to treat as the root (e.g. the folder currently shown in the UI).
    /// Entries under it are written without this prefix.
    /// </summary>
    public string? BaseFolder { get; init; }

    public string? Password { get; init; }
}

public sealed record ItemFailure(string Path, ArchiveErrorKind Kind, string Message);

public sealed class ExtractResult
{
    public string OutputDirectory { get; internal set; } = "";
    public int FilesWritten { get; internal set; }
    public int FilesTested { get; internal set; }
    public int Skipped { get; internal set; }
    public List<ItemFailure> Failures { get; } = [];

    /// <summary>Entries refused because their path tried to leave the destination folder.</summary>
    public List<string> BlockedPaths { get; } = [];

    public bool HasProblems => Failures.Count > 0 || BlockedPaths.Count > 0;
}

public sealed class CompressOptions
{
    public ArchiveKind Kind { get; init; } = ArchiveKind.Zip;
    public CompressLevel Level { get; init; } = CompressLevel.Normal;

    /// <summary>Encrypt with this password (ZIP uses AES-256). Null = no encryption.</summary>
    public string? Password { get; init; }

    /// <summary>7Z only: also hide file names.</summary>
    public bool EncryptFileNames { get; init; }
}

public sealed record CompressResult(string OutputPath, int FileCount, ulong SourceBytes, long ArchiveBytes);
