using System.Runtime.InteropServices;

namespace DdoddoZip.Core.Interop;

/// <summary>Exposes a .NET stream to 7-Zip for reading (archive input, files to compress).</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class InStreamWrapper : IInStream, ISequentialInStream, IDisposable
{
    private readonly Func<Stream>? _reopen;
    private Stream _stream;
    private long _closedLength;
    private bool _closed;

    public InStreamWrapper(Stream stream) => _stream = stream;

    /// <summary>
    /// Stream that releases its file handle once 7-Zip has read to the end, and reopens the file
    /// if 7-Zip seeks back later (ZIP re-reads a file to store it when compression did not help).
    /// </summary>
    public InStreamWrapper(Func<Stream> open)
    {
        _reopen = open;
        _stream = open();
    }

    public Exception? Error { get; private set; }

    public unsafe int Read(IntPtr data, uint size, IntPtr processedSize)
    {
        try
        {
            int n = _closed || size == 0 ? 0 : _stream.Read(new Span<byte>((void*)data, (int)Math.Min(size, int.MaxValue)));
            if (n == 0 && size != 0 && _reopen is not null && !_closed)
            {
                _closed = true;
                _closedLength = _stream.Length;
                _stream.Dispose();
            }
            if (processedSize != IntPtr.Zero)
                Marshal.WriteInt32(processedSize, n);
            return HResult.S_OK;
        }
        catch (Exception e)
        {
            Error = e;
            if (processedSize != IntPtr.Zero)
                Marshal.WriteInt32(processedSize, 0);
            return HResult.E_FAIL;
        }
    }

    public int Seek(long offset, uint seekOrigin, IntPtr newPosition)
    {
        try
        {
            if (_closed)
            {
                // Asking where the end is does not need the file; anything else reopens it.
                if ((SeekOrigin)seekOrigin == SeekOrigin.End && offset == 0)
                {
                    if (newPosition != IntPtr.Zero)
                        Marshal.WriteInt64(newPosition, _closedLength);
                    return HResult.S_OK;
                }
                _stream = _reopen!();
                _closed = false;
                if ((SeekOrigin)seekOrigin == SeekOrigin.Current)
                {
                    offset += _closedLength;
                    seekOrigin = (uint)SeekOrigin.Begin;
                }
            }
            long pos = _stream.Seek(offset, (SeekOrigin)seekOrigin);
            if (newPosition != IntPtr.Zero)
                Marshal.WriteInt64(newPosition, pos);
            return HResult.S_OK;
        }
        catch (Exception e)
        {
            Error = e;
            return HResult.E_FAIL;
        }
    }

    public void Dispose()
    {
        _closed = true;
        _stream.Dispose();
    }
}

/// <summary>Exposes a .NET stream to 7-Zip for writing (new archive, extracted files).</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class OutStreamWrapper(Stream stream) : IOutStream, ISequentialOutStream, IDisposable
{
    public Exception? Error { get; private set; }

    public unsafe int Write(IntPtr data, uint size, IntPtr processedSize)
    {
        try
        {
            stream.Write(new ReadOnlySpan<byte>((void*)data, (int)Math.Min(size, int.MaxValue)));
            if (processedSize != IntPtr.Zero)
                Marshal.WriteInt32(processedSize, (int)size);
            return HResult.S_OK;
        }
        catch (Exception e)
        {
            Error = e;
            if (processedSize != IntPtr.Zero)
                Marshal.WriteInt32(processedSize, 0);
            return HResult.E_FAIL;
        }
    }

    public int Seek(long offset, uint seekOrigin, IntPtr newPosition)
    {
        try
        {
            long pos = stream.Seek(offset, (SeekOrigin)seekOrigin);
            if (newPosition != IntPtr.Zero)
                Marshal.WriteInt64(newPosition, pos);
            return HResult.S_OK;
        }
        catch (Exception e)
        {
            Error = e;
            return HResult.E_FAIL;
        }
    }

    public int SetSize(ulong newSize)
    {
        try
        {
            stream.SetLength((long)newSize);
            return HResult.S_OK;
        }
        catch (Exception e)
        {
            Error = e;
            return HResult.E_FAIL;
        }
    }

    public void Dispose() => stream.Dispose();
}
