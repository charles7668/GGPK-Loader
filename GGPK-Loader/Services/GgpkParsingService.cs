using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GGPK_Loader.Models;
using GGPK_Loader.Utils;
using JetBrains.Annotations;

namespace GGPK_Loader.Services;

public class GgpkParsingService : IGgpkParsingService
{
    private readonly SemaphoreSlim _streamSemaphore = new(1, 1);

    [UsedImplicitly]
    private string? _ggpkFilePath;

    private Stream? _ggpkStream;

    public void OpenStream(string filePath)
    {
        _ggpkFilePath = filePath;
        _ggpkStream = File.OpenRead(filePath);
    }

    public void CloseStream()
    {
        _ggpkStream?.Dispose();

        _ggpkStream = null;
        _ggpkFilePath = null;
    }

    public async Task<GGPKTreeNode> BuildGgpkTreeAsync()
    {
        ThrowIfStreamNotOpen();
        return await Task.Run(() =>
        {
            var stream = _ggpkStream!;
            stream.Seek(0, SeekOrigin.Begin);

            var ggpkHeader = ReadGGPKHeader();
            var entryQueue = new Queue<GGPKTreeNode>();
            var offsetQueue = new Queue<ulong>();
            var rootNodeInternal = new GGPKTreeNode("", 0);

            entryQueue.Enqueue(rootNodeInternal);
            offsetQueue.Enqueue(ggpkHeader.Entries[0].Offset);

            Span<byte> entryBuffer = stackalloc byte[8];

            while (entryQueue.Count > 0)
            {
                var currentNode = entryQueue.Dequeue();
                var currentOffset = offsetQueue.Dequeue();
                stream.Seek((long)currentOffset, SeekOrigin.Begin);

                stream.ReadExactly(entryBuffer); // entry length
                var entryTag = Encoding.ASCII.GetString(entryBuffer[4..]);

                switch (entryTag)
                {
                    case "FILE":
                        HandleFileNode(currentNode, currentOffset);
                        break;
                    case "PDIR":
                        HandlePdirNode(currentNode, currentOffset, entryQueue, offsetQueue);
                        break;
                    case "FREE":
                        break;
                    default:
                        Debug.WriteLine($"Unknown Tag: {entryTag} at {currentOffset:X}");
                        break;
                }
            }

            var newRootNode = rootNodeInternal.Children.Count > 0 ? rootNodeInternal.Children[0] : rootNodeInternal;
            newRootNode.Parent = null;
            return newRootNode;
        });
    }

    public async Task<GGPKTreeNode?> BuildBundleTreeAsync(GGPKTreeNode ggpkRootNode, string ggpkFilePath)
    {
        return await Task.Run(async () =>
        {
            var bundleRootNode =
                ggpkRootNode.Children.FirstOrDefault(child =>
                {
                    if (child.Value is GGPKDirInfo childInfo)
                    {
                        return childInfo.Name == "Bundles2";
                    }

                    return false;
                });

            return bundleRootNode == null ? null : await ProcessBundleAsync(bundleRootNode);
        });
    }

    public async Task<byte[]> LoadBundleFileDataAsync(GGPKFileInfo ggpkBundleFileInfo,
        BundleIndexInfo.FileRecord bundleFileRecord, CancellationToken ct)
    {
        ThrowIfStreamNotOpen();

        var stream = _ggpkStream!;
        var buffer = ArrayPool<byte>.Shared.Rent((int)ggpkBundleFileInfo.DataSize);

        try
        {
            if (stream is FileStream fs)
            {
                await RandomAccess.ReadAsync(fs.SafeFileHandle, buffer, ggpkBundleFileInfo.DataOffset, ct);
            }
            else
            {
                await _streamSemaphore.WaitAsync(ct);
                try
                {
                    stream.Seek(ggpkBundleFileInfo.DataOffset, SeekOrigin.Begin);
                    await stream.ReadExactlyAsync(buffer, 0, (int)ggpkBundleFileInfo.DataSize, ct);
                }
                finally
                {
                    _streamSemaphore.Release();
                }
            }

            var dataSpan = new ReadOnlySpan<byte>(buffer, 0, (int)ggpkBundleFileInfo.DataSize);

            var processingSpan = dataSpan;
            var bundleInfo = ReadBundleInfo(ref processingSpan);
            var startBlock = Math.DivRem(bundleFileRecord.FileOffset, bundleInfo.Head.UncompressedBlockGranularity,
                out var remainder);
            var endBlock =
                (bundleFileRecord.FileOffset + bundleFileRecord.FileSize - 1) /
                bundleInfo.Head.UncompressedBlockGranularity + 1;

            var resultBuffer = GC.AllocateUninitializedArray<byte>(endBlock == bundleInfo.Head.BlockCount
                ? (int)(bundleInfo.UncompressedSize - bundleInfo.Head.UncompressedBlockGranularity * startBlock)
                : (int)(bundleInfo.Head.UncompressedBlockGranularity * (endBlock - startBlock)));

            DecompressBlocks(bundleInfo, ref processingSpan, resultBuffer, startBlock, endBlock);

            return new ReadOnlySpan<byte>(resultBuffer, (int)remainder, (int)bundleFileRecord.FileSize).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public Task<byte[]> LoadGGPKFileDataAsync(GGPKFileInfo ggpkFileInfo, ulong size, CancellationToken ct)
    {
        ThrowIfStreamNotOpen();

        var stream = _ggpkStream!;

        return Task.Run(async () =>
        {
            var buffer = new byte[size];
            if (stream is FileStream fs)
            {
                await RandomAccess.ReadAsync(fs.SafeFileHandle, buffer, ggpkFileInfo.DataOffset, ct);
            }
            else
            {
                await _streamSemaphore.WaitAsync(ct);
                try
                {
                    stream.Seek(ggpkFileInfo.DataOffset, SeekOrigin.Begin);
                    await stream.ReadExactlyAsync(buffer, ct);
                }
                finally
                {
                    _streamSemaphore.Release();
                }
            }

            return buffer;
        }, ct);
    }

    public async Task ReplaceFileDataAsync(ReadOnlyMemory<byte> replaceSource, GGPKTreeNode targetNode,
        CancellationToken ct)
    {
        if (targetNode.Value is BundleIndexInfo.FileRecord)
        {
            // todo process bundle file
            throw new NotImplementedException("Not support bundle file yet");
        }

        // if not GGPKFileInfo
        if (targetNode.Value is not GGPKFileInfo targetFileInfo)
        {
            return;
        }

        if (targetFileInfo.DataSize == (ulong)replaceSource.Length)
        {
            await ReplaceDataWithSameSize(replaceSource, targetNode, targetFileInfo, ct);
            return;
        }

        if ((targetFileInfo.DataSize - GGPKFreeInfo.HeaderSize) >= (ulong)replaceSource.Length)
        {
            await ReplaceDataWithSmallSize(replaceSource, targetNode, targetFileInfo, ct);
            return;
        }

        await ReplaceDataWithLargeSize(replaceSource, targetNode, targetFileInfo, ct);
    }

    private async Task ReplaceDataWithLargeSize(ReadOnlyMemory<byte> replaceSource, GGPKTreeNode targetNode,
        GGPKFileInfo targetFileInfo, CancellationToken ct)
    {
        var requiredSize = (uint)(targetFileInfo.HeaderSize + (ulong)replaceSource.Length);
        // First, attempt to find a free block matching the exact required size. If unavailable, search for a block that also accommodates the free info header.
        var targetFreeInfo = FindFreeSpace(requiredSize, FreeSpaceSearchMode.Equal) ??
                             FindFreeSpace(requiredSize + GGPKFreeInfo.HeaderSize);
        var lastFreeInfo = FindLastFreeSpace();

        ThrowIfStreamNotOpen();
        var filePath = _ggpkFilePath!;
        await _streamSemaphore.WaitAsync(ct);
        CloseStream();

        try
        {
            await using var fs = File.OpenWrite(filePath);

            long writeOffset;
            long pointerToNextOffset;

            if (targetFreeInfo != null)
            {
                writeOffset = (long)(targetFreeInfo.DataOffset - GGPKFreeInfo.HeaderSize);

                if (targetFreeInfo.Length > requiredSize + GGPKFreeInfo.HeaderSize)
                {
                    // Split the free block
                    var tailOffset = writeOffset + requiredSize;
                    var tailLength = targetFreeInfo.Length - requiredSize;

                    // Update Previous to point to tail, depends on ggpk format, support previous free info is not null 
                    var prevNextOffsetPos =
                        (long)(targetFreeInfo.PreviousFreeInfo!.DataOffset - GGPKFreeInfo.HeaderSize + 8);

                    var nextBuffer = new byte[8];
                    BinaryPrimitives.WriteUInt64LittleEndian(nextBuffer, (ulong)tailOffset);
                    await RandomAccess.WriteAsync(fs.SafeFileHandle, nextBuffer, prevNextOffsetPos, ct);

                    // Create new Free Header at tail, pointing to original Next
                    var splitFreeHeaderBuffer = new byte[16];
                    BinaryPrimitives.WriteUInt32LittleEndian(splitFreeHeaderBuffer.AsSpan(0, 4), tailLength);
                    "FREE"u8.ToArray().CopyTo(splitFreeHeaderBuffer, 4);
                    BinaryPrimitives.WriteUInt64LittleEndian(splitFreeHeaderBuffer.AsSpan(8, 8),
                        targetFreeInfo.NextOffset);
                    await RandomAccess.WriteAsync(fs.SafeFileHandle, splitFreeHeaderBuffer, tailOffset, ct);
                }
                else
                {
                    var prevNextOffsetPos =
                        (long)(targetFreeInfo.PreviousFreeInfo!.DataOffset - GGPKFreeInfo.HeaderSize + 8);

                    var nextBuffer = new byte[8];
                    BinaryPrimitives.WriteUInt64LittleEndian(nextBuffer, targetFreeInfo.NextOffset);
                    await RandomAccess.WriteAsync(fs.SafeFileHandle, nextBuffer, prevNextOffsetPos, ct);
                }
            }
            else
            {
                writeOffset = fs.Length;
            }

            pointerToNextOffset = (long)(lastFreeInfo!.DataOffset - GGPKFreeInfo.HeaderSize + 8);

            // Write New File Header + Data
            var headerBuffer = new byte[targetFileInfo.HeaderSize];
            BinaryPrimitives.WriteUInt32LittleEndian(headerBuffer.AsSpan(0, 4), requiredSize);
            "FILE"u8.ToArray().CopyTo(headerBuffer, 4);
            BinaryPrimitives.WriteUInt32LittleEndian(headerBuffer.AsSpan(8, 4), targetFileInfo.FileNameLength);

            var newHash = SHA256.HashData(replaceSource.Span);
            newHash.CopyTo(headerBuffer, 12);

            var fileNameBytes = new byte[targetFileInfo.FileNameLength * 2];
            Encoding.Unicode.GetBytes(targetFileInfo.FileName).CopyTo(fileNameBytes, 0);
            fileNameBytes.CopyTo(headerBuffer, 44);

            await RandomAccess.WriteAsync(fs.SafeFileHandle, headerBuffer, writeOffset, ct);

            var newDataOffset = writeOffset + (long)targetFileInfo.HeaderSize;
            await RandomAccess.WriteAsync(fs.SafeFileHandle, replaceSource, newDataOffset, ct);

            // Convert Old File to FREE & Link
            var oldOffset = (long)targetNode.Offset;
            var oldLength = targetFileInfo.Length;

            var freeHeaderBuffer = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(freeHeaderBuffer.AsSpan(0, 4), oldLength);
            "FREE"u8.ToArray().CopyTo(freeHeaderBuffer, 4);
            BinaryPrimitives.WriteUInt64LittleEndian(freeHeaderBuffer.AsSpan(8, 8), 0);

            await RandomAccess.WriteAsync(fs.SafeFileHandle, freeHeaderBuffer, oldOffset, ct);

            // Link old free node to list
            var linkBuffer = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(linkBuffer, (ulong)oldOffset);
            await RandomAccess.WriteAsync(fs.SafeFileHandle, linkBuffer, pointerToNextOffset, ct);

            // Update Memory
            var newFileInfo = targetFileInfo with
            {
                SHA256Hash = newHash,
                Length = requiredSize,
                DataOffset = newDataOffset,
                DataSize = (ulong)replaceSource.Length
            };
            targetNode.Offset = (ulong)writeOffset;
            targetNode.Value = newFileInfo;

            // Propagate Hash
            var parentsQueue = new Queue<GGPKTreeNode>();
            if (targetNode.Parent?.Value is GGPKDirInfo parentDirInfo)
            {
                var childIndex = targetNode.Parent.Children.IndexOf(targetNode);
                if (childIndex >= 0)
                {
                    var entryOffsetPos = (long)targetNode.Parent.Offset + 48 + parentDirInfo.NameLength * 2 +
                                         childIndex * 12 + 4;
                    var newOffsetBuffer = new byte[8];
                    BinaryPrimitives.WriteUInt64LittleEndian(newOffsetBuffer, (ulong)writeOffset);
                    await RandomAccess.WriteAsync(fs.SafeFileHandle, newOffsetBuffer, entryOffsetPos, ct);

                    var newEntries = parentDirInfo.DirectoryEntries.ToArray();
                    newEntries[childIndex] = newEntries[childIndex] with { Offset = (ulong)writeOffset };
                    targetNode.Parent.Value = parentDirInfo with { DirectoryEntries = newEntries };
                }

                parentsQueue.Enqueue(targetNode.Parent);
            }

            while (parentsQueue.Count > 0)
            {
                var parentNode = parentsQueue.Dequeue();
                var childHashes = parentNode.Children
                    .Select(child => child.Value)
                    .Select(val => val switch
                    {
                        GGPKFileInfo f => f.SHA256Hash,
                        GGPKDirInfo d => d.SHA256Hash,
                        _ => null
                    })
                    .OfType<byte[]>()
                    .ToList();

                var parentNewHash =
                    SHA256.HashData(childHashes.SelectMany(x => x).ToArray());

                var parentHashOffset = parentNode.Offset + 16;
                await RandomAccess.WriteAsync(fs.SafeFileHandle, parentNewHash, (long)parentHashOffset, ct);

                parentNode.Value = (GGPKDirInfo)parentNode.Value with { SHA256Hash = parentNewHash };

                if (parentNode.Parent?.Value is GGPKDirInfo)
                {
                    parentsQueue.Enqueue(parentNode.Parent);
                }
            }
        }
        finally
        {
            OpenStream(filePath);
            _streamSemaphore.Release();
        }
    }

    private async Task ReplaceDataWithSmallSize(ReadOnlyMemory<byte> replaceSource, GGPKTreeNode targetNode,
        GGPKFileInfo targetFileInfo, CancellationToken ct)
    {
        var lastFreeInfo = FindLastFreeSpace();

        var filePath = _ggpkFilePath!;
        await _streamSemaphore.WaitAsync(ct);
        CloseStream();
        try
        {
            await using var fs = File.OpenWrite(filePath);

            // Replace content
            await RandomAccess.WriteAsync(fs.SafeFileHandle, replaceSource, targetFileInfo.DataOffset, ct);

            // Change hash
            var newHash = System.Security.Cryptography.SHA256.HashData(replaceSource.Span);
            var hashOffset = targetFileInfo.HashOffset;
            await RandomAccess.WriteAsync(fs.SafeFileHandle, newHash, (int)hashOffset, ct);

            // Change length data
            var newEntryLength = (uint)(targetFileInfo.HeaderSize + (ulong)replaceSource.Length);
            var lengthBuffer = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(lengthBuffer, newEntryLength);
            await RandomAccess.WriteAsync(fs.SafeFileHandle, lengthBuffer, (long)targetNode.Offset, ct);

            // Create FREE Entry in the remaining space
            var freeEntryOffset = targetFileInfo.DataOffset + replaceSource.Length;
            var freeEntryLength = (uint)(targetFileInfo.DataSize - (ulong)replaceSource.Length);

            // Construct Free Header
            // Length (4), Tag (4), NextOffset (8)
            var freeHeaderBuffer = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(freeHeaderBuffer.AsSpan(0, 4), freeEntryLength);
            "FREE"u8.ToArray().CopyTo(freeHeaderBuffer, 4);
            BinaryPrimitives.WriteUInt64LittleEndian(freeHeaderBuffer.AsSpan(8, 8), 0);

            await RandomAccess.WriteAsync(fs.SafeFileHandle, freeHeaderBuffer, freeEntryOffset, ct);

            // Link new FREE entry to the list
            var lastFreeNextOffsetPos = (long)(lastFreeInfo.DataOffset - GGPKFreeInfo.HeaderSize + 8);
            var nextOffsetBuffer = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(nextOffsetBuffer, (ulong)freeEntryOffset);
            await RandomAccess.WriteAsync(fs.SafeFileHandle, nextOffsetBuffer, lastFreeNextOffsetPos, ct);

            var newFileInfo = targetFileInfo with
            {
                SHA256Hash = newHash,
                Length = newEntryLength,
                DataSize = (ulong)replaceSource.Length
            };
            targetNode.Value = newFileInfo;

            // Update Parent Hashes
            var parentsQueue = new Queue<GGPKTreeNode>();
            if (targetNode.Parent?.Value is GGPKDirInfo)
            {
                parentsQueue.Enqueue(targetNode.Parent);
            }

            while (parentsQueue.Count > 0)
            {
                var parentNode = parentsQueue.Dequeue();
                var childHashes = parentNode.Children
                    .Select(child => child.Value)
                    .Select(val => val switch
                    {
                        GGPKFileInfo f => f.SHA256Hash,
                        GGPKDirInfo d => d.SHA256Hash,
                        _ => null
                    })
                    .OfType<byte[]>()
                    .ToList();

                var parentNewHash =
                    System.Security.Cryptography.SHA256.HashData(childHashes.SelectMany(x => x).ToArray());
                // Length(4) + Tag(4) + NameLength(4) + TotalEntries(4) = 16
                var parentHashOffset = parentNode.Offset + 16;
                await RandomAccess.WriteAsync(fs.SafeFileHandle, parentNewHash, (long)parentHashOffset, ct);

                parentNode.Value = (GGPKDirInfo)parentNode.Value with { SHA256Hash = parentNewHash };

                if (parentNode.Parent?.Value is GGPKDirInfo)
                {
                    parentsQueue.Enqueue(parentNode.Parent);
                }
            }
        }
        finally
        {
            OpenStream(filePath);
            _streamSemaphore.Release();
        }
    }

    private enum FreeSpaceSearchMode
    {
        GreaterOrEqual,
        Equal
    }

    private GGPKFreeInfo? FindFreeSpace(ulong requiredSize,
        FreeSpaceSearchMode mode = FreeSpaceSearchMode.GreaterOrEqual)
    {
        ThrowIfStreamNotOpen();
        var ggpkHeader = ReadGGPKHeader();
        GGPKFreeInfo? previousFreeInfo = null;
        var nextOffset = ggpkHeader.Entries[1].Offset;

        while (nextOffset > 0)
        {
            var freeInfo = ReadGGPKFreeInfo(nextOffset, previousFreeInfo);
            var currentSize = freeInfo.DataLength + GGPKFreeInfo.HeaderSize;

            var isMatch = mode switch
            {
                FreeSpaceSearchMode.Equal => currentSize == requiredSize,
                FreeSpaceSearchMode.GreaterOrEqual => currentSize >= requiredSize,
                _ => false
            };

            if (isMatch && freeInfo.PreviousFreeInfo != null)
            {
                return freeInfo;
            }

            previousFreeInfo = freeInfo;
            nextOffset = freeInfo.NextOffset;
        }

        return null;
    }

    private GGPKFreeInfo? FindLastFreeSpace()
    {
        ThrowIfStreamNotOpen();
        var ggpkHeader = ReadGGPKHeader();
        GGPKFreeInfo? previousFreeInfo = null;
        var nextOffset = ggpkHeader.Entries[1].Offset;

        while (nextOffset > 0)
        {
            var freeInfo = ReadGGPKFreeInfo(nextOffset, previousFreeInfo);
            previousFreeInfo = freeInfo;
            nextOffset = freeInfo.NextOffset;
        }

        return previousFreeInfo;
    }

    private async Task ReplaceDataWithSameSize(ReadOnlyMemory<byte> replaceSource, GGPKTreeNode targetNode,
        GGPKFileInfo targetFileInfo, CancellationToken ct)
    {
        ThrowIfStreamNotOpen();
        var filePath = _ggpkFilePath!;
        await _streamSemaphore.WaitAsync(ct);
        CloseStream();
        try
        {
            await using var fs = File.OpenWrite(filePath);
            await RandomAccess.WriteAsync(fs.SafeFileHandle, replaceSource, targetFileInfo.DataOffset, ct);

            var newHash = System.Security.Cryptography.SHA256.HashData(replaceSource.Span);

            var hashOffset = targetFileInfo.DataOffset - targetFileInfo.FileNameLength * 2 - 32;
            await RandomAccess.WriteAsync(fs.SafeFileHandle, newHash, hashOffset, ct);

            var newFileInfo = targetFileInfo with { SHA256Hash = newHash };
            targetNode.Value = newFileInfo;

            var parentsQueue = new Queue<GGPKTreeNode>();
            if (targetNode.Parent?.Value is GGPKDirInfo)
            {
                parentsQueue.Enqueue(targetNode.Parent);
            }

            while (parentsQueue.Count > 0)
            {
                var parentNode = parentsQueue.Dequeue();
                var childHashes = parentNode.Children
                    .Select(child => child.Value)
                    .Select(val => val switch
                    {
                        GGPKFileInfo f => f.SHA256Hash,
                        GGPKDirInfo d => d.SHA256Hash,
                        _ => null
                    })
                    .OfType<byte[]>()
                    .ToList();

                var parentNewHash =
                    System.Security.Cryptography.SHA256.HashData(childHashes.SelectMany(x => x).ToArray());
                // Length(4) + Tag(4) + NameLength(4) + TotalEntries(4) = 16
                var parentHashOffset = parentNode.Offset + 16;
                await RandomAccess.WriteAsync(fs.SafeFileHandle, parentNewHash, (long)parentHashOffset, ct);

                parentNode.Value = (GGPKDirInfo)parentNode.Value with { SHA256Hash = parentNewHash };

                if (parentNode.Parent?.Value is GGPKDirInfo)
                {
                    parentsQueue.Enqueue(parentNode.Parent);
                }
            }
        }
        finally
        {
            OpenStream(filePath);
            _streamSemaphore.Release();
        }
    }

    public Task<byte[]> LoadGGPKFileDataAsync(GGPKFileInfo ggpkFileInfo, CancellationToken ct)
    {
        return LoadGGPKFileDataAsync(ggpkFileInfo, ggpkFileInfo.DataSize, ct);
    }

    private void ThrowIfStreamNotOpen()
    {
        if (_ggpkStream is { CanRead: true })
        {
            return;
        }

        throw new InvalidOperationException("GGPK stream is not open");
    }

    private GGPKHeader ReadGGPKHeader()
    {
        ThrowIfStreamNotOpen();
        var stream = _ggpkStream!;
        stream.Seek(0, SeekOrigin.Begin);

        Span<byte> buffer = stackalloc byte[28];
        stream.ReadExactly(buffer);

        var length = BinaryPrimitives.ReadUInt32LittleEndian(buffer[..4]);
        var tagBytes = buffer.Slice(4, 4);
        var tag = new char[4];
        for (var i = 0; i < 4; i++)
        {
            tag[i] = (char)tagBytes[i];
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(8, 4));
        var offset = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(12, 8));
        var offset2 = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(20, 8));

        var root = new GGPKHeader
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

        return root;
    }

    private void HandleFileNode(GGPKTreeNode currentNode, ulong currentOffset)
    {
        ThrowIfStreamNotOpen();
        var fileInfo = ReadGGPKFileInfo(currentOffset);

        var fileNode = new GGPKTreeNode(fileInfo, currentOffset)
        {
            Parent = currentNode
        };
        currentNode.Children.Add(fileNode);

        // Debug.WriteLine($"FILE Name: {fileInfo.FileName}, Size: {fileInfo.DataSize}");
    }

    private void HandlePdirNode(GGPKTreeNode currentNode,
        ulong pdirOffset,
        Queue<GGPKTreeNode> entryQueue, Queue<ulong> offsetQueue)
    {
        var dirInfo = ReadGGPKDirInfo(pdirOffset);
        var nextNode = new GGPKTreeNode(dirInfo, pdirOffset)
        {
            Parent = currentNode
        };
        currentNode.Children.Add(nextNode);
        // Debug.WriteLine($"PDIR Name: {nextNode.Value}");

        for (var i = 0; i < dirInfo.TotalEntries; i++)
        {
            offsetQueue.Enqueue(dirInfo.DirectoryEntries[i].Offset);
            entryQueue.Enqueue(nextNode);
        }
    }

    private async Task<GGPKTreeNode?> ProcessBundleAsync(GGPKTreeNode bundleRootNode)
    {
        ThrowIfStreamNotOpen();
        var indexBinNode = GetIndexBinNode(bundleRootNode);
        if (indexBinNode?.Value is not GGPKFileInfo indexFileInfo)
        {
            return null;
        }

        Debug.WriteLine($"Found index.bin: {indexBinNode.Value}");

        var ggpkStream = _ggpkStream!;
        using var buffer = new PooledBuffer((int)indexFileInfo.DataSize);

        if (ggpkStream is FileStream fs)
        {
            await RandomAccess.ReadAsync(fs.SafeFileHandle, buffer.Data.AsMemory(0, (int)indexFileInfo.DataSize),
                indexFileInfo.DataOffset);
        }
        else
        {
            await _streamSemaphore.WaitAsync();
            try
            {
                ggpkStream.Seek(indexFileInfo.DataOffset, SeekOrigin.Begin);
                await ggpkStream.ReadExactlyAsync(buffer.Data, 0, (int)indexFileInfo.DataSize);
            }
            finally
            {
                _streamSemaphore.Release();
            }
        }

        var dataSpan = new ReadOnlySpan<byte>(buffer.Data, 0, (int)indexFileInfo.DataSize);
        var bundleInfo = ReadBundleInfo(ref dataSpan);

        using var decompressed = new PooledBuffer((int)bundleInfo.UncompressedSize);
        var decompressedSize = DecompressBlocks(bundleInfo, ref dataSpan, decompressed.Data);

        if (decompressedSize != bundleInfo.UncompressedSize)
        {
            return null;
        }

        var bundleIndexInfo =
            ReadBundleIndex(new ReadOnlySpan<byte>(decompressed.Data, 0, (int)bundleInfo.UncompressedSize));

        var pathRepSpan = new ReadOnlySpan<byte>(bundleIndexInfo.PathRepBundle);
        var tempPathRepSpan = pathRepSpan;
        var pathRepBundleInfo = ReadBundleInfo(ref tempPathRepSpan);

        using var decompressedDirectory = new PooledBuffer((int)pathRepBundleInfo.UncompressedSize);

        if (DecompressOodleBundle(pathRepSpan, decompressedDirectory.Data) != pathRepBundleInfo.UncompressedSize)
        {
            return null;
        }

        var newBundleRootNode = new GGPKTreeNode("/", 0);
        ParsePaths(bundleIndexInfo.PathReps, bundleIndexInfo.Files, decompressedDirectory.Data, newBundleRootNode);
        newBundleRootNode.Value = bundleIndexInfo;
        return newBundleRootNode;
    }

    private GGPKFileInfo ReadGGPKFileInfo(ulong? offset)
    {
        ThrowIfStreamNotOpen();
        var stream = _ggpkStream!;
        if (offset != null)
        {
            stream.Seek((long)offset, SeekOrigin.Begin);
        }

        Span<byte> headerBuffer = stackalloc byte[8];
        stream.ReadExactly(headerBuffer);
        var entryLength = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer);
        var entryTag = headerBuffer.Slice(4, 4).ToArray(); // Keep tag as byte array

        stream.ReadExactly(headerBuffer[..4]);
        var fileNameLength = BinaryPrimitives.ReadUInt32LittleEndian(headerBuffer[..4]);

        Span<byte> sha256Hash = stackalloc byte[32];
        stream.ReadExactly(sha256Hash);

        Span<byte> fileNameBytes = stackalloc byte[(int)fileNameLength * 2];
        stream.ReadExactly(fileNameBytes);
        var fileName = Encoding.Unicode.GetString(fileNameBytes).TrimEnd('\0');

        var headerSize = 4 + 4 + 4 + 32 + fileNameLength * 2;
        var dataOffset = stream.Position;
        var dataSize = entryLength - headerSize;
        stream.Seek(dataSize, SeekOrigin.Current); // Skip data
        return new GGPKFileInfo(
            entryLength,
            entryTag,
            fileNameLength,
            sha256Hash.ToArray(),
            fileName,
            dataOffset,
            dataSize
        );
    }

    private GGPKFreeInfo ReadGGPKFreeInfo(ulong? offset, GGPKFreeInfo? previousFreeInfo)
    {
        ThrowIfStreamNotOpen();
        var stream = _ggpkStream!;
        if (offset != null)
        {
            stream.Seek((long)offset, SeekOrigin.Begin);
        }

        Span<byte> buffer = stackalloc byte[16];
        stream.ReadExactly(buffer);
        var entryLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        var entryTag = buffer.Slice(4, 4).ToArray(); // Keep tag as byte array
        var nextOffset = BinaryPrimitives.ReadInt64LittleEndian(buffer[8..]);
        var dataOffset = stream.Position;
        var dataSize = entryLength - 16;
        return new GGPKFreeInfo(
            entryLength,
            entryTag,
            (ulong)nextOffset,
            (ulong)dataOffset,
            dataSize,
            previousFreeInfo
        );
    }

    private GGPKDirInfo ReadGGPKDirInfo(ulong? offset)
    {
        ThrowIfStreamNotOpen();
        var stream = _ggpkStream!;
        if (offset != null)
        {
            stream.Seek((long)offset, SeekOrigin.Begin);
        }

        Span<byte> buffer = stackalloc byte[8];
        stream.ReadExactly(buffer);
        var entryLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        var entryTag = buffer.Slice(4, 4).ToArray(); // Keep tag as byte array

        stream.ReadExactly(buffer);
        var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        var totalEntries = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]);

        Span<byte> sha256Hash = stackalloc byte[32];
        stream.ReadExactly(sha256Hash);

        Span<byte> nameBytes = stackalloc byte[(int)nameLength * 2];
        stream.ReadExactly(nameBytes);
        var dirName = Encoding.Unicode.GetString(nameBytes).TrimEnd('\0');

        Span<byte> entryBuffer = stackalloc byte[12];
        List<GGPKDirectoryEntry> entries = [];
        for (var i = 0; i < totalEntries; i++)
        {
            stream.ReadExactly(entryBuffer);
            var entryNameHash = BinaryPrimitives.ReadInt32LittleEndian(entryBuffer);
            var entryOffset = BinaryPrimitives.ReadUInt64LittleEndian(entryBuffer.Slice(4));
            entries.Add(new GGPKDirectoryEntry(entryNameHash, entryOffset));
        }

        return new GGPKDirInfo(
            entryLength,
            entryTag,
            nameLength,
            totalEntries,
            sha256Hash.ToArray(),
            dirName,
            entries.ToArray()
        );
    }

    private static BundleInfo ReadBundleInfo(ref ReadOnlySpan<byte> reader)
    {
        var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(reader);
        reader = reader[4..];
        var totalPayloadSize = BinaryPrimitives.ReadUInt32LittleEndian(reader);
        reader = reader[4..];
        var headPayloadSize = BinaryPrimitives.ReadUInt32LittleEndian(reader);
        reader = reader[4..];

        var firstFileEncode = (BundleEncodeType)BinaryPrimitives.ReadUInt32LittleEndian(reader);
        reader = reader[4..];
        var unk10 = BinaryPrimitives.ReadUInt32LittleEndian(reader);
        reader = reader[4..];
        var uncompressedSize2 = BinaryPrimitives.ReadUInt64LittleEndian(reader);
        reader = reader[8..];
        var totalPayloadSize2 = BinaryPrimitives.ReadUInt64LittleEndian(reader);
        reader = reader[8..];
        var blockCount = BinaryPrimitives.ReadUInt32LittleEndian(reader);
        reader = reader[4..];
        var uncompressedBlockGranularity = BinaryPrimitives.ReadUInt32LittleEndian(reader);
        reader = reader[4..];

        var unk28 = new uint[4];
        for (var i = 0; i < 4; i++)
        {
            unk28[i] = BinaryPrimitives.ReadUInt32LittleEndian(reader);
            reader = reader[4..];
        }

        var blockSizes = new uint[blockCount];
        for (var i = 0; i < blockCount; i++)
        {
            blockSizes[i] = BinaryPrimitives.ReadUInt32LittleEndian(reader);
            reader = reader[4..];
        }

        return new BundleInfo
        {
            UncompressedSize = uncompressedSize,
            TotalPayloadSize = totalPayloadSize,
            HeadPayloadSize = headPayloadSize,
            Head = new BundleInfo.HeadPayloadT
            {
                FirstFileEncode = firstFileEncode,
                Unk10 = unk10,
                UncompressedSize2 = uncompressedSize2,
                TotalPayloadSize2 = totalPayloadSize2,
                BlockCount = blockCount,
                UncompressedBlockGranularity = uncompressedBlockGranularity,
                Unk28 = unk28,
                BlockSizes = blockSizes
            }
        };
    }

    private static int DecompressBlocks(BundleInfo bundleInfo, ref ReadOnlySpan<byte> dataSpan, Span<byte> dest)
    {
        return DecompressBlocks(bundleInfo, ref dataSpan, dest, 0, bundleInfo.Head.BlockCount);
    }

    private static int DecompressBlocks(BundleInfo bundleInfo, ref ReadOnlySpan<byte> dataSpan, Span<byte> dest,
        long startBlock, long endBlock)
    {
        var firstBlockOffset = 0;
        for (var i = 0; i < startBlock; i++)
        {
            firstBlockOffset += (int)bundleInfo.Head.BlockSizes[i];
        }

        dataSpan = dataSpan.Slice(firstBlockOffset);

        var decompressedOffset = 0;
        for (var i = (int)startBlock; i < endBlock; i++)
        {
            var blockSize = bundleInfo.Head.BlockSizes[i];
            var compressedBlock = dataSpan[..(int)blockSize];
            dataSpan = dataSpan[(int)blockSize..];

            var theoreticalOffset = i * bundleInfo.Head.UncompressedBlockGranularity;
            var currentUncompressedSize = (int)Math.Min(bundleInfo.Head.UncompressedBlockGranularity,
                bundleInfo.UncompressedSize - theoreticalOffset);

            var blockResult = Oo2Core.Decompress(compressedBlock, compressedBlock.Length,
                dest.Slice(decompressedOffset, currentUncompressedSize),
                currentUncompressedSize);

            if (blockResult != currentUncompressedSize)
            {
                Debug.WriteLine(
                    $"Decompression failed for block {i}: Expected {currentUncompressedSize}, got {blockResult}");
            }

            decompressedOffset += currentUncompressedSize;
        }

        return decompressedOffset;
    }

    private static int DecompressOodleBundle(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        if (data.Length < 12)
        {
            return 0;
        }

        var dataSpan = data;
        var bundleInfo = ReadBundleInfo(ref dataSpan);

        return destination.Length < bundleInfo.UncompressedSize
            ? 0
            : DecompressBlocks(bundleInfo, ref dataSpan, destination);
    }

    private static BundleIndexInfo ReadBundleIndex(ReadOnlySpan<byte> data)
    {
        var bundleCount = BinaryPrimitives.ReadUInt32LittleEndian(data);
        data = data[4..];
        var bundles = new BundleIndexInfo.BundleRecord[bundleCount];

        for (var i = 0; i < bundleCount; i++)
        {
            var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(data);
            data = data[4..];
            var name = Encoding.UTF8.GetString(data[..(int)nameLength]);
            data = data[(int)nameLength..];
            var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(data);
            data = data[4..];
            bundles[i] = new BundleIndexInfo.BundleRecord(nameLength, name, uncompressedSize);
        }

        var fileCount = BinaryPrimitives.ReadUInt32LittleEndian(data);
        data = data[4..];
        var files = new BundleIndexInfo.FileRecord[fileCount];

        for (var i = 0; i < fileCount; i++)
        {
            var hash = BinaryPrimitives.ReadUInt64LittleEndian(data);
            data = data[8..];
            var bundleIndex = BinaryPrimitives.ReadUInt32LittleEndian(data);
            data = data[4..];
            var fileOffset = BinaryPrimitives.ReadUInt32LittleEndian(data);
            data = data[4..];
            var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(data);
            data = data[4..];
            files[i] = new BundleIndexInfo.FileRecord(hash, bundleIndex, fileOffset, fileSize);
        }

        var pathRepCount = BinaryPrimitives.ReadUInt32LittleEndian(data);
        data = data[4..];
        var pathReps = new BundleIndexInfo.PathRepRecord[pathRepCount];

        for (var i = 0; i < pathRepCount; i++)
        {
            var hash = BinaryPrimitives.ReadUInt64LittleEndian(data);
            data = data[8..];
            var payloadOffset = BinaryPrimitives.ReadUInt32LittleEndian(data);
            data = data[4..];
            var payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(data);
            data = data[4..];
            var payloadRecursiveSize = BinaryPrimitives.ReadUInt32LittleEndian(data);
            data = data[4..];
            pathReps[i] = new BundleIndexInfo.PathRepRecord(hash, payloadOffset, payloadSize, payloadRecursiveSize);
        }

        var pathRepBundle = data.ToArray();

        return new BundleIndexInfo
        {
            BundleCount = bundleCount,
            Bundles = bundles,
            FileCount = fileCount,
            Files = files,
            PathRepCount = pathRepCount,
            PathReps = pathReps,
            PathRepBundle = pathRepBundle
        };
    }

    private static GGPKTreeNode? GetIndexBinNode(GGPKTreeNode bundleRootNode)
    {
        GGPKTreeNode? indexBinNode = null; // Initialize here
        foreach (var child in bundleRootNode.Children)
        {
            if (child.Value is not GGPKFileInfo fileInfo ||
                (!fileInfo.FileName.EndsWith("_.index.bin", StringComparison.OrdinalIgnoreCase) &&
                 !fileInfo.FileName.EndsWith("index.bin", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            indexBinNode = child;
            break;
        }

        return indexBinNode;
    }

    private static void ParsePaths(BundleIndexInfo.PathRepRecord[] pathReps, BundleIndexInfo.FileRecord[] files,
        byte[] directory, GGPKTreeNode rootNode)
    {
        var fileDict = files.ToDictionary(x => x.Hash, x => x);
        ProcessPathRep(pathReps, fileDict, directory, rootNode);
    }

    private static void ProcessPathRep(IEnumerable<BundleIndexInfo.PathRepRecord> reps,
        Dictionary<ulong, BundleIndexInfo.FileRecord> fileRecordsDict, byte[] directory,
        GGPKTreeNode rootNode)
    {
        foreach (var rep in reps)
        {
            ProcessSinglePathRep(rep, fileRecordsDict, directory, rootNode);
        }
    }

    private static void ProcessSinglePathRep(BundleIndexInfo.PathRepRecord rep,
        Dictionary<ulong, BundleIndexInfo.FileRecord> fileRecordsDict, byte[] directory,
        GGPKTreeNode rootNode)
    {
        var temp = new List<string>();
        var isBase = false;

        var offset = (int)rep.PayloadOffset;
        var limit = offset + (int)rep.PayloadSize - 4;

        while (offset <= limit)
        {
            var index = BitConverter.ToInt32(directory, offset);
            offset += 4;

            if (index == 0)
            {
                isBase = !isBase;
                if (isBase)
                {
                    temp.Clear();
                }

                continue;
            }

            index -= 1;

            var str = ReadNullTerminatedString(directory, ref offset);

            if (index < temp.Count)
            {
                str = temp[index] + str;
            }

            if (isBase)
            {
                temp.Add(str);
            }
            else
            {
                if (fileRecordsDict.TryGetValue(MurmurHash64A(Encoding.ASCII.GetBytes(str)),
                        out var fileRecord))
                {
                    AddFileToBundleTree(rootNode, str, ref fileRecord);
                }
            }
        }
    }

    public static uint MurmurHash2(ReadOnlySpan<byte> data, uint seed = 0xC58F1A7Bu)
    {
        const uint m = 0x5BD1E995u;
        const int r = 24;

        unchecked
        {
            seed ^= (uint)data.Length;

            while (data.Length >= 4)
            {
                var k = BinaryPrimitives.ReadUInt32LittleEndian(data);
                k *= m;
                k ^= k >> r;
                k *= m;

                seed = (seed * m) ^ k;

                data = data[4..];
            }

            switch (data.Length)
            {
                case 3:
                    seed ^= (uint)data[2] << 16;
                    goto case 2;
                case 2:
                    seed ^= (uint)data[1] << 8;
                    goto case 1;
                case 1:
                    seed ^= data[0];
                    seed *= m;
                    break;
            }

            seed ^= seed >> 13;
            seed *= m;
            return seed ^ (seed >> 15);
        }
    }

    public static ulong MurmurHash64A(ReadOnlySpan<byte> utf8Name, ulong seed = 0x1337B33F)
    {
        if (utf8Name.IsEmpty)
        {
            return 0xF42A94E69CFF42FEul;
        }

        if (utf8Name[^1] == '/')
        {
            utf8Name = utf8Name[..^1];
        }

        const ulong m = 0xC6A4A7935BD1E995ul;
        const int r = 47;

        unchecked
        {
            seed ^= (ulong)utf8Name.Length * m;

            while (utf8Name.Length >= 8)
            {
                var k = BinaryPrimitives.ReadUInt64LittleEndian(utf8Name);
                k *= m;
                k ^= k >> r;
                k *= m;

                seed ^= k;
                seed *= m;

                utf8Name = utf8Name[8..];
            }

            if (utf8Name.Length > 0)
            {
                ulong tail = 0;
                for (var i = 0; i < utf8Name.Length; i++)
                {
                    tail |= (ulong)utf8Name[i] << (i * 8);
                }

                seed ^= tail;
                seed *= m;
            }

            seed ^= seed >> r;
            seed *= m;
            return seed ^ (seed >> r);
        }
    }

    private static string ReadNullTerminatedString(byte[] directory, ref int offset)
    {
        var strStart = offset;
        while (offset < directory.Length && directory[offset] != 0)
        {
            offset++;
        }

        var str = Encoding.UTF8.GetString(directory, strStart, offset - strStart);
        offset++; // Skip '\0'
        return str;
    }

    private static void AddFileToBundleTree(GGPKTreeNode root, string path, ref BundleIndexInfo.FileRecord fileRecord)
    {
        var parts = path.Split('/');
        var currentNode = root;

        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part))
            {
                continue;
            }

            GGPKTreeNode? foundChild = null;
            foreach (var child in currentNode.Children)
            {
                if (child.Value is not string name || name != part)
                {
                    continue;
                }

                foundChild = child;
                break;
            }

            if (foundChild == null)
            {
                foundChild = new GGPKTreeNode(part, 0)
                {
                    Parent = currentNode
                }; // Offset 0 for virtual nodes for now
                currentNode.Children.Add(foundChild);
            }

            currentNode = foundChild;
        }

        fileRecord = fileRecord with { FileName = parts[^1] };
        currentNode.Value = fileRecord;
    }

    private readonly struct PooledBuffer(int size) : IDisposable
    {
        public readonly byte[] Data = ArrayPool<byte>.Shared.Rent(size);

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(Data);
        }
    }
}