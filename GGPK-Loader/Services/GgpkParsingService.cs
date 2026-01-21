using System;
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

namespace GGPK_Loader.Services;

public class GgpkParsingService(IStreamManager streamManager) : IGgpkParsingService
{
    private readonly IStreamManager _streamManager = streamManager;

    public void OpenStream(string filePath)
    {
        _streamManager.Open(filePath);
    }

    public void CloseStream()
    {
        _streamManager.Close();
    }

    public async Task<GGPKTreeNode> BuildGgpkTreeAsync()
    {
        return await Task.Run(() =>
        {
            var stream = _streamManager.Stream!;
            stream.Seek(0, SeekOrigin.Begin);

            var ggpkHeader = ReadGGPKHeader(stream);
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
                        HandleFileNode(currentNode, currentOffset, stream);
                        break;
                    case "PDIR":
                        HandlePdirNode(currentNode, currentOffset, entryQueue, offsetQueue, stream);
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


    public async Task ReplaceFileDataAsync(ReadOnlyMemory<byte> replaceSource, GGPKTreeNode targetNode,
        CancellationToken ct)
    {
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

        if (targetFileInfo.DataSize - GGPKFreeInfo.HeaderSize >= (ulong)replaceSource.Length)
        {
            await ReplaceDataWithSmallSize(replaceSource, targetNode, targetFileInfo, ct);
            return;
        }

        await ReplaceDataWithLargeSize(replaceSource, targetNode, targetFileInfo, ct);
    }

    public Task<byte[]> LoadGGPKFileDataAsync(GGPKFileInfo ggpkFileInfo, ulong size, CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            var buffer = new byte[size];
            await _streamManager.ReadAtAsync(buffer, ggpkFileInfo.DataOffset, ct);

            return buffer;
        }, ct);
    }

    public Task<byte[]> LoadGGPKFileDataAsync(GGPKFileInfo ggpkFileInfo, CancellationToken ct)
    {
        return LoadGGPKFileDataAsync(ggpkFileInfo, ggpkFileInfo.DataSize, ct);
    }

    private async Task ReplaceDataWithLargeSize(ReadOnlyMemory<byte> replaceSource, GGPKTreeNode targetNode,
        GGPKFileInfo targetFileInfo, CancellationToken ct)
    {
        var requiredSize = (uint)(targetFileInfo.HeaderSize + (ulong)replaceSource.Length);
        // First, attempt to find a free block matching the exact required size. If unavailable, search for a block that also accommodates the free info header.
        var targetFreeInfo = FindFreeSpace(requiredSize, FreeSpaceSearchMode.Equal) ??
                             FindFreeSpace(requiredSize + GGPKFreeInfo.HeaderSize);
        var lastFreeInfo = FindLastFreeSpace();

        await _streamManager.ExecuteWriteTransactionAsync(async (fs, _) =>
        {
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
        }, ct);
    }

    private async Task ReplaceDataWithSmallSize(ReadOnlyMemory<byte> replaceSource, GGPKTreeNode targetNode,
        GGPKFileInfo targetFileInfo, CancellationToken ct)
    {
        var lastFreeInfo = FindLastFreeSpace()!;

        await _streamManager.ExecuteWriteTransactionAsync(async (fs, _) =>
        {
            // Replace content
            await RandomAccess.WriteAsync(fs.SafeFileHandle, replaceSource, targetFileInfo.DataOffset, ct);

            // Change hash
            var newHash = SHA256.HashData(replaceSource.Span);
            var hashOffset = targetFileInfo.HashOffset;
            await RandomAccess.WriteAsync(fs.SafeFileHandle, newHash, (long)hashOffset, ct);

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
                    SHA256.HashData(childHashes.SelectMany(x => x).ToArray());
                // Length(4) + Tag(4) + NameLength(4) + TotalEntries(4) = 16
                var parentHashOffset = parentNode.Offset + 16;
                await RandomAccess.WriteAsync(fs.SafeFileHandle, parentNewHash, (long)parentHashOffset, ct);

                parentNode.Value = (GGPKDirInfo)parentNode.Value with { SHA256Hash = parentNewHash };

                if (parentNode.Parent?.Value is GGPKDirInfo)
                {
                    parentsQueue.Enqueue(parentNode.Parent);
                }
            }
        }, ct);
    }

    private GGPKFreeInfo? FindFreeSpace(ulong requiredSize,
        FreeSpaceSearchMode mode = FreeSpaceSearchMode.GreaterOrEqual)
    {
        var stream = _streamManager.Stream!;
        var ggpkHeader = ReadGGPKHeader(stream);
        GGPKFreeInfo? previousFreeInfo = null;
        var nextOffset = ggpkHeader.Entries[1].Offset;

        while (nextOffset > 0)
        {
            var freeInfo = ReadGGPKFreeInfo(nextOffset, previousFreeInfo, stream);
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
        var stream = _streamManager.Stream!;
        var ggpkHeader = ReadGGPKHeader(stream);
        GGPKFreeInfo? previousFreeInfo = null;
        var nextOffset = ggpkHeader.Entries[1].Offset;

        while (nextOffset > 0)
        {
            var freeInfo = ReadGGPKFreeInfo(nextOffset, previousFreeInfo, stream);
            previousFreeInfo = freeInfo;
            nextOffset = freeInfo.NextOffset;
        }

        return previousFreeInfo;
    }

    private async Task ReplaceDataWithSameSize(ReadOnlyMemory<byte> replaceSource, GGPKTreeNode targetNode,
        GGPKFileInfo targetFileInfo, CancellationToken ct)
    {
        await _streamManager.ExecuteWriteTransactionAsync(async (fs, _) =>
        {
            await RandomAccess.WriteAsync(fs.SafeFileHandle, replaceSource, targetFileInfo.DataOffset, ct);

            var newHash = SHA256.HashData(replaceSource.Span);

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
                    SHA256.HashData(childHashes.SelectMany(x => x).ToArray());
                // Length(4) + Tag(4) + NameLength(4) + TotalEntries(4) = 16
                var parentHashOffset = parentNode.Offset + 16;
                await RandomAccess.WriteAsync(fs.SafeFileHandle, parentNewHash, (long)parentHashOffset, ct);

                parentNode.Value = (GGPKDirInfo)parentNode.Value with { SHA256Hash = parentNewHash };

                if (parentNode.Parent?.Value is GGPKDirInfo)
                {
                    parentsQueue.Enqueue(parentNode.Parent);
                }
            }
        }, ct);
    }

    private static GGPKHeader ReadGGPKHeader(Stream stream)
    {
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

    private void HandleFileNode(GGPKTreeNode currentNode, ulong currentOffset, Stream stream)
    {
        var fileInfo = ReadGGPKFileInfo(currentOffset, stream);

        var fileNode = new GGPKTreeNode(fileInfo, currentOffset)
        {
            Parent = currentNode
        };
        currentNode.Children.Add(fileNode);
    }

    private void HandlePdirNode(GGPKTreeNode currentNode,
        ulong pdirOffset,
        Queue<GGPKTreeNode> entryQueue, Queue<ulong> offsetQueue, Stream stream)
    {
        var dirInfo = ReadGGPKDirInfo(pdirOffset, stream);
        var nextNode = new GGPKTreeNode(dirInfo, pdirOffset)
        {
            Parent = currentNode
        };
        currentNode.Children.Add(nextNode);

        for (var i = 0; i < dirInfo.TotalEntries; i++)
        {
            offsetQueue.Enqueue(dirInfo.DirectoryEntries[i].Offset);
            entryQueue.Enqueue(nextNode);
        }
    }

    private static GGPKFileInfo ReadGGPKFileInfo(ulong? offset, Stream stream)
    {
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

    private static GGPKFreeInfo ReadGGPKFreeInfo(ulong? offset, GGPKFreeInfo? previousFreeInfo, Stream stream)
    {
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

    private static GGPKDirInfo ReadGGPKDirInfo(ulong? offset, Stream stream)
    {
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

    private enum FreeSpaceSearchMode
    {
        GreaterOrEqual,
        Equal
    }
}