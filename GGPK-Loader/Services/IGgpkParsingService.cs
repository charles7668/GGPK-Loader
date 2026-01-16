using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using GGPK_Loader.Models;

namespace GGPK_Loader.Services;

public interface IGgpkParsingService
{
    void OpenStream(string filePath);
    void CloseStream();
    Task<GGPKTreeNode> BuildGgpkTreeAsync();
    Task<GGPKTreeNode?> BuildBundleTreeAsync(GGPKTreeNode ggpkRootNode, string ggpkFilePath);

    Task<byte[]> LoadBundleFileDataAsync(GGPKFileInfo ggpkBundleFileInfo, BundleIndexInfo.FileRecord bundleFileRecord,
        CancellationToken ct);

    Task<byte[]> LoadGGPKFileDataAsync(GGPKFileInfo ggpkFileInfo, CancellationToken ct);
    Task<byte[]> LoadGGPKFileDataAsync(GGPKFileInfo ggpkFileInfo, ulong size, CancellationToken ct);

    Task ReplaceFileDataAsync(ReadOnlyMemory<byte> replaceSource, GGPKTreeNode targetNode, CancellationToken ct);
}