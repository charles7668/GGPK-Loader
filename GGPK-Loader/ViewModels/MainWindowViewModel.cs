using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GGPK_Loader.Models;
using GGPK_Loader.Models.Schema;
using GGPK_Loader.Services;
using GGPK_Loader.Utils;
using JetBrains.Annotations;

namespace GGPK_Loader.ViewModels;

[method: UsedImplicitly]
public partial class MainWindowViewModel(
    IFileService fileService,
    IMessageService messageService,
    IGgpkParsingService ggpkParsingService,
    IGgpkBundleService ggpkBundleService,
    ISchemaService schemaService) : ViewModelBase
{
    // For Design Time Preview
    public MainWindowViewModel() : this(null!, null!, null!, null!, null!)
    {
        // Design-time constructor
    }

    private static readonly Typeface TypeFace = new("Consolas");

    [ObservableProperty]
    private string _buttonText = "Open";

    private string _currentDatFileName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDatViewVisible))]
    [NotifyPropertyChangedFor(nameof(IsInfoTextVisible))]
    [NotifyPropertyChangedFor(nameof(IsDatViewVisible))]
    [NotifyPropertyChangedFor(nameof(IsInfoTextVisible))]
    private DatRowInfo? _datInfoSource;

    [ObservableProperty]
    private byte[]? _datViewBytes;

    [ObservableProperty]
    private ObservableCollection<GGPKTreeNode> _ggpkNodes = new();

    [ObservableProperty]
    private bool _hasBundleSearchResults;

    [ObservableProperty]
    private bool _hasGgpkSearchResults;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadTreeStructureCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenGgpkFileCommand))]
    private bool _isLoading;

    private CancellationTokenSource? _loadingFileCts;
    private Task _loadingFileTask = Task.CompletedTask;

    [ObservableProperty]
    private bool _matchCase;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInfoTextVisible))]
    private string _nodeInfoText = "";

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private GGPKTreeNode? _selectedBundleInfoNode;

    [ObservableProperty]
    private GGPKTreeNode? _selectedBundleSearchResult;

    [ObservableProperty]
    private GGPKTreeNode? _selectedGgpkSearchResult;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInfoTextVisible))]
    private Bitmap? _selectedImage;

    [ObservableProperty]
    private GGPKTreeNode? _selectedNode;

    [ObservableProperty]
    private bool _useRegex;

    public bool IsDatViewVisible => DatInfoSource != null;

    public bool IsInfoTextVisible => SelectedImage == null && !IsDatViewVisible;

    public ObservableCollection<GGPKTreeNode> GgpkSearchResults { get; } = new();
    public ObservableCollection<GGPKTreeNode> BundleSearchResults { get; } = new();

    public ObservableCollection<GGPKTreeNode> BundleTreeItems { get; } = new();

    partial void OnDatViewBytesChanged(byte[]? value)
    {
        DatInfoSource = value == null ? null : CreateDatRows(value);
    }

    private DatRowInfo? CreateDatRows(byte[] data)
    {
        if (data.Length < 4)
        {
            return null;
        }

        var rowCount = BitConverter.ToInt32(data, 0);

        // Check actual row size first to validate schema
        long separatorIndex = -1;
        // Search for 0xBBBBBBBBBBBBBBBB
        var limit = data.Length - 8;
        for (var i = 4; i <= limit; i++)
        {
            if (data[i] == 0xBB && data[i + 1] == 0xBB && data[i + 2] == 0xBB && data[i + 3] == 0xBB &&
                data[i + 4] == 0xBB && data[i + 5] == 0xBB && data[i + 6] == 0xBB && data[i + 7] == 0xBB)
            {
                separatorIndex = i;
                break;
            }
        }

        var contentEnd = separatorIndex != -1 ? separatorIndex : data.Length;
        var contentSize = contentEnd - 4;
        var actualRowSize = rowCount > 0 ? (int)(contentSize / rowCount) : 0;

        // Try to load schema
        var tableName = Path.GetFileNameWithoutExtension(_currentDatFileName);
        int[]? validVersions = null;
        if (_currentDatFileName.EndsWith(".datc64", StringComparison.OrdinalIgnoreCase))
        {
            validVersions = new[] { 2, 3 };
        }
        else
        {
            validVersions = new[] { 1, 3 };
        }

        var columns = schemaService.GetColumns(tableName, validVersions);

        if (columns != null)
        {
            var is64Bit = _currentDatFileName.EndsWith("64", StringComparison.OrdinalIgnoreCase);
            var calculatedRowSize = 0;
            foreach (var col in columns)
            {
                calculatedRowSize += GetColumnSize(col, is64Bit);
            }

            if (calculatedRowSize > 0)
            {
                var effectiveRowSize = actualRowSize > 0 ? actualRowSize : calculatedRowSize;
                var extraBytes = Math.Max(0, effectiveRowSize - calculatedRowSize);

                var totalColumns = columns.Count + extraBytes;
                var headers = new List<string>(totalColumns);
                var columnWidths = new double[totalColumns];

                // Schema Headers
                for (var i = 0; i < columns.Count; i++)
                {
                    headers.Add(columns[i].Name ?? "Unk");
                    columnWidths[i] = GetTextWidth(headers[i]);
                }

                // Extra Byte Headers
                for (var i = 0; i < extraBytes; i++)
                {
                    var h = i.ToString("X2");
                    headers.Add(h);
                    columnWidths[columns.Count + i] = Math.Max(GetTextWidth("00"), GetTextWidth(h));
                }

                var items = new ObservableCollection<DatRow>();

                for (var r = 0; r < rowCount; r++)
                {
                    var rowStart = 4 + r * effectiveRowSize;
                    if (rowStart + effectiveRowSize > data.Length)
                    {
                        break;
                    }

                    var rowValues = new string[totalColumns];
                    var offset = 0;

                    // Read Schema Columns
                    for (var c = 0; c < columns.Count; c++)
                    {
                        var col = columns[c];
                        var size = GetColumnSize(col, is64Bit);
                        var val = ReadColumnValue(data, rowStart + offset, col, is64Bit, separatorIndex);
                        rowValues[c] = val;

                        var w = GetTextWidth(val);
                        if (w > columnWidths[c])
                        {
                            columnWidths[c] = w;
                        }

                        offset += size;
                    }

                    // Read Extra Bytes
                    for (var i = 0; i < extraBytes; i++)
                    {
                        var colIdx = columns.Count + i;
                        if (rowStart + offset + i < data.Length)
                        {
                            var b = data[rowStart + offset + i];
                            var val = b.ToString("X2");
                            rowValues[colIdx] = val;

                            var w = GetTextWidth(val);
                            if (w > columnWidths[colIdx])
                            {
                                columnWidths[colIdx] = w;
                            }
                        }
                        else
                        {
                            rowValues[colIdx] = "??";
                        }
                    }

                    items.Add(new DatRow(r, rowValues));
                }

                // Add padding to widths
                for (var i = 0; i < columnWidths.Length; i++)
                {
                    columnWidths[i] += 20;
                }

                // Add Row Index Column Width
                // "Row" length is 3 or calculated from Max Index 
                var maxIndexStr = (rowCount - 1).ToString();
                var indexWidth = Math.Max(GetTextWidth("Row"), GetTextWidth(maxIndexStr)) + 20;

                var finalWidths = new List<double> { indexWidth };
                finalWidths.AddRange(columnWidths);

                return new DatRowInfo(items, headers, finalWidths);
            }
        }

        // Fallback to heuristic
        if (rowCount <= 0)
        {
            return null;
        }

        var rowSize = (int)(contentSize / rowCount);
        if (rowSize == 0)
        {
            return null;
        }

        var maxIndex = rowSize - 1;
        var indexDigits = maxIndex > 255 ? (int)Math.Floor(Math.Log(maxIndex, 16)) + 1 : 2;
        var headerFormat = "X" + indexDigits;

        var itemsFallback = new ObservableCollection<DatRow>();
        var widthsFallback = new double[rowSize];
        var headersFallback = new List<string>();

        for (var i = 0; i < rowSize; i++)
        {
            var h = i.ToString(headerFormat);
            headersFallback.Add(h);
            widthsFallback[i] = GetTextWidth(h);
        }

        for (var r = 0; r < rowCount; r++)
        {
            var rowStart = 4 + r * rowSize;
            var rowValues = new string[rowSize];

            for (var c = 0; c < rowSize; c++)
            {
                if (rowStart + c < data.Length)
                {
                    rowValues[c] = data[rowStart + c].ToString("X2");
                }
                else
                {
                    rowValues[c] = "??";
                }

                var w = GetTextWidth(rowValues[c]);
                if (w > widthsFallback[c])
                {
                    widthsFallback[c] = w;
                }
            }

            itemsFallback.Add(new DatRow(r, rowValues));
        }

        for (var i = 0; i < widthsFallback.Length; i++)
        {
            widthsFallback[i] += 20;
        }

        // Add Row Index Column Width Fallback
        var maxIndexStrFallback = (rowCount - 1).ToString();
        var indexWidthFallback = Math.Max(GetTextWidth("Row"), GetTextWidth(maxIndexStrFallback)) + 20;

        var finalWidthsFallback = new List<double>();
        finalWidthsFallback.Add(indexWidthFallback);
        finalWidthsFallback.AddRange(widthsFallback);

        return new DatRowInfo(itemsFallback, headersFallback, finalWidthsFallback);
    }

    private static double GetTextWidth(string text)
    {
        var ft = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            TypeFace,
            14,
            null
        );
        return ft.WidthIncludingTrailingWhitespace;
    }

    private static int GetColumnSize(SchemaColumn col, bool is64Bit)
    {
        if (col.Array)
        {
            return is64Bit ? 16 : 8;
        }

        return col.Type switch
        {
            "bool" => 1,
            "i16" or "u16" => 2,
            "i32" or "u32" or "f32" => 4,
            "i64" or "u64" => 8,
            "string" => is64Bit ? 8 : 4,
            "foreignrow" => is64Bit ? 16 : 4,
            "row" => is64Bit ? 8 : 4,
            "enumrow" => 4,
            "rid" => is64Bit ? 16 : 4,
            _ => 1
        };
    }

    private static string ReadColumnValue(byte[] data, int offset, SchemaColumn col, bool is64Bit, long baseOffset)
    {
        var size = GetColumnSize(col, is64Bit);
        if (offset + size > data.Length)
        {
            return "ERR";
        }

        if (col.Array)
        {
            var count = is64Bit ? BitConverter.ToInt64(data, offset) : BitConverter.ToInt32(data, offset);
            return $"[{count}]";
        }

        switch (col.Type)
        {
            case "bool":
                return data[offset] != 0 ? "True" : "False";
            case "i16": return BitConverter.ToInt16(data, offset).ToString();
            case "i32": return BitConverter.ToInt32(data, offset).ToString();
            case "i64": return BitConverter.ToInt64(data, offset).ToString();
            case "u16": return BitConverter.ToUInt16(data, offset).ToString();
            case "u32": return BitConverter.ToUInt32(data, offset).ToString();
            case "u64": return BitConverter.ToUInt64(data, offset).ToString();
            case "f32": return BitConverter.ToSingle(data, offset).ToString("F2");
            case "f64": return BitConverter.ToDouble(data, offset).ToString("F2");
            case "string":
                var sRel = is64Bit ? BitConverter.ToInt64(data, offset) : BitConverter.ToUInt32(data, offset);
                var sPtr = baseOffset + sRel;

                // If baseOffset is invalid (-1) or result is out of bounds
                if (baseOffset >= 0 && sPtr >= 0 && sPtr < data.Length)
                {
                    var start = (int)sPtr;
                    var len = 0;
                    while (start + len + 1 < data.Length)
                    {
                        if (data[start + len] == 0 && data[start + len + 1] == 0)
                        {
                            break;
                        }

                        len += 2;
                    }

                    return Encoding.Unicode.GetString(data, start, len);
                }

                return $"Ptr({sRel:X})";
            case "foreignrow":
                var key = is64Bit ? BitConverter.ToUInt64(data, offset) : BitConverter.ToUInt32(data, offset);
                return $"FK({key:X})";
            case "row":
                var rKey = is64Bit ? BitConverter.ToUInt64(data, offset) : BitConverter.ToUInt32(data, offset);
                return $"Row({rKey:X})";
            case "enumrow":
                var eKey = BitConverter.ToInt32(data, offset);
                return $"Enum({eKey:X})";
            default:
                return "??";
        }
    }

    partial void OnSelectedGgpkSearchResultChanged(GGPKTreeNode? value)
    {
        if (value == null)
        {
            return;
        }

        HandleSearchResultSelection(value);
        SelectedNode = value;
    }

    partial void OnSelectedBundleSearchResultChanged(GGPKTreeNode? value)
    {
        if (value == null)
        {
            return;
        }

        HandleSearchResultSelection(value);
        SelectedBundleInfoNode = value;
    }

    private static void HandleSearchResultSelection(GGPKTreeNode value)
    {
        // Expand parents
        var parent = value.Parent;
        while (parent != null)
        {
            parent.IsExpanded = true;
            parent = parent.Parent;
        }

        value.IsExpanded = true;
        value.IsSelected = true;
    }

    [RelayCommand]
    private async Task Search()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return;
        }

        IsLoading = true;
        GgpkSearchResults.Clear();
        BundleSearchResults.Clear();

        try
        {
            string pattern;
            if (UseRegex)
            {
                pattern = SearchText;
            }
            else
            {
                // Wildcard to Regex: *.txt -> .*\.txt (removed start/end anchors)
                pattern = Regex.Escape(SearchText)
                    .Replace("\\*", ".*")
                    .Replace("\\?", ".");
            }

            var regexOptions = MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
            var regex = new Regex(pattern, regexOptions);

            await Task.Run(() =>
            {
                if (GgpkNodes.Count > 0)
                {
                    SearchRecursive(GgpkNodes[0], regex, GgpkSearchResults);
                }

                if (BundleTreeItems.Count > 0)
                {
                    SearchRecursive(BundleTreeItems[0], regex, BundleSearchResults);
                }
            });



            if (GgpkSearchResults.Count == 0 && BundleSearchResults.Count == 0)
            {
                await messageService.ShowErrorMessageAsync("No results found.");
            }

            HasGgpkSearchResults = GgpkSearchResults.Count > 0;
            HasBundleSearchResults = BundleSearchResults.Count > 0;
        }
        catch (Exception ex)
        {
            await messageService.ShowErrorMessageAsync($"Search failed: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void CloseGgpkSearchResults()
    {
        GgpkSearchResults.Clear();
        HasGgpkSearchResults = false;
    }

    [RelayCommand]
    private void CloseBundleSearchResults()
    {
        BundleSearchResults.Clear();
        HasBundleSearchResults = false;
    }

    private void SearchRecursive(GGPKTreeNode node, Regex regex, ObservableCollection<GGPKTreeNode> results)
    {
        var name = node.Value.ToString() ?? "";
        if (regex.IsMatch(name))
        {
            Dispatcher.UIThread.Post(() => results.Add(node));
        }

        foreach (var child in node.Children)
        {
            SearchRecursive(child, regex, results);
        }
    }

    partial void OnSelectedBundleInfoNodeChanged(GGPKTreeNode? value)
    {
        if (IsLoading)
        {
            return;
        }

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
        if (IsLoading)
        {
            return;
        }

        if (!ShouldProcessNode(value, out var nodeValue))
        {
            return;
        }

        var (oldCts, token) = ResetCancellationSource();
        SelectedImage = null;
        DatViewBytes = null;
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

        if (IsDatFile(fileInfo.FileName))
        {
            _loadingFileTask = ChainDatLoadingTask(_loadingFileTask, oldCts, token, fileInfo);
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

    private async Task<Bitmap?> ResolveDdsDataAsync(byte[] data)
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

    private Task ChainDatLoadingTask(Task previousTask, CancellationTokenSource? oldCts, CancellationToken token,
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
                await LoadDatViewAsync(fileInfo, token);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load dat file: {ex.Message}");
            }
        }, TaskScheduler.Default).Unwrap();
    }

    private async Task LoadDatViewAsync(GGPKFileInfo ggpkFileInfo, CancellationToken token)
    {
        _currentDatFileName = ggpkFileInfo.FileName;
        var data = await ggpkParsingService.LoadGGPKFileDataAsync(ggpkFileInfo, token);
        Dispatcher.UIThread.Post(() => { DatViewBytes = data; });
    }

    private async Task LoadBundleDatViewAsync(GGPKFileInfo bundleFileInfo, BundleIndexInfo.FileRecord fileRecord,
        CancellationToken token)
    {
        _currentDatFileName = fileRecord.FileName;
        var data = await ggpkBundleService.LoadBundleFileDataAsync(bundleFileInfo, fileRecord, token);
        Dispatcher.UIThread.Post(() => { DatViewBytes = data; });
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
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    SelectedImage = null;
                    DatViewBytes = null;
                });
                if (IsTextFile(fileRecord.FileName))
                {
                    var partialRecord = fileRecord;
                    if (fileRecord.FileSize > 2 * 1024 * 1024)
                    {
                        partialRecord = fileRecord with { FileSize = 2 * 1024 * 1024 };
                    }

                    var data = await ggpkBundleService.LoadBundleFileDataAsync(bundleFileInfo,
                        partialRecord, token);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            NodeInfoText = Encoding.Unicode.GetString(data);
                        }
                    });
                }
                else if (IsDdsFile(fileRecord.FileName))
                {
                    var data = await ggpkBundleService.LoadBundleFileDataAsync(bundleFileInfo,
                        fileRecord, token);
                    var image = await ResolveDdsDataAsync(data);
                    await Dispatcher.UIThread.InvokeAsync(() => { SelectedImage = image; });
                }
                else if (IsDdsHeaderFile(fileRecord.FileName))
                {
                    var data = (await ggpkBundleService.LoadBundleFileDataAsync(bundleFileInfo,
                        fileRecord, token)).Skip(28).ToArray();
                    var image = await ResolveDdsDataAsync(data);
                    await Dispatcher.UIThread.InvokeAsync(() => { SelectedImage = image; });
                }
                else if (IsDatFile(fileRecord.FileName))
                {
                    await LoadBundleDatViewAsync(bundleFileInfo, fileRecord, token);
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
               fileName.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDdsFile(string fileName)
    {
        return fileName.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDdsHeaderFile(string fileName)
    {
        return fileName.EndsWith(".dds.header", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDatFile(string fileName)
    {
        return fileName.EndsWith(".datc64", StringComparison.OrdinalIgnoreCase);
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

        var data = await ggpkBundleService.LoadBundleFileDataAsync(ggpkFileInfo, fileRecord with { FileSize = 64 },
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

    private bool CanOpenGgpkFile()
    {
        return !IsLoading;
    }

    [RelayCommand(CanExecute = nameof(CanOpenGgpkFile))]
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

            var bundleTree = await ggpkBundleService.BuildBundleTreeAsync(rootNode, filePath);
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
                        ggpkBundleService.LoadBundleFileDataAsync(bundleGGPKFileInfo, bundleFileRecord,
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

    [RelayCommand]
    private async Task CopyPath(GGPKTreeNode? node)
    {
        if (node is null)
        {
            return;
        }

        var pathParts = new Stack<string>();
        var current = node;
        while (current != null)
        {
            var name = current.Value switch
            {
                GGPKFileInfo info => info.FileName,
                BundleIndexInfo.FileRecord record => record.FileName,
                string s => s,
                _ => ""
            };

            if (!string.IsNullOrEmpty(name))
            {
                pathParts.Push(name);
            }

            current = current.Parent;
        }

        var path = string.Join("/", pathParts);

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
            desktop)
        {
            if (desktop.MainWindow?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(path);
            }
        }
    }

    [RelayCommand]
    private async Task ReplaceBundleFile(GGPKTreeNode? node)
    {
        if (node?.Value is BundleIndexInfo.FileRecord fileRecord &&
            BundleTreeItems.FirstOrDefault()?.Value is BundleIndexInfo bundleIndexInfo)
        {
            try
            {
                var filePath = await fileService.OpenFileAsync();
                if (string.IsNullOrEmpty(filePath))
                {
                    return;
                }

                IsLoading = true;
                var newContent = await File.ReadAllBytesAsync(filePath);

                var bundleName = bundleIndexInfo.Bundles[fileRecord.BundleIndex].Name;
                var foundNode = FindGgpkNode(bundleName + ".bundle.bin");

                if (foundNode?.Value is GGPKFileInfo bundleGGPKFileInfo)
                {
                    var newBundleData = await ggpkBundleService.ReplaceBundleFileContentAsync(bundleGGPKFileInfo,
                        fileRecord, newContent, CancellationToken.None);

                    await ggpkParsingService.ReplaceFileDataAsync(newBundleData, foundNode, CancellationToken.None);

                    // Update Index Info
                    var sizeDiff = newContent.Length - (int)fileRecord.FileSize;
                    var bundleRecord = bundleIndexInfo.Bundles[fileRecord.BundleIndex];
                    var newUncompressedSize = (uint)((int)bundleRecord.UncompressedSize + sizeDiff);

                    // Update Bundle Record
                    var newBundles = bundleIndexInfo.Bundles.ToArray();
                    newBundles[fileRecord.BundleIndex] = new BundleIndexInfo.BundleRecord(
                        bundleRecord.NameLength, bundleRecord.Name, newUncompressedSize);

                    // Update File Records
                    var newFiles = bundleIndexInfo.Files.ToArray();
                    for (var i = 0; i < newFiles.Length; i++)
                    {
                        var f = newFiles[i];
                        if (f.Hash == fileRecord.Hash && f.BundleIndex == fileRecord.BundleIndex)
                        {
                            newFiles[i] = f with { FileSize = (uint)newContent.Length };
                        }
                        else if (f.BundleIndex == fileRecord.BundleIndex && f.FileOffset > fileRecord.FileOffset)
                        {
                            newFiles[i] = f with { FileOffset = (uint)(f.FileOffset + sizeDiff) };
                        }
                    }

                    var newIndexInfo = bundleIndexInfo with
                    {
                        Bundles = newBundles,
                        Files = newFiles
                    };

                    var indexNode = FindGgpkNode("_.index.bin") ?? FindGgpkNode("index.bin");
                    if (indexNode != null)
                    {
                        await ggpkBundleService.UpdateBundleIndexAsync(newIndexInfo, indexNode, CancellationToken.None);
                    }
                    else
                    {
                        await messageService.ShowErrorMessageAsync("Could not find index.bin to update.");
                    }

                    // Refresh Bundle Tree
                    if (GgpkNodes.FirstOrDefault() is { } rootNode)
                    {
                        var newBundleTree = await ggpkBundleService.BuildBundleTreeAsync(rootNode, "");
                        BundleTreeItems.Clear();
                        if (newBundleTree != null)
                        {
                            BundleTreeItems.Add(newBundleTree);
                        }
                    }

                    // Try to re-select the node if possible, but the old node reference is stale.
                    // Accessing fileRecord.FileName might help to find it again later if needed.
                    await messageService.ShowErrorMessageAsync("File replaced successfully. Bundle tree refreshed.");
                }
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception ex)
            {
                await messageService.ShowErrorMessageAsync($"Failed to replace file: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
            }
        }
    }

    [RelayCommand]
    private async Task Replace(GGPKTreeNode? node)
    {
        if (node is { Value: GGPKFileInfo, Parent.Value: GGPKDirInfo })
        {
            try
            {
                var filePath = await fileService.OpenFileAsync();
                if (string.IsNullOrEmpty(filePath))
                {
                    return;
                }

                var data = await File.ReadAllBytesAsync(filePath);
                await ggpkParsingService.ReplaceFileDataAsync(data, node, CancellationToken.None);
                await messageService.ShowErrorMessageAsync("File replaced successfully.");
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception ex)
            {
                await messageService.ShowErrorMessageAsync($"Failed to replace file: {ex.Message}");
            }
        }
    }

    public record DatRowInfo(ObservableCollection<DatRow> Rows, List<string> Headers, List<double> ColumnWidths);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public record DatRow(int Index, string[] Values);
}