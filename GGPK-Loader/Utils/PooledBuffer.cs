using System;
using System.Buffers;

namespace GGPK_Loader.Utils;

public readonly struct PooledBuffer(int size) : IDisposable
{
    public byte[] Data { get; } = ArrayPool<byte>.Shared.Rent(size);

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(Data);
    }
}