using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace GGPK_Loader.Services;

public class FileService(Window target) : IFileService
{
    public async Task<string?> OpenFileAsync()
    {
        var topLevel = TopLevel.GetTopLevel(target);
        if (topLevel == null)
        {
            return null;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select GGPK File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("GGPK Files") { Patterns = new[] { "*.ggpk" } },
                FilePickerFileTypes.All
            }
        });

        return files.Count >= 1 ? files[0].Path.LocalPath : null;
    }
}