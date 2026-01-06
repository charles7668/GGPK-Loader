using System.Threading.Tasks;

namespace GGPK_Loader.Services;

public interface IFileService
{
    Task<string?> OpenFileAsync();
}