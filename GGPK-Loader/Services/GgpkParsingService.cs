using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GGPK_Loader.Models;
using GGPK_Loader.Utils;
using JetBrains.Annotations;

namespace GGPK_Loader.Services;

public class GgpkParsingService : IGgpkParsingService
{
    [UsedImplicitly]
    private string? _ggpkFilePath;

    private Stream? _ggpkStream;
    private readonly SemaphoreSlim _streamSemaphore = new(1, 1);

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

    private void ThrowIfStreamNotOpen()
    {
        if (_ggpkStream is { CanRead: true })
        {
            return;
        }

        throw new InvalidOperationException("GGPK stream is not open");
    }

    public async Task<GGPKTreeNode> BuildGgpkTreeAsync()
    {
        ThrowIfStreamNotOpen();
        return await Task.Run(() =>
        {
            var stream = _ggpkStream!;
            stream.Seek(0, SeekOrigin.Begin);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            var ggpkHeader = ReadGGPKHeader(reader);
            var entryQueue = new Queue<GGPKTreeNode>();
            var offsetQueue = new Queue<ulong>();
            var rootNodeInternal = new GGPKTreeNode("", 0);

            entryQueue.Enqueue(rootNodeInternal);
            offsetQueue.Enqueue(ggpkHeader.Entries[0].Offset);

            while (entryQueue.Count > 0)
            {
                var currentNode = entryQueue.Dequeue();
                var currentOffset = offsetQueue.Dequeue();
                stream.Seek((long)currentOffset, SeekOrigin.Begin);

                _ = reader.ReadUInt32(); // entry length
                var entryTag = new string(reader.ReadChars(4));

                switch (entryTag)
                {
                    case "FILE":
                        HandleFileNode(reader, currentNode, currentOffset);
                        break;
                    case "PDIR":
                        HandlePdirNode(reader, currentNode, entryQueue, offsetQueue);
                        break;
                    case "FREE":
                        break;
                    default:
                        Debug.WriteLine($"Unknown Tag: {entryTag} at {currentOffset:X}");
                        break;
                }
            }

            return rootNodeInternal.Children.Count > 0 ? rootNodeInternal.Children[0] : rootNodeInternal;
        });
    }

    public async Task<GGPKTreeNode?> BuildBundleTreeAsync(GGPKTreeNode ggpkRootNode, string ggpkFilePath)
    {
        return await Task.Run(() =>
        {
            var bundleRootNode =
                ggpkRootNode.Children.FirstOrDefault(child =>
                {
                    if (child.Value is string childStr)
                        return childStr == "Bundles2";
                    return false;
                });

            return bundleRootNode == null ? null : ProcessBundle(bundleRootNode, ggpkFilePath);
        });
    }

    public async Task<byte[]> LoadBundleFileDataAsync(GGPKFileInfo ggpkBundleFileInfo,
        BundleIndexInfo.FileRecord bundleFileRecord, CancellationToken ct)
    {
        ThrowIfStreamNotOpen();

        var stream = _ggpkStream!;
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent((int)ggpkBundleFileInfo.DataSize);

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
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public Task<byte[]> LoadGGPKFileDataAsync(GGPKFileInfo ggpkFileInfo, CancellationToken ct)
    {
        ThrowIfStreamNotOpen();

        var stream = _ggpkStream!;

        return Task.Run(async () =>
        {
            var buffer = new byte[ggpkFileInfo.DataSize];
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


    private static GGPKHeader ReadGGPKHeader(BinaryReader reader)
    {
        var length = reader.ReadUInt32();
        var tag = reader.ReadChars(4);
        var version = reader.ReadUInt32();
        var offset = reader.ReadUInt64();
        var offset2 = reader.ReadUInt64();

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

    private static void HandleFileNode(BinaryReader reader, GGPKTreeNode currentNode, ulong currentOffset)
    {
        var fileInfo = ReadGGPKFileInfo(reader.BaseStream, reader, currentOffset);

        var fileNode = new GGPKTreeNode(fileInfo, currentOffset);
        currentNode.Children.Add(fileNode);

        Debug.WriteLine($"FILE Name: {fileInfo.FileName}, Size: {fileInfo.DataSize}");
    }

    private static void HandlePdirNode(BinaryReader reader, GGPKTreeNode currentNode,
        Queue<GGPKTreeNode> entryQueue, Queue<ulong> offsetQueue)
    {
        var nameLength = reader.ReadUInt32();
        var totalEntries = reader.ReadUInt32();
        reader.ReadBytes(32); // sha256
        var nameBytes = reader.ReadBytes((int)nameLength * 2);
        var name = Encoding.Unicode.GetString(nameBytes).TrimEnd('\0');

        var nextNode = new GGPKTreeNode(name, currentNode.Offset);
        currentNode.Children.Add(nextNode);
        Debug.WriteLine($"PDIR Name: {nextNode.Value}");

        for (var i = 0; i < totalEntries; i++)
        {
            reader.ReadInt32(); // entry name hash
            var entryOffset = reader.ReadUInt64();

            offsetQueue.Enqueue(entryOffset);
            entryQueue.Enqueue(nextNode);
        }
    }

    private static GGPKTreeNode? ProcessBundle(GGPKTreeNode bundleRootNode, string ggpkFilePath)
    {
        var indexBinNode = GetIndexBinNode(bundleRootNode);
        if (indexBinNode == null)
        {
            return null;
        }

        Debug.WriteLine($"Found index.bin: {indexBinNode.Value}");

        using var ggpkStream = File.OpenRead(ggpkFilePath);
        using var reader = new BinaryReader(ggpkStream);
        var fileInfo = ReadGGPKFileInfo(ggpkStream, reader, indexBinNode.Offset);
        ggpkStream.Seek(fileInfo.DataOffset, SeekOrigin.Begin);
        var data = reader.ReadBytes((int)fileInfo.DataSize);

        var dataSpan = new ReadOnlySpan<byte>(data);
        var bundleInfo = ReadBundleInfo(ref dataSpan);

        var decompressed = new byte[bundleInfo.UncompressedSize];
        var decompressedOffset = DecompressBlocks(bundleInfo, ref dataSpan, decompressed);

        if (decompressedOffset != bundleInfo.UncompressedSize)
        {
            return null;
        }

        var bundleIndexInfo = ReadBundleIndex(new ReadOnlySpan<byte>(decompressed));

        var pathRepSpan = new ReadOnlySpan<byte>(bundleIndexInfo.PathRepBundle);
        var tempPathRepSpan = pathRepSpan;
        var pathRepBundleInfo = ReadBundleInfo(ref tempPathRepSpan);
        var decompressedDirectory = new byte[pathRepBundleInfo.UncompressedSize];

        if (DecompressOodleBundle(pathRepSpan, decompressedDirectory) != pathRepBundleInfo.UncompressedSize)
        {
            return null;
        }

        var newBundleRootNode = new GGPKTreeNode("/", 0);
        ParsePaths(bundleIndexInfo.PathReps, bundleIndexInfo.Files, decompressedDirectory, newBundleRootNode);
        newBundleRootNode.Value = bundleIndexInfo;
        return newBundleRootNode;
    }

    private static GGPKFileInfo ReadGGPKFileInfo(Stream stream, BinaryReader reader, ulong? offset)
    {
        if (offset != null)
        {
            stream.Seek((long)offset, SeekOrigin.Begin);
        }

        var entryLength = reader.ReadUInt32();
        var entryTag = reader.ReadBytes(4);
        var fileNameLength = reader.ReadUInt32();
        var sha256Hash = reader.ReadBytes(32);
        var fileName = Encoding.Unicode.GetString(reader.ReadBytes((int)(fileNameLength * 2))).TrimEnd('\0');

        var headerSize = 4 + 4 + 4 + 32 + fileNameLength * 2;
        var dataOffset = stream.Position;
        var dataSize = entryLength - headerSize;
        stream.Seek(dataSize, SeekOrigin.Current); // Skip data
        return new GGPKFileInfo(
            entryLength,
            entryTag,
            fileNameLength,
            sha256Hash,
            fileName,
            dataOffset,
            dataSize
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
                    if (fileRecordsDict.TryGetValue(MurmurHash64A(str.Select(x => (byte)x).ToArray()),
                            out var fileRecord))
                    {
                        AddFileToBundleTree(rootNode, str, ref fileRecord);
                    }
                }
            }
        }
    }

    private static ulong MurmurHash64A(ReadOnlySpan<byte> utf8Name, ulong seed = 0x1337B33F)
    {
        if (utf8Name.IsEmpty)
            return 0xF42A94E69CFF42FEul;

        if (utf8Name[^1] == '/')
            utf8Name = utf8Name[..^1];

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
                foundChild = new GGPKTreeNode(part, 0); // Offset 0 for virtual nodes for now
                currentNode.Children.Add(foundChild);
            }

            currentNode = foundChild;
        }

        fileRecord = fileRecord with { FileName = parts[^1] };
        currentNode.Value = fileRecord;
    }
}