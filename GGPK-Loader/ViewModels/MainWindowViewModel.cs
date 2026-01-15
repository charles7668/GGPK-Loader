using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GGPK_Loader.Models;
using GGPK_Loader.Services;
using GGPK_Loader.Utils;
using JetBrains.Annotations;

namespace GGPK_Loader.ViewModels;

[method: UsedImplicitly]
public partial class MainWindowViewModel(
    IFileService fileService,
    IMessageService messageService,
    IGgpkParsingService ggpkParsingService) : ViewModelBase
{
    // For Design Time Preview
    public MainWindowViewModel() : this(null!, null!, null!)
    {
        // Design-time constructor
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadTreeStructureCommand))]
    private bool _isLoading;

    [ObservableProperty]
    private string _buttonText = "Open";

    [ObservableProperty]
    private ObservableCollection<GGPKTreeNode> _ggpkNodes = new();

    private CancellationTokenSource? _loadingFileCts;
    private Task _loadingFileTask = Task.CompletedTask;

    [ObservableProperty]
    private string _nodeInfoText = "";

    [ObservableProperty]
    private GGPKTreeNode? _selectedBundleInfoNode;

    [ObservableProperty]
    private Bitmap? _selectedImage;

    [ObservableProperty]
    private GGPKTreeNode? _selectedNode;

    public ObservableCollection<GGPKTreeNode> BundleTreeItems { get; } = new();

    partial void OnSelectedBundleInfoNodeChanged(GGPKTreeNode? value)
    {
        if (BundleTreeItems.Count == 0 || BundleTreeItems[0].Value is not BundleIndexInfo bundleIndexInfo)
        {
            return;
        }

        if (value?.Value is not BundleIndexInfo.FileRecord fileRecord ||
            fileRecord.BundleIndex >= bundleIndexInfo.Bundles.Length)
        {
            return;
        }

        var bundleName = bundleIndexInfo.Bundles[fileRecord.BundleIndex].Name;
        NodeInfoText = $"\nBundle: {bundleName}";

        var foundNode = FindGgpkNode(bundleName + ".bundle.bin");
        if (foundNode?.Value is GGPKFileInfo ggpkFileInfo)
        {
            var (oldCts, token) = ResetCancellationSource();
            _loadingFileTask =
                ChainBundleFileLoadingTask(_loadingFileTask, oldCts, token, ggpkFileInfo, fileRecord);
        }
    }

    private GGPKTreeNode? FindGgpkNode(string bundleName)
    {
        if (GgpkNodes.Count == 0)
        {
            return null;
        }

        var parts = bundleName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentNode = GgpkNodes[0].Children.FirstOrDefault(child => child.Value.ToString() == "Bundles2");
        if (currentNode == null)
        {
            return null;
        }

        foreach (var part in parts)
        {
            var match = currentNode.Children.FirstOrDefault(child => child.Value.ToString() == part);

            if (match == null)
            {
                return null;
            }

            currentNode = match;
        }

        return currentNode;
    }

    partial void OnSelectedNodeChanged(GGPKTreeNode? value)
    {
        if (!ShouldProcessNode(value, out var nodeValue))
        {
            return;
        }

        var (oldCts, token) = ResetCancellationSource();
        SelectedImage = null;
        var fileInfo = nodeValue!;
        NodeInfoText = fileInfo.FileName;
        if (IsImageFile(fileInfo.FileName))
        {
            _loadingFileTask = ChainImageLoadingTask(_loadingFileTask, oldCts, token, fileInfo);
            return;
        }

        if (IsTextFile(fileInfo.FileName))
        {
            _loadingFileTask = ChainTextLoadingTask(_loadingFileTask, oldCts, token, fileInfo);
            return;
        }

        _loadingFileTask = ChainFileInformationLoadingTask(_loadingFileTask, oldCts, token, fileInfo);
        // Cleanup if not starting a new task
        _loadingFileTask = _loadingFileTask.ContinueWith(_ => oldCts?.Dispose(), TaskScheduler.Default);
    }

    private (CancellationTokenSource? oldCts, CancellationToken token) ResetCancellationSource()
    {
        var oldCts = _loadingFileCts;
        oldCts?.Cancel();
        _loadingFileCts = new CancellationTokenSource();
        return (oldCts, _loadingFileCts.Token);
    }

    private static bool ShouldProcessNode(GGPKTreeNode? node, out GGPKFileInfo? fileInfo)
    {
        fileInfo = null;
        if (node?.Value is not GGPKFileInfo info)
        {
            return false;
        }

        fileInfo = info;
        return true;
    }

    private static bool IsImageFile(string fileName)
    {
        return fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase);
    }

    private Task ChainImageLoadingTask(Task previousTask, CancellationTokenSource? oldCts, CancellationToken token,
        GGPKFileInfo fileInfo)
    {
        return previousTask.ContinueWith(async _ =>
        {
            oldCts?.Dispose();
            if (token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await LoadImageAsync(fileInfo, token);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load image: {ex.Message}");
            }
        }, TaskScheduler.Default).Unwrap();
    }

    private async Task LoadImageAsync(GGPKFileInfo fileInfo, CancellationToken token)
    {
        var imageData = await ggpkParsingService.LoadGGPKFileDataAsync(fileInfo, token);
        var ms = new MemoryStream(imageData);
        var image = new Bitmap(ms);
        Dispatcher.UIThread.Post(() =>
        {
            SelectedImage?.Dispose();
            SelectedImage = image;
        });
    }

    private static bool IsCompressedFormat(DirectXTex.DXGI_FORMAT format, out DirectXTex.DXGI_FORMAT targetFormat)
    {
        switch (format)
        {
            case DirectXTex.DXGI_FORMAT.BC1_TYPELESS:
            case DirectXTex.DXGI_FORMAT.BC1_UNORM:
            case DirectXTex.DXGI_FORMAT.BC1_UNORM_SRGB:
            case DirectXTex.DXGI_FORMAT.BC2_TYPELESS:
            case DirectXTex.DXGI_FORMAT.BC2_UNORM:
            case DirectXTex.DXGI_FORMAT.BC2_UNORM_SRGB:
            case DirectXTex.DXGI_FORMAT.BC3_TYPELESS:
            case DirectXTex.DXGI_FORMAT.BC3_UNORM:
            case DirectXTex.DXGI_FORMAT.BC3_UNORM_SRGB:
            case DirectXTex.DXGI_FORMAT.BC4_SNORM:
            case DirectXTex.DXGI_FORMAT.BC4_TYPELESS:
            case DirectXTex.DXGI_FORMAT.BC4_UNORM:
            case DirectXTex.DXGI_FORMAT.BC5_SNORM:
            case DirectXTex.DXGI_FORMAT.BC5_TYPELESS:
            case DirectXTex.DXGI_FORMAT.BC5_UNORM:
            case DirectXTex.DXGI_FORMAT.BC7_TYPELESS:
            case DirectXTex.DXGI_FORMAT.BC7_UNORM:
            case DirectXTex.DXGI_FORMAT.BC7_UNORM_SRGB:
                targetFormat = DirectXTex.DXGI_FORMAT.R8G8B8A8_UNORM;
                return true;
            default:
                targetFormat = format;
                return false;
        }
    }

    private async Task<Bitmap?> ResolveDDSDataAsync(byte[] data)
    {
        var scratchImage = new DirectXTex.ScratchImage();
        var decompressedScratch = new DirectXTex.ScratchImage();
        try
        {
            var hr = DirectXTex.GetMetadataFromDDSMemory(data, 0, out var metadata);
            if (hr != 0)
            {
                await messageService.ShowErrorMessageAsync("Failed to resolve DDS data");
                return null;
            }

            Debug.WriteLine($"Metadata Width: {metadata.width}, Height: {metadata.height}, Format: {metadata.format}");

            hr = DirectXTex.LoadFromDDSMemory(data, 0, ref metadata, ref scratchImage);
            if (hr != 0)
            {
                await messageService.ShowErrorMessageAsync("Failed to resolve DDS data");
                return null;
            }

            Debug.WriteLine($"Load Result: {hr}, Images: {scratchImage.nimages}, Size: {scratchImage.size}");
            if (scratchImage.nimages > 0)
            {
                var image = Marshal.PtrToStructure<DirectXTex.Image>(scratchImage.image);
                Debug.WriteLine(
                    $"Image Width: {image.width}, Height: {image.height}, Format: {image.format}, RowPitch: {image.rowPitch}");

                var isCompressed = IsCompressedFormat(image.format, out var targetFormat);

                if (isCompressed)
                {
                    Debug.WriteLine(
                        $"Format {image.format} is compressed. Attempting to decompress to {targetFormat}...");
                    hr = DirectXTex.Decompress(ref image, targetFormat, ref decompressedScratch);
                    if (hr >= 0)
                    {
                        Debug.WriteLine("Decompression successful.");
                        image = Marshal.PtrToStructure<DirectXTex.Image>(decompressedScratch.image);
                    }
                    else
                    {
                        Debug.WriteLine($"Decompression failed with HRESULT {hr:X}");
                        await messageService.ShowErrorMessageAsync($"Decompression failed with HRESULT {hr:X}");
                        return null;
                    }
                }

                if (image.format == targetFormat)
                {
                    var bitmap = new WriteableBitmap(
                        new PixelSize((int)image.width, (int)image.height),
                        new Vector(96, 96),
                        PixelFormat.Bgra8888,
                        AlphaFormat.Premul);

                    using var buffer = bitmap.Lock();
                    var dest = buffer.Address;
                    var src = image.pixels;
                    var height = (int)image.height;
                    var rowPitch = (int)image.rowPitch;

                    for (var i = 0; i < height; i++)
                    {
                        unsafe
                        {
                            Buffer.MemoryCopy((void*)(src + i * rowPitch), (void*)(dest + i * rowPitch),
                                rowPitch, rowPitch);
                        }
                    }

                    return bitmap;
                }
                else
                {
                    Debug.WriteLine(
                        $"Format {image.format} not supported for direct display demo yet (Target: {targetFormat}).");
                    await messageService.ShowErrorMessageAsync(
                        $"Format {image.format} not supported for direct display demo yet (Target: {targetFormat}).");
                    return null;
                }
            }
        }
        catch (Exception ex)
        {
            await messageService.ShowErrorMessageAsync(ex.Message);
        }
        finally
        {
            try
            {
                DirectXTex.Release(ref decompressedScratch);
                DirectXTex.Release(ref scratchImage);
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private Task ChainBundleFileLoadingTask(Task previousTask, CancellationTokenSource? oldCts, CancellationToken token,
        GGPKFileInfo bundleFileInfo, BundleIndexInfo.FileRecord fileRecord)
    {
        return previousTask.ContinueWith(async _ =>
        {
            oldCts?.Dispose();
            if (token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                SelectedImage = null;
                if (IsTextFile(fileRecord.FileName))
                {
                    var partialRecord = fileRecord;
                    if (fileRecord.FileSize > 2 * 1024 * 1024)
                    {
                        partialRecord = fileRecord with { FileSize = 2 * 1024 * 1024 };
                    }

                    var data = await ggpkParsingService.LoadBundleFileDataAsync(bundleFileInfo,
                        partialRecord, token);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            NodeInfoText = Encoding.Unicode.GetString(data);
                        }
                    });
                }
                else if (IsDDSFile(fileRecord.FileName))
                {
                    var data = await ggpkParsingService.LoadBundleFileDataAsync(bundleFileInfo,
                        fileRecord, token);
                    var image = await ResolveDDSDataAsync(data);
                    SelectedImage = image;
                }
                else if (IsDDSHeaderFile(fileRecord.FileName))
                {
                    var data = (await ggpkParsingService.LoadBundleFileDataAsync(bundleFileInfo,
                        fileRecord, token)).Skip(28).ToArray();
                    var image = await ResolveDDSDataAsync(data);
                    SelectedImage = image;
                }
                else
                {
                    await LoadBundleFileInformationAsync(bundleFileInfo, fileRecord, token);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load bundle file: {ex.Message}");
                await messageService.ShowErrorMessageAsync($"Failed to load bundle file: {ex.Message}");
            }
        }, TaskScheduler.Default).Unwrap();
    }

    private static bool IsTextFile(string fileName)
    {
        return fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDDSFile(string fileName)
    {
        return fileName.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDDSHeaderFile(string fileName)
    {
        return fileName.EndsWith(".dds.header", StringComparison.OrdinalIgnoreCase);
    }

    private Task ChainTextLoadingTask(Task previousTask, CancellationTokenSource? oldCts, CancellationToken token,
        GGPKFileInfo fileInfo)
    {
        return previousTask.ContinueWith(async _ =>
        {
            oldCts?.Dispose();
            if (token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await LoadTextAsync(fileInfo, token);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load text: {ex.Message}");
            }
        }, TaskScheduler.Default).Unwrap();
    }

    private async Task LoadTextAsync(GGPKFileInfo ggpkFileInfo, CancellationToken token)
    {
        var data = await ggpkParsingService.LoadGGPKFileDataAsync(ggpkFileInfo, token);
        var text = Encoding.Unicode.GetString(data);

        Dispatcher.UIThread.Post(() =>
        {
            if (!token.IsCancellationRequested)
            {
                NodeInfoText = text;
            }
        });
    }

    private Task ChainFileInformationLoadingTask(Task previousTask, CancellationTokenSource? oldCts,
        CancellationToken token,
        GGPKFileInfo ggpkFileInfo)
    {
        return previousTask.ContinueWith(async _ =>
        {
            oldCts?.Dispose();
            if (token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await LoadFileInformationAsync(ggpkFileInfo, token);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load file: {ex.Message}");
            }
        }, TaskScheduler.Default).Unwrap();
    }

    private async Task LoadBundleFileInformationAsync(GGPKFileInfo ggpkFileInfo, BundleIndexInfo.FileRecord fileRecord,
        CancellationToken token)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Bundle File Name : {fileRecord.FileName}");
        sb.AppendLine($"Total Data Size: {fileRecord.FileSize} bytes");
        sb.AppendLine("--------------------------------------------------");
        sb.AppendLine("Raw Data (First 64 bytes):");

        var data = await ggpkParsingService.LoadBundleFileDataAsync(ggpkFileInfo, fileRecord with { FileSize = 64 },
            token);
        var displayLength = Math.Min((int)fileRecord.FileSize, 64);

        for (var i = 0; i < displayLength; i++)
        {
            sb.Append($"{data[i]:X2} ");
            if ((i + 1) % 16 == 0)
            {
                sb.AppendLine();
            }
        }

        Dispatcher.UIThread.Post(() => { NodeInfoText = sb.ToString(); });
    }

    private async Task LoadFileInformationAsync(GGPKFileInfo ggpkFileInfo, CancellationToken token)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"File Name : {ggpkFileInfo.FileName}");
        sb.AppendLine($"Total Data Size: {ggpkFileInfo.DataSize} bytes");
        sb.AppendLine("--------------------------------------------------");
        sb.AppendLine("Raw Data (First 64 bytes):");

        var data = await ggpkParsingService.LoadGGPKFileDataAsync(ggpkFileInfo, 64, token);
        var displayLength = Math.Min((int)ggpkFileInfo.DataSize, 64);

        for (var i = 0; i < displayLength; i++)
        {
            sb.Append($"{data[i]:X2} ");
            if ((i + 1) % 16 == 0)
            {
                sb.AppendLine();
            }
        }

        Dispatcher.UIThread.Post(() => { NodeInfoText = sb.ToString(); });
    }

    [RelayCommand]
    private async Task OpenGgpkFile()
    {
        var filePath = await fileService.OpenFileAsync();
        if (filePath == null)
        {
            return;
        }

        ggpkParsingService.CloseStream();
        ggpkParsingService.OpenStream(filePath);

        Debug.WriteLine($"Selected file in VM: {filePath}");

        ButtonText = "Loading";
        IsLoading = true;
        try
        {
            var rootNode = await ggpkParsingService.BuildGgpkTreeAsync();

            GgpkNodes.Clear();
            GgpkNodes.Add(rootNode);

            var bundleTree = await ggpkParsingService.BuildBundleTreeAsync(rootNode, filePath);
            SelectedBundleInfoNode = null;

            BundleTreeItems.Clear();
            if (bundleTree != null)
            {
                BundleTreeItems.Add(bundleTree);
            }
        }
        catch (Exception ex)
        {
            await messageService.ShowErrorMessageAsync(ex.Message);
        }
        finally
        {
            ButtonText = "Open";
            IsLoading = false;
        }
    }

    private bool CanDownloadTreeStructure()
    {
        return !IsLoading;
    }

    [RelayCommand(CanExecute = nameof(CanDownloadTreeStructure))]
    private async Task DownloadTreeStructure()
    {
        if (GgpkNodes.Count == 0)
        {
            await messageService.ShowErrorMessageAsync("No GGPK file loaded.");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("GGPK Tree Structure:");
        foreach (var node in GgpkNodes)
        {
            AppendNodeStructure(sb, node, 0);
        }

        if (BundleTreeItems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Bundle Tree Structure:");
            foreach (var node in BundleTreeItems)
            {
                AppendNodeStructure(sb, node, 0);
            }
        }

        var savePath = await fileService.SaveFileAsync("Save Tree Structure", "GGPK_Tree_Structure", "txt");
        if (string.IsNullOrEmpty(savePath))
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(savePath, sb.ToString());
        }
        catch (Exception ex)
        {
            await messageService.ShowErrorMessageAsync($"Failed to save file: {ex.Message}");
        }
    }

    private void AppendNodeStructure(StringBuilder sb, GGPKTreeNode node, int depth)
    {
        sb.Append(' ', depth * 2);
        sb.AppendLine(node.Value.ToString());

        foreach (var child in node.Children)
        {
            AppendNodeStructure(sb, child, depth + 1);
        }
    }

    [RelayCommand]
    private async Task Export(GGPKTreeNode? node)
    {
        if (node?.Value == null)
        {
            return;
        }

        string fileName;
        Func<Task<byte[]>>? loadDataTaskFactory = null;

        if (node.Value is GGPKFileInfo ggpkFileInfo)
        {
            fileName = ggpkFileInfo.FileName;
            loadDataTaskFactory = () => ggpkParsingService.LoadGGPKFileDataAsync(ggpkFileInfo, CancellationToken.None);
        }
        else if (node.Value is BundleIndexInfo.FileRecord bundleFileRecord)
        {
            fileName = bundleFileRecord.FileName;
            if (BundleTreeItems.FirstOrDefault()?.Value is BundleIndexInfo bundleIndexInfo)
            {
                var bundleName = bundleIndexInfo.Bundles[bundleFileRecord.BundleIndex].Name;

                var foundNode = FindGgpkNode(bundleName + ".bundle.bin");
                if (foundNode?.Value is GGPKFileInfo bundleGGPKFileInfo)
                {
                    loadDataTaskFactory = () =>
                        ggpkParsingService.LoadBundleFileDataAsync(bundleGGPKFileInfo, bundleFileRecord,
                            CancellationToken.None);
                }
            }
        }
        else
        {
            return;
        }

        if (loadDataTaskFactory == null)
        {
            return;
        }

        var savePath = await fileService.SaveFileAsync("Save File", fileName, "");
        if (string.IsNullOrEmpty(savePath))
        {
            return;
        }

        IsLoading = true;
        try
        {
            var data = await loadDataTaskFactory();
            await File.WriteAllBytesAsync(savePath, data);
        }
        catch (Exception ex)
        {
            await messageService.ShowErrorMessageAsync($"Failed to export file: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }
}