using DdoddoZip.Core.Interop;

namespace DdoddoZip.Core;

/// <summary>
/// An opened archive: its file list (F01) plus extract/test operations (F02, F05-F09).
/// Use from background threads; one session must not be used by two threads at once.
/// </summary>
public sealed class ArchiveSession : IDisposable
{
    private static readonly HashSet<string> StreamFormats = new(StringComparer.OrdinalIgnoreCase)
        { "gzip", "bzip2", "xz", "zstd", "lzma" };

    private IInArchive? _archive;
    private InStreamWrapper? _stream;
    private string? _tempInnerTar;
    private string? _password;

    public string ArchivePath { get; }

    /// <summary>Display name of the format: "ZIP", "7Z", "RAR5", "TAR.GZ"...</summary>
    public string FormatName { get; private set; } = "";

    public IReadOnlyList<ArchiveEntry> Entries { get; private set; } = [];

    /// <summary>ZIP only: how file names are encoded. Null for other formats.</summary>
    public NameEncodingInfo? NameEncoding { get; private set; }

    public bool HasEncryptedEntries => Entries.Any(e => e.IsEncrypted);

    /// <summary>Password that worked so far (given or typed), so a reopened session need not ask again.</summary>
    public string? Password => _password;

    public ulong TotalSize => Entries.Aggregate(0UL, (sum, e) => sum + e.Size);

    private ArchiveSession(string path) => ArchivePath = path;

    public static ArchiveSession Open(
        string path,
        ArchiveOpenOptions? options = null,
        IArchiveInteraction? interaction = null,
        CancellationToken ct = default)
    {
        options ??= new ArchiveOpenOptions();
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
            throw new ArchiveException(ArchiveErrorKind.Io, $"파일을 찾을 수 없습니다: {path}");

        var session = new ArchiveSession(path) { _password = options.Password };
        try
        {
            session.OpenCore(options, interaction, ct);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private void OpenCore(ArchiveOpenOptions options, IArchiveInteraction? interaction, CancellationToken ct)
    {
        var lib = SevenZipLibrary.Instance;
        var (archive, stream, format) = OpenWithLibrary(lib, ArchivePath, options, interaction, ct);
        _archive = archive;
        _stream = stream;
        FormatName = DisplayFormat(format.Name);

        // "a.tar.gz": the gzip holds a single .tar, so open that tar to show the real file list.
        if (StreamFormats.Contains(format.Name) && HoldsSingleTar(archive, ArchivePath))
        {
            var temp = ExtractSingleStreamToTemp(archive, ct);
            Close();
            _tempInnerTar = temp;
            var (tarArchive, tarStream, _) = OpenWithLibrary(lib, temp, new ArchiveOpenOptions(), null, ct, onlyFormat: "tar");
            _archive = tarArchive;
            _stream = tarStream;
            FormatName = "TAR." + DisplayFormat(format.Name);
        }

        Entries = ReadEntries(_archive!);
    }

    private (IInArchive Archive, InStreamWrapper Stream, FormatEntry Format) OpenWithLibrary(
        SevenZipLibrary lib,
        string path,
        ArchiveOpenOptions options,
        IArchiveInteraction? interaction,
        CancellationToken ct,
        string? onlyFormat = null)
    {
        var stream = new InStreamWrapper(new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess));
        try
        {
            var candidates = onlyFormat is null ? lib.GetOpenCandidates(path) : [lib.GetFormat(onlyFormat)];
            foreach (var format in candidates)
            {
                bool wrongBefore = false;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var archive = lib.CreateObject<IInArchive>(format.ClassId);

                    if (format.Name.Equals("zip", StringComparison.OrdinalIgnoreCase))
                    {
                        NameEncoding ??= EncodingDetector.DetectZip(path);
                        if ((options.CodePage ?? NameEncoding.CodePage) is int cp)
                            SevenZipLibrary.SetProperties(archive, [("cp", PropVariant.FromUInt32((uint)cp))]);
                    }

                    stream.Seek(0, 0, IntPtr.Zero);
                    var callback = new OpenCallback(path, _password, wrongBefore, interaction, ct);
                    ulong maxCheckStart = 1 << 22;
                    int hr = archive.Open(stream, ref maxCheckStart, callback);

                    if (hr == HResult.S_OK)
                    {
                        _password = callback.Password ?? _password;
                        return (archive, stream, format);
                    }

                    archive.Close();
                    SevenZipLibrary.Release(archive);

                    if (callback.UserCancelled || ct.IsCancellationRequested)
                        throw new OperationCanceledException(ct);
                    if (stream.Error is { } readError)
                        throw new ArchiveException(ArchiveErrorKind.Io, $"파일을 읽을 수 없습니다: {readError.Message}", readError);

                    // Encrypted file names (7Z): a wrong password makes Open fail. Ask again.
                    if (callback.PasswordRequested)
                    {
                        if (interaction is null)
                            throw new ArchiveException(ArchiveErrorKind.WrongPassword, "암호가 필요하거나 맞지 않습니다.");
                        _password = null;
                        wrongBefore = true;
                        continue;
                    }
                    break; // not this format; try the next one
                }
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        stream.Dispose();
        throw new ArchiveException(ArchiveErrorKind.NotArchive, "압축 파일이 아니거나, 손상되었거나, 지원하지 않는 형식입니다.");
    }

    private static readonly string[] TarCompoundExtensions =
        [".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst", ".tar.lzma", ".tgz", ".tbz", ".tbz2", ".txz", ".tzst"];

    /// <summary>
    /// True for "x.tar.gz"-style files. gzip stores the inner name ("x.tar"); xz and bzip2 store
    /// no name, so the archive's own file name decides.
    /// </summary>
    private static bool HoldsSingleTar(IInArchive archive, string archivePath)
    {
        if (archive.GetNumberOfItems(out var n) != HResult.S_OK || n != 1)
            return false;
        var pv = default(PropVariant);
        archive.GetProperty(0, ItemPropId.Path, ref pv);
        var name = pv.AsString();
        pv.Clear();
        if (name is not null && name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            return true;
        return TarCompoundExtensions.Any(ext => archivePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    private static string ExtractSingleStreamToTemp(IInArchive archive, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "DdoddoZip");
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tar");
        var entry = new ArchiveEntry(0, "inner.tar", false, 0, 0, null, false, 0);
        var plan = new Dictionary<uint, (ArchiveEntry, string)> { [0] = (entry, temp) };
        var result = new ExtractResult();
        var cb = new ExtractCallback("", plan, false, OverwriteMode.Overwrite, null, null, null, result, ct);
        int hr = archive.Extract([0u], 1, 0, cb);
        if (hr != HResult.S_OK || result.Failures.Count > 0 || cb.FatalError is not null)
        {
            try { File.Delete(temp); } catch (IOException) { }
            if (hr == HResult.E_ABORT) throw new OperationCanceledException(ct);
            throw new ArchiveException(ArchiveErrorKind.Corrupted,
                result.Failures.FirstOrDefault()?.Message ?? "압축 파일 안의 TAR를 꺼내지 못했습니다.", cb.FatalError);
        }
        return temp;
    }

    private List<ArchiveEntry> ReadEntries(IInArchive archive)
    {
        archive.GetNumberOfItems(out var count);
        var list = new List<ArchiveEntry>((int)count);
        var pv = default(PropVariant);

        for (uint i = 0; i < count; i++)
        {
            archive.GetProperty(i, ItemPropId.Path, ref pv);
            var path = pv.AsString(); pv.Clear();
            archive.GetProperty(i, ItemPropId.IsDir, ref pv);
            var isDir = pv.AsBool(); pv.Clear();
            archive.GetProperty(i, ItemPropId.Size, ref pv);
            var size = pv.AsUInt64(); pv.Clear();
            archive.GetProperty(i, ItemPropId.PackSize, ref pv);
            var packed = pv.AsUInt64(); pv.Clear();
            archive.GetProperty(i, ItemPropId.MTime, ref pv);
            var mtime = pv.AsDateTime(); pv.Clear();
            archive.GetProperty(i, ItemPropId.Encrypted, ref pv);
            var encrypted = pv.AsBool(); pv.Clear();
            archive.GetProperty(i, ItemPropId.Attrib, ref pv);
            var attrib = (uint)pv.AsUInt64(); pv.Clear();

            if (string.IsNullOrEmpty(path))
                path = count == 1 ? NamePolicy.ArchiveBaseName(ArchivePath) : $"[{i}]";
            path = path.Replace('/', '\\').TrimEnd('\\');

            list.Add(new ArchiveEntry((int)i, path, isDir, size, packed, mtime, encrypted, attrib));
        }
        return list;
    }

    /// <summary>True when every selected entry sits under one top-level folder (or there is a single entry).</summary>
    public bool HasSingleRoot(IEnumerable<ArchiveEntry>? entries = null, string? baseFolder = null)
    {
        string? root = null;
        bool rootIsFolder = false;
        int count = 0;
        foreach (var e in entries ?? Entries)
        {
            var path = StripBase(e.Path, baseFolder);
            if (path is null) continue;
            count++;
            var slash = path.IndexOf('\\');
            var first = slash < 0 ? path : path[..slash];
            if (root is null) root = first;
            else if (!root.Equals(first, StringComparison.OrdinalIgnoreCase)) return false;
            if (slash >= 0 || e.IsDirectory) rootIsFolder = true;
        }
        return count == 1 || (root is not null && rootIsFolder);
    }

    public ExtractResult Extract(
        ExtractOptions options,
        IArchiveInteraction? interaction = null,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default)
    {
        var selected = SelectEntries(options.Indices);
        bool makeFolder = options.FolderMode switch
        {
            FolderMode.Always => true,
            FolderMode.Never => false,
            _ => !HasSingleRoot(selected, options.BaseFolder),
        };
        var root = Path.GetFullPath(options.Destination);
        if (makeFolder)
            root = Path.Combine(root, PathGuard.SanitizeSegment(NamePolicy.ArchiveBaseName(ArchivePath)));

        var result = new ExtractResult { OutputDirectory = root };
        var plan = new Dictionary<uint, (ArchiveEntry, string)>();
        foreach (var e in selected)
        {
            var rel = StripBase(e.Path, options.BaseFolder);
            if (rel is null) continue;
            var full = PathGuard.Resolve(root, rel, out _);
            if (full is null)
                result.BlockedPaths.Add(e.Path);
            else
                plan[(uint)e.Index] = (e, full);
        }

        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ArchiveException(ArchiveErrorKind.Io, $"풀 위치를 만들 수 없습니다: {root} ({e.Message})", e);
        }

        Run(plan, testMode: false, options.Overwrite, options.Password, interaction, progress, result, ct);
        return result;
    }

    /// <summary>Checks every file without writing anything (integrity test).</summary>
    public ExtractResult Test(
        IArchiveInteraction? interaction = null,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default)
    {
        var result = new ExtractResult();
        var plan = Entries.ToDictionary(e => (uint)e.Index, e => (e, ""));
        Run(plan, testMode: true, OverwriteMode.Skip, null, interaction, progress, result, ct);
        return result;
    }

    private void Run(
        Dictionary<uint, (ArchiveEntry Entry, string FullPath)> plan,
        bool testMode,
        OverwriteMode overwrite,
        string? password,
        IArchiveInteraction? interaction,
        IProgress<ArchiveProgress>? progress,
        ExtractResult result,
        CancellationToken ct)
    {
        var archive = _archive ?? throw new ObjectDisposedException(nameof(ArchiveSession));
        password ??= _password;
        var indices = plan.Keys.Order().ToArray();
        var directories = new List<(string FullPath, DateTime Modified)>();

        while (indices.Length > 0)
        {
            ct.ThrowIfCancellationRequested();
            var cb = new ExtractCallback(ArchivePath, plan, testMode, overwrite, password, interaction, progress, result, ct);
            int hr = archive.Extract(indices, (uint)indices.Length, testMode ? 1 : 0, cb);
            directories.AddRange(cb.Directories);

            if (cb.UserCancelled || hr == HResult.E_ABORT || ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            if (cb.FatalError is ArchiveException ae)
                throw ae;
            if (cb.FatalError is not null)
                throw new ArchiveException(ArchiveErrorKind.Unknown, cb.FatalError.Message, cb.FatalError);
            if (hr != HResult.S_OK && hr != HResult.S_FALSE)
                throw new ArchiveException(ArchiveErrorKind.Corrupted, $"압축을 푸는 중 오류가 났습니다 (0x{hr:X8}).");

            password = cb.Password;
            if (cb.WrongPasswordIndices.Count == 0 || interaction is null)
                break;

            // Wrong password: ask again and retry only the files that failed.
            var retry = interaction.AskPassword(ArchivePath, previousWasWrong: true);
            if (retry is null)
                break;
            password = retry;
            var failed = cb.WrongPasswordIndices.Select(i => plan[i].Entry.Path).ToHashSet();
            result.Failures.RemoveAll(f => f.Kind == ArchiveErrorKind.WrongPassword && failed.Contains(f.Path));
            indices = cb.WrongPasswordIndices.Order().ToArray();
        }

        _password = password;

        // Folder times are set last; writing files inside would change them again.
        foreach (var (dir, time) in directories.OrderByDescending(d => d.FullPath.Length))
        {
            try { Directory.SetLastWriteTimeUtc(dir, time); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private List<ArchiveEntry> SelectEntries(IReadOnlyCollection<int>? indices)
    {
        if (indices is null) return [.. Entries];
        var set = indices.ToHashSet();
        return Entries.Where(e => set.Contains(e.Index)).ToList();
    }

    /// <summary>Entry path relative to <paramref name="baseFolder"/>, or null when outside it.</summary>
    private static string? StripBase(string path, string? baseFolder)
    {
        if (string.IsNullOrEmpty(baseFolder)) return path;
        var prefix = baseFolder.Replace('/', '\\').TrimEnd('\\') + '\\';
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path[prefix.Length..] : null;
    }

    private static string DisplayFormat(string handlerName) => handlerName.ToLowerInvariant() switch
    {
        "gzip" => "GZ",
        "bzip2" => "BZ2",
        "zstd" => "ZST",
        _ => handlerName.ToUpperInvariant(),
    };

    private void Close()
    {
        if (_archive is not null)
        {
            _archive.Close();
            SevenZipLibrary.Release(_archive);
            _archive = null;
        }
        _stream?.Dispose();
        _stream = null;
    }

    public void Dispose()
    {
        Close();
        if (_tempInnerTar is not null)
        {
            try { File.Delete(_tempInnerTar); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _tempInnerTar = null;
        }
    }
}
