using System;
using System.Threading;
using System.Threading.Tasks;
using GGPK_Loader.Models;

namespace GGPK_Loader.Services;

public interface IGgpkBundleService
{
    Task<GGPKTreeNode?> BuildBundleTreeAsync(GGPKTreeNode ggpkRootNode, string ggpkFilePath);

    Task<byte[]> LoadBundleFileDataAsync(GGPKFileInfo ggpkBundleFileInfo,
        BundleIndexInfo.FileRecord bundleFileRecord, CancellationToken ct);

    Task<byte[]> ReplaceBundleFileContentAsync(GGPKFileInfo ggpkBundleFileInfo,
        BundleIndexInfo.FileRecord bundleFileRecord, ReadOnlyMemory<byte> newContent, CancellationToken ct);

    Task UpdateBundleIndexAsync(BundleIndexInfo bundleIndexInfo, GGPKTreeNode indexNode,
        CancellationToken ct);
}