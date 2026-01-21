using System;
using System.Threading;
using System.Threading.Tasks;
using GGPK_Loader.Models;

namespace GGPK_Loader.Services;

public interface IGgpkParsingService
{
    void OpenStream(string filePath);
    void CloseStream();
    Task<GGPKTreeNode> BuildGgpkTreeAsync();

    Task<byte[]> LoadGGPKFileDataAsync(GGPKFileInfo ggpkFileInfo, CancellationToken ct);
    Task<byte[]> LoadGGPKFileDataAsync(GGPKFileInfo ggpkFileInfo, ulong size, CancellationToken ct);

    Task ReplaceFileDataAsync(ReadOnlyMemory<byte> replaceSource, GGPKTreeNode targetNode, CancellationToken ct);
}