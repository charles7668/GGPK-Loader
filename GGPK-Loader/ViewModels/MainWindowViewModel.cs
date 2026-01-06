using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
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

    public string Greeting { get; } = "Welcome to Avalonia!";

    [ObservableProperty]
    private ObservableCollection<GGPKTreeNode> _ggpkNodes = new ObservableCollection<GGPKTreeNode>();

    [RelayCommand]
    private async Task OpenGgpkFile()
    {
        var filePath = await fileService.OpenFileAsync();
        if (filePath != null)
        {
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