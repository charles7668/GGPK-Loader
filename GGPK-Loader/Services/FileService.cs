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
            Title = "Select File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("GGPK Files") { Patterns = new[] { "*.*" } },
                FilePickerFileTypes.All
            }
        });

        return files.Count >= 1 ? files[0].Path.LocalPath : null;
    }

    public async Task<string?> SaveFileAsync(string title, string defaultFileName, string extension)
    {
        var topLevel = TopLevel.GetTopLevel(target);
        if (topLevel == null)
        {
            return null;
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = defaultFileName,
            DefaultExtension = extension,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Text Files") { Patterns = new[] { $"*.{extension}" } },
                FilePickerFileTypes.All
            }
        });

        return file?.Path.LocalPath;
    }
}