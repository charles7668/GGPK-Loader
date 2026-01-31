using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GGPK_Loader.Models;
using GGPK_Loader.Services;
using JetBrains.Annotations;

namespace GGPK_Loader.ViewModels;

[method: UsedImplicitly]
public partial class MainWindowViewModel(
    IFileService fileService,
    IMessageService messageService,
    IGgpkParsingService ggpkParsingService,
    IGgpkBundleService ggpkBundleService,
    ISchemaService schemaService,
    ITextureService textureService,
    IDatParsingService datParsingService) : ViewModelBase
{
    // For Design Time Preview
    public MainWindowViewModel() : this(null!, null!, null!, null!, null!, null!, null!)
    {
        // Design-time constructor
    }

    [ObservableProperty]
    private string _buttonText = "Open";

    private string _currentDatFileName = "";

    [ObservableProperty]
    private string _datFilterText = "";

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

    private List<DatRow>? _originalDatRows;

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
        DatFilterText = "";
        if (value == null)
        {
            DatInfoSource = null;
            return;
        }

        IsLoading = true;
        Task.Run(async () =>
        {
            try
            {
                var info = datParsingService.ParseDatFile(value, _currentDatFileName);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    DatInfoSource = info;
                    ApplyDatFilter();
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error creating dat rows: {ex}");
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await messageService.ShowErrorMessageAsync($"Failed to parse dat file: {ex.Message}");
                    DatInfoSource = null;
                });
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() => IsLoading = false);
            }
        });
    }

    partial void OnDatFilterTextChanged(string value)
    {
        ApplyDatFilter();
    }

    private void ApplyDatFilter()
    {
        if (DatInfoSource == null || _originalDatRows == null)
        {
            return;
        }

        IEnumerable<DatRow> filtered = _originalDatRows;
        if (!string.IsNullOrWhiteSpace(DatFilterText))
        {
            filtered = _originalDatRows.Where(r =>
                r.Values.Any(v => v.Contains(DatFilterText, StringComparison.OrdinalIgnoreCase))
            );
        }

        DatInfoSource = DatInfoSource with { Rows = new ObservableCollection<DatRow>(filtered) };
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

    private async Task<Bitmap?> ResolveDdsDataAsync(byte[] data)
    {
        return await textureService.ResolveDdsDataAsync(data);
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

    private bool IsDdsFile(string fileName)
    {
        return textureService?.IsDdsFile(fileName) ?? fileName.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsDdsHeaderFile(string fileName)
    {
        return textureService?.IsDdsHeaderFile(fileName) ?? fileName.EndsWith(".dds.header", StringComparison.OrdinalIgnoreCase);
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

    [RelayCommand]
    private async Task CopyDatRow(object? parameter)
    {
        if (parameter == null)
        {
            return;
        }

        var rows = new List<DatRow>();
        if (parameter is DatRow singleRow)
        {
            rows.Add(singleRow);
        }
        else if (parameter is IEnumerable list)
        {
            foreach (var item in list)
            {
                if (item is DatRow r)
                {
                    rows.Add(r);
                }
            }
        }

        if (rows.Count == 0 || DatInfoSource == null)
        {
            return;
        }

        // Sort rows by index
        rows.Sort((a, b) => a.Index.CompareTo(b.Index));

        var sb = new StringBuilder();

        // 1. Header (Index + Column Headers)
        sb.Append("Index\t");
        sb.AppendLine(string.Join("\t", DatInfoSource.Headers));

        // 2. Rows (Index + Values)
        foreach (var row in rows)
        {
            sb.Append(row.Index).Append('\t');
            sb.AppendLine(string.Join("\t", row.Values));
        }

        var text = sb.ToString();

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
            }
        }
    }

    [RelayCommand]
    private async Task CopyDatRowJson(object? parameter)
    {
        if (parameter == null || DatInfoSource == null || DatInfoSource.Columns == null)
        {
            return;
        }

        var rows = new List<DatRow>();
        if (parameter is DatRow singleRow)
        {
            rows.Add(singleRow);
        }
        else if (parameter is IEnumerable list)
        {
            // Materialize the list first to avoid any potential enumeration issues with live collections
            var items = list.Cast<object>().ToList();
            foreach (var item in items)
            {
                if (item is DatRow r)
                {
                    rows.Add(r);
                }
            }
        }

        if (rows.Count == 0)
        {
            return;
        }

        if (rows.Count > 1)
        {
            rows.Sort((a, b) => a.Index.CompareTo(b.Index));
        }

        // Debug: Check count
        // await messageService.ShowErrorMessageAsync($"Exporting {rows.Count} rows..."); 

        IsLoading = true;
        var columns = DatInfoSource.Columns;

        try
        {
            var resultList = new List<Dictionary<string, object?>>();

            await Task.Run(async () =>
            {
                foreach (var row in rows)
                {
                    var rowObj = new Dictionary<string, object?>();
                    rowObj["_Index"] = row.Index;

                    for (var i = 0; i < columns.Count; i++)
                    {
                        var col = columns[i];
                        if (i >= row.Values.Length)
                        {
                            break;
                        }

                        var valStr = row.Values[i];
                        var colName = col.Name ?? $"Col{i}";

                        // Check if it's a Foreign Row that we want to resolve
                        if (col.Type == "foreignrow")
                        {
                            var targetTable = col.References?.Table;
                            if (string.IsNullOrEmpty(targetTable))
                            {
                                targetTable = col.Name;
                            }

                            if (col.Array && valStr.StartsWith("[") && valStr.EndsWith("]"))
                            {
                                // Handle Array of Foreign Keys
                                var innerContent = valStr.Substring(1, valStr.Length - 2);
                                if (string.IsNullOrWhiteSpace(innerContent))
                                {
                                    rowObj[colName] = new List<object>();
                                }
                                else
                                {
                                    var splits = innerContent.Split(new[] { ", " },
                                        StringSplitOptions.RemoveEmptyEntries);
                                    var resolvedList = new List<object?>();

                                    foreach (var item in splits)
                                    {
                                        var cleanItem = item.Trim();
                                        if (cleanItem.StartsWith("FK("))
                                        {
                                            var hexId = cleanItem.Substring(3, cleanItem.Length - 4);
                                            if (!string.IsNullOrEmpty(targetTable) && long.TryParse(hexId,
                                                    NumberStyles.HexNumber, null, out var id))
                                            {
                                                try
                                                {
                                                    // Resolve recursively
                                                    var foreignData = await ResolveForeignRow(targetTable, id);
                                                    resolvedList.Add(foreignData ?? cleanItem);
                                                }
                                                catch
                                                {
                                                    resolvedList.Add(cleanItem);
                                                }
                                            }
                                            else
                                            {
                                                resolvedList.Add(cleanItem);
                                            }
                                        }
                                        else
                                        {
                                            resolvedList.Add(cleanItem);
                                        }
                                    }

                                    rowObj[colName] = resolvedList;
                                }
                            }
                            else if (!col.Array && valStr != "null" && valStr.StartsWith("FK("))
                            {
                                // Handle Single Foreign Key
                                var hexId = valStr.Substring(3, valStr.Length - 4);
                                if (!string.IsNullOrEmpty(targetTable) &&
                                    long.TryParse(hexId, NumberStyles.HexNumber, null, out var id))
                                {
                                    try
                                    {
                                        var foreignData = await ResolveForeignRow(targetTable, id);
                                        rowObj[colName] = foreignData ?? valStr;
                                    }
                                    catch
                                    {
                                        rowObj[colName] = valStr;
                                    }
                                }
                                else
                                {
                                    rowObj[colName] = valStr;
                                }
                            }
                            else
                            {
                                // null or unknown format
                                rowObj[colName] = valStr;
                            }
                        }
                        else
                        {
                            rowObj[colName] = valStr;
                        }
                    }

                    resultList.Add(rowObj);
                }
            });

            var json = JsonSerializer.Serialize(resultList, new JsonSerializerOptions { WriteIndented = true });

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                if (desktop.MainWindow?.Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(json);
                }
            }
        }
        catch (Exception ex)
        {
            await messageService.ShowErrorMessageAsync($"JSON Export Failed: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task CopyDatRowBinary(object? parameter)
    {
        if (parameter == null || DatViewBytes == null || DatViewBytes.Length < 4)
        {
            return;
        }

        var rows = new List<DatRow>();
        if (parameter is DatRow singleRow)
        {
            rows.Add(singleRow);
        }
        else if (parameter is IEnumerable list)
        {
            var items = list.Cast<object>().ToList();
            foreach (var item in items)
            {
                if (item is DatRow r)
                {
                    rows.Add(r);
                }
            }
        }

        if (rows.Count == 0)
        {
            return;
        }

        if (rows.Count > 1)
        {
            rows.Sort((a, b) => a.Index.CompareTo(b.Index));
        }

        // Calculate Row Size
        var data = DatViewBytes;
        var rowCount = BitConverter.ToInt32(data, 0);
        long separatorIndex = -1;
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

        if (actualRowSize <= 0)
        {
            return;
        }

        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            var start = 4 + row.Index * actualRowSize;
            if (start + actualRowSize > data.Length)
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.AppendLine();
            }

            var rowBytes = new byte[actualRowSize];
            Array.Copy(data, start, rowBytes, 0, actualRowSize);
            sb.Append(BitConverter.ToString(rowBytes).Replace("-", " "));
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(sb.ToString());
            }
        }
    }

    private async Task<object?> ResolveForeignRow(string? tableName, long id, int currentDepth = 0)
    {
        if (string.IsNullOrEmpty(tableName))
        {
            return null;
        }

        if (currentDepth > 5)
        {
            return null;
        }

        IEnumerable<GGPKTreeNode> searchNodes;
        if (SelectedNode != null)
        {
            var root = SelectedNode;
            while (root.Parent != null)
            {
                root = root.Parent;
            }

            searchNodes = [root];
        }
        else
        {
            searchNodes = GgpkNodes.Concat(BundleTreeItems);
        }

        var ggpkTreeNodes = searchNodes.ToList();
        var node = FindNodeByName(ggpkTreeNodes, tableName + ".datc64") ??
                   FindNodeByName(ggpkTreeNodes, tableName + ".dat");

        if (node?.Value is GGPKFileInfo fib)
        {
            try
            {
                var data = await ggpkParsingService.LoadGGPKFileDataAsync(fib, CancellationToken.None);
                return await ResolveForeignRowDataAsync(data, tableName, id, fib.FileName, currentDepth);
            }
            catch
            {
                return null;
            }
        }

        if (node?.Value is BundleIndexInfo.FileRecord fileRecord)
        {
            try
            {
                // Find BundleIndexInfo from BundleTreeItems
                var bundleIndexInfo = BundleTreeItems.FirstOrDefault()?.Value as BundleIndexInfo;
                if (bundleIndexInfo == null)
                {
                    return null;
                }

                if (fileRecord.BundleIndex >= bundleIndexInfo.Bundles.Length)
                {
                    return null;
                }

                var bundleName = bundleIndexInfo.Bundles[fileRecord.BundleIndex].Name;
                if (string.IsNullOrEmpty(bundleName))
                {
                    return null;
                }

                // Find the bundle file node in GGPK tree
                // Usually bundle files in GGPK have .bundle.bin extension
                var bundleNode = FindGgpkNode(bundleName + ".bundle.bin");

                if (bundleNode?.Value is GGPKFileInfo bundleFileInfo)
                {
                    var data = await ggpkBundleService.LoadBundleFileDataAsync(bundleFileInfo, fileRecord,
                        CancellationToken.None);
                    return await ResolveForeignRowDataAsync(data, tableName, id, fileRecord.FileName, currentDepth);
                }
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private async Task<object?> ResolveForeignRowDataAsync(byte[] data, string tableName, long id, string fileName,
        int currentDepth)
    {
        if (data.Length < 4)
        {
            return null;
        }

        var rowCount = BitConverter.ToInt32(data, 0);

        long separatorIndex = -1;
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

        if (actualRowSize == 0 || id >= rowCount)
        {
            return null;
        }

        var isDatc64 = fileName.EndsWith(".datc64", StringComparison.OrdinalIgnoreCase);
        var validVersions = isDatc64 ? new[] { 2, 3 } : new[] { 1, 3 };

        var cols = schemaService.GetColumns(tableName, validVersions);
        if (cols == null)
        {
            return null;
        }

        var is64Bit = isDatc64;

        var rowStart = 4 + (int)id * actualRowSize;

        var rowDict = new Dictionary<string, object?>();
        var offset = 0;
        foreach (var c in cols)
        {
            var size = datParsingService.GetColumnSize(c, is64Bit);
            if (offset + size > actualRowSize)
            {
                break;
            }

            var val = datParsingService.ReadColumnValue(data, rowStart + offset, c, is64Bit, separatorIndex);

            // Recursive Foreign Key Resolution (Limit 5)
            if (c.Type == "foreignrow" && val != "null" && val.StartsWith("FK(") && currentDepth < 5)
            {
                var targetTable = c.References?.Table;
                if (string.IsNullOrEmpty(targetTable))
                {
                    targetTable = c.Name;
                }

                var hexId = val.Substring(3, val.Length - 4);
                if (!string.IsNullOrEmpty(targetTable) &&
                    long.TryParse(hexId, NumberStyles.HexNumber, null, out var foreignId))
                {
                    try
                    {
                        var foreignData = await ResolveForeignRow(targetTable, foreignId, currentDepth + 1);
                        rowDict[c.Name ?? "unk"] = foreignData ?? val;
                    }
                    catch
                    {
                        rowDict[c.Name ?? "unk"] = val;
                    }
                }
                else
                {
                    rowDict[c.Name ?? "unk"] = val;
                }
            }
            else
            {
                rowDict[c.Name ?? "unk"] = val;
            }

            offset += size;
        }

        return rowDict;
    }

    private GGPKTreeNode? FindNodeByName(IEnumerable<GGPKTreeNode> nodes, string name)
    {
        foreach (var node in nodes)
        {
            if (node.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            if (node.Children.Count > 0)
            {
                var found = FindNodeByName(node.Children, name);
                if (found != null)
                {
                    return found;
                }
            }
        }

        return null;
    }
}