using System.Collections.Concurrent;

namespace DdoddoZip.Core.Interop;

/// <summary>
/// Writes small extracted files to disk on a few background threads.
///
/// Creating a file is the slow part on a PC with antivirus/EDR: about 1.5 ms per file on the
/// development PC, while writing and closing are fast. 7-Zip hands files over one by one, so
/// small files are decoded into memory and created here in parallel
/// (5,000 files: 11 s in line, 2.2 s with 4 threads in a raw test).
/// </summary>
internal sealed class BackgroundWriter : IDisposable
{
    /// <summary>Files up to this size are buffered in memory and written in the background.</summary>
    public const long SmallFileLimit = 256 * 1024;

    private readonly BlockingCollection<Job> _queue = new(boundedCapacity: 256); // at most 64 MB waiting
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<(ArchiveEntry Entry, Exception Error)> _failures = new();
    private readonly Task[] _workers;
    private bool _disposed;

    private sealed record Job(string Path, ArchiveEntry Entry, MemoryStream Data);

    /// <summary>Set when the disk is full: extraction should stop instead of failing every file.</summary>
    public Exception? DiskFull { get; private set; }

    public BackgroundWriter(int workers = 4)
    {
        _workers = new Task[workers];
        for (int i = 0; i < workers; i++)
            _workers[i] = Task.Factory.StartNew(Work, TaskCreationOptions.LongRunning);
    }

    public void Write(string path, ArchiveEntry entry, MemoryStream data)
    {
        _pending[path] = 0;
        _queue.Add(new Job(path, entry, data));
    }

    public bool IsPending(string path) => _pending.ContainsKey(path);

    /// <summary>Blocks while a file with this path is still waiting to be written.</summary>
    public void WaitFor(string path)
    {
        var spin = new SpinWait();
        while (_pending.ContainsKey(path))
            spin.SpinOnce(sleep1Threshold: 10);
    }

    /// <summary>Blocks until nothing is waiting (before picking a new unique name).</summary>
    public void WaitIdle()
    {
        var spin = new SpinWait();
        while (!_pending.IsEmpty)
            spin.SpinOnce(sleep1Threshold: 10);
    }

    private void Work()
    {
        foreach (var job in _queue.GetConsumingEnumerable())
        {
            try
            {
                using (var file = new FileStream(job.Path, FileMode.Create, FileAccess.Write, FileShare.None, 1))
                {
                    job.Data.Position = 0;
                    job.Data.CopyTo(file);
                    file.Flush();
                    if (job.Entry.Modified is { } m)
                    {
                        try { File.SetLastWriteTimeUtc(file.SafeFileHandle, m); }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
                    }
                }
                ExtractCallback.ApplyAttributes(job.Path, job.Entry);
            }
            catch (Exception e) // any error: a dead worker would leave the queue full and extraction stuck
            {
                _failures.Enqueue((job.Entry, e));
                if (e is IOException { HResult: unchecked((int)0x80070070) or unchecked((int)0x80070027) })
                    DiskFull ??= e;
                try { File.Delete(job.Path); } catch (Exception) { }
            }
            finally
            {
                _pending.TryRemove(job.Path, out _);
                job.Data.Dispose();
            }
        }
    }

    /// <summary>Waits for every queued file, then returns the ones that could not be written.</summary>
    public IReadOnlyCollection<(ArchiveEntry Entry, Exception Error)> Finish()
    {
        Dispose();
        return _failures;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        Task.WaitAll(_workers);
        _queue.Dispose();
    }
}

/// <summary>
/// Write-only buffer for one small extracted file. It stays in memory up to <paramref name="limit"/>;
/// if more data arrives (the size stored in an archive can be wrong or forged), everything moves to
/// the real file on disk so a crafted archive cannot fill the memory.
/// </summary>
internal sealed class SpillStream(string path, long limit) : Stream
{
    private MemoryStream? _memory = new();

    /// <summary>The file on disk, once the data outgrew the memory limit.</summary>
    public FileStream? File { get; private set; }

    /// <summary>Hands the in-memory data to the caller, who then owns it.</summary>
    public MemoryStream TakeMemory()
    {
        var m = _memory ?? throw new InvalidOperationException("The data already moved to disk.");
        _memory = null;
        return m;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (File is null && _memory is not null && _memory.Length + buffer.Length > limit)
        {
            var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            _memory.Position = 0;
            _memory.CopyTo(file);
            _memory.Dispose();
            _memory = null;
            File = file;
        }
        if (File is not null) File.Write(buffer);
        else (_memory ?? throw new ObjectDisposedException(nameof(SpillStream))).Write(buffer);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Flush() => File?.Flush();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _memory?.Dispose();
            _memory = null;
            File?.Dispose();
        }
        base.Dispose(disposing);
    }
}
