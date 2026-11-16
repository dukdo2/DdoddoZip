using DdoddoZip.Core.Interop;

namespace DdoddoZip.Core;

/// <summary>Creates new archives (F03, F10).</summary>
public static class Compressor
{
    /// <summary>
    /// "○○.zip으로 압축하기": zips the selected items next to them with an automatic, non-clashing name.
    /// </summary>
    public static CompressResult QuickZip(
        IReadOnlyList<string> sources,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default)
    {
        var first = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sources[0]));
        var dir = Path.GetDirectoryName(first) ?? first;
        var dest = NamePolicy.GetUniquePath(dir, NamePolicy.QuickArchiveBaseName(sources), ".zip");
        return Compress(sources, dest, new CompressOptions(), progress, ct);
    }

    /// <summary>
    /// Compresses files and folders into <paramref name="destination"/> (replaced if it exists).
    /// Each source keeps its own name at the top of the archive. Writes to a temp file first,
    /// so a cancelled or failed run never leaves a broken archive behind.
    /// </summary>
    public static CompressResult Compress(
        IReadOnlyList<string> sources,
        string destination,
        CompressOptions options,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (sources.Count == 0)
            throw new ArgumentException("압축할 항목이 없습니다.", nameof(sources));

        if (options.Password is { } pw && options.Kind == ArchiveKind.Zip && !pw.All(c => c is >= ' ' and <= '~'))
            throw new ArchiveException(ArchiveErrorKind.Unsupported,
                "ZIP 암호에는 영문, 숫자, 기호만 쓸 수 있습니다. 한글 암호를 쓰려면 7Z 형식을 선택하세요.");
        if (options.Password is { Length: 0 })
            throw new ArchiveException(ArchiveErrorKind.Unsupported, "암호가 비어 있습니다.");

        destination = Path.GetFullPath(destination);
        var lib = SevenZipLibrary.Instance;
        var items = CollectItems(sources, destination, ct);
        var temp = destination + "." + Guid.NewGuid().ToString("N")[..8] + ".ddz-tmp";
        string? tarTemp = null;

        try
        {
            var streamHandler = ArchiveKindInfo.StreamHandler(options.Kind);
            if (streamHandler is null)
            {
                RunUpdate(lib, ArchiveKindInfo.ContainerHandler(options.Kind), items, temp, options, progress, ct);
            }
            else
            {
                // TAR.GZ and friends: build the .tar, then compress it as a single stream.
                tarTemp = destination + "." + Guid.NewGuid().ToString("N")[..8] + ".tar.ddz-tmp";
                RunUpdate(lib, "tar", items, tarTemp, options, Scale(progress, 0, 0.5), ct);

                var info = new FileInfo(tarTemp);
                var now = DateTime.UtcNow;
                var tarName = NamePolicy.ArchiveBaseName(destination) + ".tar";
                var single = new List<UpdateItem>
                {
                    new(tarTemp, tarName, false, (ulong)info.Length, FileAttributes.Archive, now, now, now),
                };
                RunUpdate(lib, streamHandler, single, temp, options, Scale(progress, 0.5, 0.5), ct);
            }

            File.Move(temp, destination, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        finally
        {
            if (tarTemp is not null) TryDelete(tarTemp);
        }

        ulong sourceBytes = 0;
        foreach (var i in items) sourceBytes += i.Size;
        return new CompressResult(destination, items.Count(i => !i.IsDirectory), sourceBytes, new FileInfo(destination).Length);
    }

    private static void RunUpdate(
        SevenZipLibrary lib,
        string handlerName,
        IReadOnlyList<UpdateItem> items,
        string outputPath,
        CompressOptions options,
        IProgress<ArchiveProgress>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var format = lib.GetFormat(handlerName);
        var archive = lib.CreateObject<IOutArchive>(format.ClassId);
        try
        {
            SevenZipLibrary.SetProperties(archive, BuildProperties(handlerName, options));

            using var output = new OutStreamWrapper(new FileStream(outputPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16));
            var password = handlerName is "zip" or "7z" ? options.Password : null;
            var callback = new UpdateCallback(items, password, progress, ct);
            int hr = archive.UpdateItems(output, (uint)items.Count, callback);
            callback.Complete();

            if (hr == HResult.E_ABORT || ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            if (callback.FatalError is ArchiveException ae)
                throw ae;
            if (callback.FatalError is not null)
                throw new ArchiveException(ArchiveErrorKind.Io, callback.FatalError.Message, callback.FatalError);
            if (output.Error is { } writeError)
                throw new ArchiveException(ArchiveErrorKind.Io, $"압축 파일을 쓰지 못했습니다 (디스크 공간이 부족할 수 있습니다): {writeError.Message}", writeError);
            if (hr != HResult.S_OK)
                throw new ArchiveException(ArchiveErrorKind.Unknown, $"압축 중 오류가 났습니다 (0x{hr:X8}).");
        }
        finally
        {
            SevenZipLibrary.Release(archive);
        }
    }

    private static List<(string, PropVariant)> BuildProperties(string handler, CompressOptions o)
    {
        var props = new List<(string, PropVariant)>();
        var level = (uint)o.Level;
        switch (handler)
        {
            case "zip":
                props.Add(("x", PropVariant.FromUInt32(level)));
                props.Add(("cu", PropVariant.FromBool(true))); // non-English names as UTF-8 (readable everywhere)
                if (o.Password is not null)
                    props.Add(("em", PropVariant.FromString("AES256")));
                break;
            case "7z":
                props.Add(("x", PropVariant.FromUInt32(level)));
                if (o.Password is not null && o.EncryptFileNames)
                    props.Add(("he", PropVariant.FromBool(true)));
                break;
            case "gzip" or "bzip2" or "xz":
                props.Add(("x", PropVariant.FromUInt32(Math.Max(level, 1))));
                break;
        }
        return props;
    }

    private static List<UpdateItem> CollectItems(IReadOnlyList<string> sources, string destination, CancellationToken ct)
    {
        var items = new List<UpdateItem>();
        foreach (var raw in sources)
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            var name = Path.GetFileName(full);
            if (string.IsNullOrEmpty(name))
                throw new ArchiveException(ArchiveErrorKind.Io, $"드라이브 전체는 압축할 수 없습니다: {raw}");

            if (Directory.Exists(full))
                AddDirectory(new DirectoryInfo(full), name, items, destination, ct);
            else if (File.Exists(full))
                AddFile(new FileInfo(full), name, items, destination);
            else
                throw new ArchiveException(ArchiveErrorKind.Io, $"파일이나 폴더를 찾을 수 없습니다: {raw}");
        }
        return items;
    }

    private static void AddDirectory(DirectoryInfo dir, string archivePath, List<UpdateItem> items, string destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        items.Add(new UpdateItem(null, archivePath, true, 0, dir.Attributes, dir.CreationTimeUtc, dir.LastWriteTimeUtc, dir.LastAccessTimeUtc));

        IEnumerable<FileSystemInfo> children;
        try
        {
            children = dir.EnumerateFileSystemInfos().OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ArchiveException(ArchiveErrorKind.Io, $"폴더를 읽을 수 없습니다: {dir.FullName} ({e.Message})", e);
        }

        foreach (var child in children)
        {
            var childPath = archivePath + "\\" + child.Name;
            if (child is DirectoryInfo sub)
            {
                // Do not follow junctions/symlinks: they can point back up and loop forever.
                if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                AddDirectory(sub, childPath, items, destination, ct);
            }
            else if (child is FileInfo file)
            {
                AddFile(file, childPath, items, destination);
            }
        }
    }

    private static void AddFile(FileInfo file, string archivePath, List<UpdateItem> items, string destination)
    {
        if (string.Equals(file.FullName, destination, StringComparison.OrdinalIgnoreCase) ||
            file.Name.EndsWith(".ddz-tmp", StringComparison.OrdinalIgnoreCase))
            return; // never put the archive being written into itself
        items.Add(new UpdateItem(file.FullName, archivePath, false, (ulong)file.Length, file.Attributes,
            file.CreationTimeUtc, file.LastWriteTimeUtc, file.LastAccessTimeUtc));
    }

    private static IProgress<ArchiveProgress>? Scale(IProgress<ArchiveProgress>? inner, double start, double share)
    {
        return inner is null ? null : new ScaledProgress(inner, start, share);
    }

    /// <summary>Maps one stage's 0-100% onto a slice of the whole job. Reports synchronously, in order.</summary>
    private sealed class ScaledProgress(IProgress<ArchiveProgress> inner, double start, double share) : IProgress<ArchiveProgress>
    {
        public void Report(ArchiveProgress p)
        {
            const ulong scale = 1_000_000;
            inner.Report(new ArchiveProgress((ulong)((start + p.Fraction * share) * scale), scale, p.CurrentItem));
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
