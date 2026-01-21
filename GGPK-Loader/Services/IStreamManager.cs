using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GGPK_Loader.Services;

public interface IStreamManager : IDisposable
{
    Stream? Stream { get; }
    void Open(string filePath);
    void Close();
    Task ReadAtAsync(Memory<byte> buffer, long offset, CancellationToken ct = default);
    Task ExecuteWriteTransactionAsync(Func<FileStream, CancellationToken, Task> action, CancellationToken ct = default);
}