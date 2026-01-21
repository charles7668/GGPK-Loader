using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GGPK_Loader.Services;

public sealed class StreamManager : IStreamManager
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private string? _filePath;

    private bool IsOpen => Stream != null;
    public Stream? Stream { get; private set; }

    public void Dispose()
    {
        Stream?.Dispose();
        _semaphore.Dispose();
    }

    public void Open(string filePath)
    {
        if (Stream != null)
        {
            Close();
        }

        _filePath = filePath;
        Stream = File.OpenRead(filePath);
    }

    public void Close()
    {
        Stream?.Dispose();
        Stream = null;
        _filePath = null;
    }

    public async Task ReadAtAsync(Memory<byte> buffer, long offset, CancellationToken ct = default)
    {
        ThrowIfStreamNotOpen();
        var stream = Stream!;
        await _semaphore.WaitAsync(ct);
        try
        {
            if (stream is FileStream fs)
            {
                await RandomAccess.ReadAsync(fs.SafeFileHandle, buffer, offset, ct);
            }
            else
            {
                stream.Seek(offset, SeekOrigin.Begin);
                await stream.ReadExactlyAsync(buffer, ct);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task ExecuteWriteTransactionAsync(Func<FileStream, CancellationToken, Task> action,
        CancellationToken ct = default)
    {
        ThrowIfStreamNotOpen();

        var path = _filePath!;

        await _semaphore.WaitAsync(ct);
        try
        {
            // Close existing read stream
            await (Stream?.DisposeAsync() ?? default);
            Stream = null;

            // Perform write operations
            await using var fs = File.OpenWrite(path);
            await action(fs, ct);
        }
        finally
        {
            // Reopen read stream
            Stream = File.OpenRead(path);
            _semaphore.Release();
        }
    }

    private void ThrowIfStreamNotOpen()
    {
        if (IsOpen)
        {
            return;
        }

        throw new InvalidOperationException("GGPK stream is not open");
    }
}