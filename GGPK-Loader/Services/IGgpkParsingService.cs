using System.Threading.Tasks;
using GGPK_Loader.Models;

namespace GGPK_Loader.Services;

public interface IGgpkParsingService
{
    Task<GGPKTreeNode> BuildGgpkTreeAsync(string filePath);
    Task<GGPKTreeNode?> BuildBundleTreeAsync(GGPKTreeNode ggpkRootNode, string ggpkFilePath);
    Task<byte[]> LoadBundleFileDataAsync(string ggpkFilePath, ulong bundleOffset, BundleIndexInfo.FileRecord fileRecord);
}