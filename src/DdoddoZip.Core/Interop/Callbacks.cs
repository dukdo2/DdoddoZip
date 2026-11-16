using System.Runtime.InteropServices;

namespace DdoddoZip.Core.Interop;

/// <summary>Answers 7-Zip's password question while opening (7Z with encrypted file names).</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class OpenCallback(
    string archivePath,
    string? password,
    bool previousWasWrong,
    IArchiveInteraction? interaction,
    CancellationToken ct) : IArchiveOpenCallback, ICryptoGetTextPassword
{
    public string? Password { get; private set; } = password;
    public bool PasswordRequested { get; private set; }
    public bool UserCancelled { get; private set; }

    public int SetTotal(IntPtr files, IntPtr bytes) => HResult.S_OK;

    public int SetCompleted(IntPtr files, IntPtr bytes) =>
        ct.IsCancellationRequested ? HResult.E_ABORT : HResult.S_OK;

    public int CryptoGetTextPassword(out IntPtr password)
    {
        PasswordRequested = true;
        password = IntPtr.Zero;
        try
        {
            if (Password is null || previousWasWrong)
            {
                var answer = interaction?.AskPassword(archivePath, previousWasWrong);
                if (answer is null)
                {
                    UserCancelled = true;
                    return HResult.E_ABORT;
                }
                Password = answer;
                previousWasWrong = false;
            }
            password = Marshal.StringToBSTR(Password);
            return HResult.S_OK;
        }
        catch
        {
            return HResult.E_FAIL;
        }
    }
}

/// <summary>Receives extracted data from 7-Zip and writes it to disk safely.</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class ExtractCallback : IArchiveExtractCallback, ICryptoGetTextPassword
{
    private enum ItemState { None, Directory, Writing, Skipped, Failed, Testing }

    private readonly string _archivePath;
    private readonly IReadOnlyDictionary<uint, (ArchiveEntry Entry, string FullPath)> _plan;
    private readonly bool _testMode;
    private readonly OverwriteMode _overwriteMode;
    private readonly IArchiveInteraction? _interaction;
    private readonly IProgress<ArchiveProgress>? _progress;
    private readonly CancellationToken _ct;
    private readonly ExtractResult _result;

    private OverwriteChoice? _rememberedChoice;
    private ulong _total;
    private ulong _completed;
    private string? _currentName;

    private ItemState _state;
    private uint _currentIndex;
    private string? _currentPath;
    private ArchiveEntry? _currentEntry;
    private FileStream? _file;
    private OutStreamWrapper? _out;

    public string? Password { get; private set; }
    public bool PasswordWasAsked { get; private set; }
    public bool UserCancelled { get; private set; }
    public Exception? FatalError { get; private set; }
    public List<uint> WrongPasswordIndices { get; } = [];
    public List<(string FullPath, DateTime Modified)> Directories { get; } = [];

    public ExtractCallback(
        string archivePath,
        IReadOnlyDictionary<uint, (ArchiveEntry, string)> plan,
        bool testMode,
        OverwriteMode overwriteMode,
        string? password,
        IArchiveInteraction? interaction,
        IProgress<ArchiveProgress>? progress,
        ExtractResult result,
        CancellationToken ct)
    {
        _archivePath = archivePath;
        _plan = plan;
        _testMode = testMode;
        _overwriteMode = overwriteMode;
        Password = password;
        _interaction = interaction;
        _progress = progress;
        _result = result;
        _ct = ct;
    }

    public int SetTotal(ulong total)
    {
        _total = total;
        return HResult.S_OK;
    }

    public int SetCompleted(IntPtr completeValue)
    {
        if (completeValue != IntPtr.Zero)
            _completed = (ulong)Marshal.ReadInt64(completeValue);
        _progress?.Report(new ArchiveProgress(_completed, _total, _currentName));
        return _ct.IsCancellationRequested ? HResult.E_ABORT : HResult.S_OK;
    }

    public int GetStream(uint index, out ISequentialOutStream? outStream, AskMode askExtractMode)
    {
        outStream = null;
        _state = ItemState.None;
        _currentIndex = index;
        _currentPath = null;
        _currentEntry = null;

        if (!_plan.TryGetValue(index, out var item))
            return HResult.S_OK;

        _currentEntry = item.Entry;
        _currentName = item.Entry.Path;

        if (_testMode || askExtractMode != AskMode.Extract)
        {
            _state = askExtractMode == AskMode.Test ? ItemState.Testing : ItemState.None;
            return HResult.S_OK;
        }

        try
        {
            var full = item.FullPath;
            if (item.Entry.IsDirectory)
            {
                Directory.CreateDirectory(full);
                if (item.Entry.Modified is { } dirTime)
                    Directories.Add((full, dirTime));
                _state = ItemState.Directory;
                return HResult.S_OK;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            if (File.Exists(full) || Directory.Exists(full))
            {
                switch (DecideOverwrite(full, item.Entry))
                {
                    case OverwriteChoice.Skip:
                        _state = ItemState.Skipped;
                        _result.Skipped++;
                        return HResult.S_OK;
                    case OverwriteChoice.Rename:
                        full = NamePolicy.GetUniqueFilePath(full);
                        break;
                    case OverwriteChoice.Cancel:
                        UserCancelled = true;
                        return HResult.E_ABORT;
                    case OverwriteChoice.Overwrite:
                        if (Directory.Exists(full))
                        {
                            Fail(item.Entry.Path, ArchiveErrorKind.Io, "같은 이름의 폴더가 있어 파일을 만들 수 없습니다");
                            return HResult.S_OK;
                        }
                        var attr = File.GetAttributes(full);
                        if ((attr & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
                            File.SetAttributes(full, FileAttributes.Normal);
                        break;
                }
            }

            _file = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            _out = new OutStreamWrapper(_file);
            _currentPath = full;
            _state = ItemState.Writing;
            outStream = _out;
            return HResult.S_OK;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Fail(item.Entry.Path, ArchiveErrorKind.Io, e.Message);
            return HResult.S_OK; // keep going with the other files
        }
        catch (Exception e)
        {
            FatalError = e;
            return HResult.E_FAIL;
        }
    }

    private OverwriteChoice DecideOverwrite(string existing, ArchiveEntry entry)
    {
        if (_rememberedChoice is { } remembered)
            return remembered;

        switch (_overwriteMode)
        {
            case OverwriteMode.Overwrite: return OverwriteChoice.Overwrite;
            case OverwriteMode.Skip: return OverwriteChoice.Skip;
            case OverwriteMode.Rename: return OverwriteChoice.Rename;
        }

        if (_interaction is null)
            return OverwriteChoice.Skip;

        var info = new FileInfo(existing);
        var decision = _interaction.AskOverwrite(new OverwriteRequest(
            existing,
            info.Exists ? info.Length : 0,
            info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue,
            entry.Size,
            entry.Modified));
        if (decision.ApplyToAll && decision.Choice != OverwriteChoice.Cancel)
            _rememberedChoice = decision.Choice;
        return decision.Choice;
    }

    public int PrepareOperation(AskMode askExtractMode) => HResult.S_OK;

    public int SetOperationResult(OperationResult result)
    {
        var entry = _currentEntry;
        var path = _currentPath;
        var state = _state;
        CloseFile();

        try
        {
            if (entry is null || state is ItemState.Skipped or ItemState.Failed or ItemState.None)
                return HResult.S_OK;

            if (_out?.Error is { } writeError)
            {
                TryDelete(path);
                FatalError = new ArchiveException(ArchiveErrorKind.Io,
                    $"파일을 쓰지 못했습니다 (디스크 공간이 부족할 수 있습니다): {writeError.Message}", writeError);
                return HResult.E_FAIL;
            }

            if (result == OperationResult.Ok)
            {
                if (state == ItemState.Writing && path is not null)
                {
                    ApplyMetadata(path, entry);
                    _result.FilesWritten++;
                }
                else if (state == ItemState.Testing)
                {
                    _result.FilesTested++;
                }
                return HResult.S_OK;
            }

            if (state == ItemState.Writing)
                TryDelete(path);

            bool maybePassword = entry.IsEncrypted &&
                result is OperationResult.WrongPassword or OperationResult.DataError or OperationResult.CrcError;
            if (maybePassword)
            {
                WrongPasswordIndices.Add(_currentIndex);
                Fail(entry.Path, ArchiveErrorKind.WrongPassword, "암호가 맞지 않습니다");
            }
            else
            {
                Fail(entry.Path, MapKind(result), Describe(result));
            }
            return HResult.S_OK;
        }
        finally
        {
            _out = null;
            _state = ItemState.None;
        }
    }

    public int CryptoGetTextPassword(out IntPtr password)
    {
        password = IntPtr.Zero;
        try
        {
            if (Password is null)
            {
                PasswordWasAsked = true;
                var answer = _interaction?.AskPassword(_archivePath, false);
                if (answer is null)
                {
                    UserCancelled = true;
                    return HResult.E_ABORT;
                }
                Password = answer;
            }
            password = Marshal.StringToBSTR(Password);
            return HResult.S_OK;
        }
        catch (Exception e)
        {
            FatalError = e;
            return HResult.E_FAIL;
        }
    }

    private void Fail(string path, ArchiveErrorKind kind, string message)
    {
        _state = ItemState.Failed;
        _result.Failures.Add(new ItemFailure(path, kind, message));
    }

    private void CloseFile()
    {
        try { _file?.Dispose(); }
        catch (IOException e) { FatalError ??= e; }
        _file = null;
    }

    private static void ApplyMetadata(string path, ArchiveEntry entry)
    {
        try
        {
            if (entry.Modified is { } m)
                File.SetLastWriteTimeUtc(path, m);
            // Keep only the harmless attributes (read-only, hidden, archive); never "system".
            var attr = (FileAttributes)(entry.Attributes & 0x23);
            if (attr != 0 && (entry.Attributes & 0x8000) == 0) // 0x8000: unix permissions in high bits
                File.SetAttributes(path, attr);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Times and attributes are best effort.
        }
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static ArchiveErrorKind MapKind(OperationResult r) => r switch
    {
        OperationResult.UnsupportedMethod => ArchiveErrorKind.Unsupported,
        OperationResult.WrongPassword => ArchiveErrorKind.WrongPassword,
        _ => ArchiveErrorKind.Corrupted,
    };

    private static string Describe(OperationResult r) => r switch
    {
        OperationResult.UnsupportedMethod => "지원하지 않는 압축 방식입니다",
        OperationResult.DataError => "데이터가 손상되었습니다",
        OperationResult.CrcError => "파일 검사값(CRC)이 맞지 않습니다. 손상된 파일입니다",
        OperationResult.Unavailable => "필요한 데이터를 찾을 수 없습니다 (분할 파일 일부가 없을 수 있습니다)",
        OperationResult.UnexpectedEnd => "압축 파일이 중간에 끊겼습니다",
        OperationResult.DataAfterEnd => "압축 파일 끝에 알 수 없는 데이터가 있습니다",
        OperationResult.IsNotArc => "압축 파일 형식이 아닙니다",
        OperationResult.HeadersError => "압축 파일 머리 정보가 손상되었습니다",
        OperationResult.WrongPassword => "암호가 맞지 않습니다",
        _ => $"알 수 없는 오류 ({(int)r})",
    };
}

internal sealed record UpdateItem(
    string? SourcePath,
    string ArchivePath,
    bool IsDirectory,
    ulong Size,
    FileAttributes Attributes,
    DateTime CreatedUtc,
    DateTime ModifiedUtc,
    DateTime AccessedUtc);

/// <summary>Feeds files to 7-Zip when creating an archive.</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class UpdateCallback(
    IReadOnlyList<UpdateItem> items,
    string? password,
    IProgress<ArchiveProgress>? progress,
    CancellationToken ct) : IArchiveUpdateCallback, ICryptoGetTextPassword2
{
    private readonly object _lock = new();
    private readonly List<InStreamWrapper> _open = [];
    private ulong _total;
    private string? _currentName;

    public Exception? FatalError { get; private set; }

    /// <summary>
    /// Closes every file still open and reports read errors. ZIP compresses several files on
    /// worker threads at once, so streams cannot be closed one by one in SetOperationResult.
    /// </summary>
    public void Complete()
    {
        lock (_lock)
        {
            foreach (var s in _open)
            {
                if (s.Error is { } readError)
                    FatalError ??= new ArchiveException(ArchiveErrorKind.Io, $"파일을 읽는 중 오류가 났습니다: {readError.Message}", readError);
                s.Dispose();
            }
            _open.Clear();
        }
    }

    public int SetTotal(ulong total)
    {
        _total = total;
        return HResult.S_OK;
    }

    public int SetCompleted(IntPtr completeValue)
    {
        ulong done = completeValue == IntPtr.Zero ? 0 : (ulong)Marshal.ReadInt64(completeValue);
        progress?.Report(new ArchiveProgress(done, _total, _currentName));
        return ct.IsCancellationRequested ? HResult.E_ABORT : HResult.S_OK;
    }

    public int GetUpdateItemInfo(uint index, out int newData, out int newProps, out uint indexInArchive)
    {
        newData = 1;
        newProps = 1;
        indexInArchive = uint.MaxValue;
        return HResult.S_OK;
    }

    public int GetProperty(uint index, ItemPropId propId, IntPtr value)
    {
        try
        {
            var item = items[(int)index];
            var pv = propId switch
            {
                ItemPropId.Path => PropVariant.FromString(item.ArchivePath),
                ItemPropId.IsDir => PropVariant.FromBool(item.IsDirectory),
                ItemPropId.Size => PropVariant.FromUInt64(item.Size),
                ItemPropId.Attrib => PropVariant.FromUInt32((uint)item.Attributes),
                ItemPropId.MTime => PropVariant.FromFileTime(item.ModifiedUtc),
                ItemPropId.CTime => PropVariant.FromFileTime(item.CreatedUtc),
                ItemPropId.ATime => PropVariant.FromFileTime(item.AccessedUtc),
                ItemPropId.IsAnti => PropVariant.FromBool(false),
                _ => default,
            };
            Marshal.StructureToPtr(pv, value, false);
            return HResult.S_OK;
        }
        catch (Exception e)
        {
            FatalError = e;
            return HResult.E_FAIL;
        }
    }

    public int GetStream(uint index, out ISequentialInStream? inStream)
    {
        inStream = null;
        var item = items[(int)index];
        _currentName = item.ArchivePath;
        if (item.IsDirectory || item.SourcePath is null)
            return HResult.S_OK;
        try
        {
            var source = item.SourcePath;
            var wrapper = new InStreamWrapper(() => new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan));
            lock (_lock) _open.Add(wrapper);
            inStream = wrapper;
            return HResult.S_OK;
        }
        catch (Exception e)
        {
            FatalError = new ArchiveException(ArchiveErrorKind.Io,
                $"파일을 읽을 수 없습니다 (다른 프로그램이 사용 중일 수 있습니다): {item.SourcePath}", e);
            return HResult.E_FAIL;
        }
    }

    public int SetOperationResult(OperationResult result) => HResult.S_OK;

    public int CryptoGetTextPassword2(out int passwordIsDefined, out IntPtr pw)
    {
        passwordIsDefined = password is null ? 0 : 1;
        pw = Marshal.StringToBSTR(password ?? "");
        return HResult.S_OK;
    }
}
