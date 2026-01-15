using System.Threading.Tasks;

namespace GGPK_Loader.Services;

public interface IFileService
{
    Task<string?> OpenFileAsync();
    Task<string?> SaveFileAsync(string title, string defaultFileName, string extension);
}