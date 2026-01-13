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
using File = System.IO.File;

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
    private string _buttonText = "Open";

    private string _currentFilePath = "";

    [ObservableProperty]
    private ObservableCollection<GGPKTreeNode> _ggpkNodes = new();

    private CancellationTokenSource? _loadingFileCts;
    private Task _loadingFileTask = Task.CompletedTask;

    [ObservableProperty]
    private string _nodeInfoText = "";

    [ObservableProperty]
    private Bitmap? _selectedImage;

    [ObservableProperty]
    private GGPKTreeNode? _selectedNode;

    [ObservableProperty]
    private GGPKTreeNode? _selectedBundleInfoNode;

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
        if (foundNode != null)
        {
            var (oldCts, token) = ResetCancellationSource();
            _loadingFileTask =
                ChainBundleFileLoadingTask(_loadingFileTask, oldCts, token, foundNode.Offset, fileRecord);
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
            return null;

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
        var (oldCts, token) = ResetCancellationSource();
        SelectedImage = null;

        if (ShouldProcessNode(value, out var fileName))
        {
            NodeInfoText = fileName;
            if (IsImageFile(fileName))
            {
                _loadingFileTask = ChainImageLoadingTask(_loadingFileTask, oldCts, token, value!.Offset);
                return;
            }

            if (IsTextFile(fileName))
            {
                _loadingFileTask = ChainTextLoadingTask(_loadingFileTask, oldCts, token, value!.Offset);
                return;
            }

            if (IsGgdhFile(fileName))
            {
                _loadingFileTask = ChainGgdhLoadingTask(_loadingFileTask, oldCts, token, value!.Offset);
                return;
            }
        }

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

    private static bool ShouldProcessNode(GGPKTreeNode? node, out string fileName)
    {
        fileName = string.Empty;
        if (node is { Children.Count: 0 })
        {
            fileName = node.Value?.ToString() ?? string.Empty;
            return true;
        }

        return false;
    }

    private static bool IsImageFile(string fileName)
    {
        return fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase);
    }

    private Task ChainImageLoadingTask(Task previousTask, CancellationTokenSource? oldCts, CancellationToken token,
        ulong offset)
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
                await LoadImageAsync(offset, token);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load image: {ex.Message}");
            }
        }, TaskScheduler.Default).Unwrap();
    }

    private async Task LoadImageAsync(ulong offset, CancellationToken token)
    {
        if (string.IsNullOrEmpty(_currentFilePath))
        {
            return;
        }

        await using var stream = File.OpenRead(_currentFilePath);
        using var reader = new BinaryReader(stream);

        stream.Seek((long)offset, SeekOrigin.Begin);
        var entryLength = reader.ReadUInt32();
        var entryTag = new string(reader.ReadChars(4));

        if (entryTag != "FILE")
        {
            return;
        }

        var nameLength = reader.ReadUInt32();
        stream.Seek(32 + nameLength * 2, SeekOrigin.Current); // Skip hash + name

        var headerSize = 4 + 4 + 4 + 32 + nameLength * 2;
        var dataSize = entryLength - headerSize;

        if (dataSize <= 0)
        {
            return;
        }

        var data = reader.ReadBytes((int)dataSize);
        if (token.IsCancellationRequested)
        {
            return;
        }

        using var memoryStream = new MemoryStream(data);
        var bitmap = new Bitmap(memoryStream);

        Dispatcher.UIThread.Post(() =>
        {
            if (!token.IsCancellationRequested)
            {
                SelectedImage = bitmap;
            }
        });
    }

    private bool IsCompressedFormat(DirectXTex.DXGI_FORMAT format, out DirectXTex.DXGI_FORMAT targetFormat)
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
        ulong bundleOffset, BundleIndexInfo.FileRecord fileRecord)
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
                if (IsTextFile(fileRecord.FileName))
                {
                    var partialRecord = fileRecord;
                    if (fileRecord.FileSize > 2 * 1024 * 1024)
                    {
                        partialRecord = fileRecord with { FileSize = 2 * 1024 * 1024 };
                    }

                    var data = await ggpkParsingService.LoadBundleFileDataAsync(_currentFilePath, bundleOffset,
                        partialRecord);
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
                    var data = await ggpkParsingService.LoadBundleFileDataAsync(_currentFilePath, bundleOffset,
                        fileRecord);
                    var image = await ResolveDDSDataAsync(data);
                    SelectedImage = image;
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
               fileName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDDSFile(string fileName)
    {
        return fileName.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);
    }

    private Task ChainTextLoadingTask(Task previousTask, CancellationTokenSource? oldCts, CancellationToken token,
        ulong offset)
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
                await LoadTextAsync(offset, token);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load text: {ex.Message}");
            }
        }, TaskScheduler.Default).Unwrap();
    }

    private async Task LoadTextAsync(ulong offset, CancellationToken token)
    {
        if (string.IsNullOrEmpty(_currentFilePath))
        {
            return;
        }

        await using var stream = File.OpenRead(_currentFilePath);
        using var reader = new BinaryReader(stream);

        stream.Seek((long)offset, SeekOrigin.Begin);
        var entryLength = reader.ReadUInt32();
        var entryTag = new string(reader.ReadChars(4));

        if (entryTag != "FILE")
        {
            return;
        }

        var nameLength = reader.ReadUInt32();
        stream.Seek(32 + nameLength * 2, SeekOrigin.Current); // Skip hash + name

        var headerSize = 4 + 4 + 4 + 32 + nameLength * 2;
        var dataSize = entryLength - headerSize;

        if (dataSize <= 0)
        {
            return;
        }

        var data = reader.ReadBytes((int)dataSize);
        if (token.IsCancellationRequested)
        {
            return;
        }

        var text = Encoding.Unicode.GetString(data);

        Dispatcher.UIThread.Post(() =>
        {
            if (!token.IsCancellationRequested)
            {
                NodeInfoText = text;
            }
        });
    }

    private static bool IsGgdhFile(string fileName)
    {
        return fileName.EndsWith("ggdh", StringComparison.OrdinalIgnoreCase);
    }

    private Task ChainGgdhLoadingTask(Task previousTask, CancellationTokenSource? oldCts, CancellationToken token,
        ulong offset)
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
                await LoadGgdhAsync(offset, token);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load ggdh: {ex.Message}");
            }
        }, TaskScheduler.Default).Unwrap();
    }

    private async Task LoadGgdhAsync(ulong offset, CancellationToken token)
    {
        if (string.IsNullOrEmpty(_currentFilePath))
        {
            return;
        }

        await using var stream = File.OpenRead(_currentFilePath);
        using var reader = new BinaryReader(stream);

        stream.Seek((long)offset, SeekOrigin.Begin);
        var entryLength = reader.ReadUInt32();
        var entryTag = new string(reader.ReadChars(4));

        if (entryTag != "FILE")
        {
            return;
        }

        var nameLength = reader.ReadUInt32();
        stream.Seek(32 + nameLength * 2, SeekOrigin.Current); // Skip hash + name

        var headerSize = 4 + 4 + 4 + 32 + nameLength * 2;
        var dataSize = entryLength - headerSize;

        if (dataSize <= 0)
        {
            return;
        }

        var data = reader.ReadBytes((int)dataSize);
        if (token.IsCancellationRequested)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("GGDH File Content");
        sb.AppendLine($"Total Data Size: {dataSize} bytes");
        sb.AppendLine("--------------------------------------------------");
        sb.AppendLine("Raw Data (First 64 bytes):");

        var displayLength = Math.Min((int)dataSize, 64);

        for (var i = 0; i < displayLength; i++)
        {
            sb.Append($"{data[i]:X2} ");
            if ((i + 1) % 16 == 0)
            {
                sb.AppendLine();
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!token.IsCancellationRequested)
            {
                NodeInfoText = sb.ToString();
            }
        });
    }

    private BundleIndexInfo? _currentBundleIndexInfo;

    [RelayCommand]
    private async Task OpenGgpkFile()
    {
        var filePath = await fileService.OpenFileAsync();
        if (filePath == null)
        {
            return;
        }

        _currentFilePath = filePath;
        Debug.WriteLine($"Selected file in VM: {filePath}");

        ButtonText = "Loading";
        try
        {
            var rootNode = await ggpkParsingService.BuildGgpkTreeAsync(filePath);

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
        }
    }
}