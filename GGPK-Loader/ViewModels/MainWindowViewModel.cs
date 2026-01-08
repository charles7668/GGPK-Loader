using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GGPK_Loader.Models;
using GGPK_Loader.Services;
using JetBrains.Annotations;
using File = System.IO.File;

namespace GGPK_Loader.ViewModels;

[method: UsedImplicitly]
public partial class MainWindowViewModel(IFileService fileService, IMessageService messageService) : ViewModelBase
{
    // For Design Time Preview
    public MainWindowViewModel() : this(null!, null!)
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
        else
        {
            NodeInfoText = string.Empty;
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
            fileName = (string?)node.Value ?? string.Empty;
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

    private static bool IsTextFile(string fileName)
    {
        return fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase);
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

    [RelayCommand]
    private async Task OpenGgpkFile()
    {
        var filePath = await fileService.OpenFileAsync();
        if (filePath != null)
        {
            _currentFilePath = filePath;
            Debug.WriteLine($"Selected file in VM: {filePath}");

            ButtonText = "Loading";
            try
            {
                var rootNode = await Task.Run(async () =>
                {
                    await using var stream = File.OpenRead(filePath);
                    using var reader = new BinaryReader(stream);

                    GGPKRoot root;
                    {
                        var length = reader.ReadUInt32();
                        var tag = reader.ReadChars(4);
                        var version = reader.ReadUInt32();
                        var offset = reader.ReadUInt64();
                        var offset2 = reader.ReadUInt64();

                        root = new GGPKRoot
                        {
                            Length = length,
                            Tag = tag,
                            Version = version,
                            Entries =
                            [
                                new GGPKEntry { Offset = offset },
                                new GGPKEntry { Offset = offset2 }
                            ]
                        };

                        var tagString = new string(root.Tag);
                        if (tagString != "GGPK")
                        {
                            throw new InvalidDataException("Invalid GGPK file format.");
                        }

                        Debug.WriteLine($"GGPK Length: {root.Length}");
                        Debug.WriteLine($"GGPK Tag: {tagString}");
                        Debug.WriteLine($"GGPK Version: {root.Version}");
                    }

                    var entryQueue = new Queue<GGPKTreeNode>();
                    var offsetQueue = new Queue<ulong>();
                    var rootNodeInternal = new GGPKTreeNode("", 0);
                    entryQueue.Enqueue(rootNodeInternal);
                    offsetQueue.Enqueue(root.Entries[0].Offset);

                    while (entryQueue.Count > 0)
                    {
                        var currentNode = entryQueue.Dequeue();
                        var currentOffset = offsetQueue.Dequeue();
                        stream.Seek((long)currentOffset, SeekOrigin.Begin);

                        var entryLength = reader.ReadUInt32();
                        var entryTag = new string(reader.ReadChars(4));

                        switch (entryTag)
                        {
                            case "FILE":
                                var fileNameLength = reader.ReadUInt32();
                                var fileHash = reader.ReadBytes(32);
                                var fileNameBytes = reader.ReadBytes((int)fileNameLength * 2);
                                var fileName = Encoding.Unicode.GetString(fileNameBytes).TrimEnd('\0');

                                var headerSize = 4 + 4 + 4 + 32 + fileNameLength * 2;
                                var dataSize = entryLength - headerSize;

                                var fileNode = new GGPKTreeNode((currentNode.Value + "/" + fileName).Replace("//", "/"),
                                    currentOffset);
                                currentNode.Children.Add(fileNode);

                                Debug.WriteLine($"FILE Name: {fileNode.Value}, Size: {dataSize}");
                                break;
                            case "PDIR":
                                var nameLength = reader.ReadUInt32();
                                var totalEntries = reader.ReadUInt32();
                                var sha256 = reader.ReadBytes(32);
                                var nameBytes = reader.ReadBytes((int)nameLength * 2);
                                var name = Encoding.Unicode.GetString(nameBytes).TrimEnd('\0');

                                var nextNode = new GGPKTreeNode((currentNode.Value + "/" + name).Replace("//", "/"),
                                    currentNode.Offset);
                                currentNode.Children.Add(nextNode);
                                Debug.WriteLine($"PDIR Name: {nextNode.Value}");

                                for (var i = 0; i < totalEntries; i++)
                                {
                                    reader.ReadInt32(); // entry name hash
                                    var entryOffset = reader.ReadUInt64();

                                    offsetQueue.Enqueue(entryOffset);
                                    entryQueue.Enqueue(nextNode);
                                }

                                break;
                            case "FREE":
                                break;
                            default:
                                Debug.WriteLine($"Unknown Tag: {entryTag} at {currentNode.Offset:X}");
                                break;
                        }
                    }

                    return rootNodeInternal.Children.Count > 0 ? rootNodeInternal.Children[0] : rootNodeInternal;
                });

                GgpkNodes.Clear();
                GgpkNodes.Add(rootNode);
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
}