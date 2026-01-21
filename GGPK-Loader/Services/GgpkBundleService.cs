using System;
using System.Buffers;
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

namespace GGPK_Loader.Services;

public class GgpkBundleService(IStreamManager streamManager, IGgpkParsingService ggpkParsingService)
    : IGgpkBundleService
{
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
        var buffer = ArrayPool<byte>.Shared.Rent((int)ggpkBundleFileInfo.DataSize);

        try
        {
            await streamManager.ReadAtAsync(buffer.AsMemory(0, (int)ggpkBundleFileInfo.DataSize),
                ggpkBundleFileInfo.DataOffset, ct);

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

    public async Task<byte[]> ReplaceBundleFileContentAsync(GGPKFileInfo ggpkBundleFileInfo,
        BundleIndexInfo.FileRecord bundleFileRecord, ReadOnlyMemory<byte> newContent, CancellationToken ct)
    {
        using var buffer = new PooledBuffer((int)ggpkBundleFileInfo.DataSize);

        await streamManager.ReadAtAsync(buffer.Data.AsMemory(0, (int)ggpkBundleFileInfo.DataSize),
            ggpkBundleFileInfo.DataOffset, ct);

        var dataSpan = new ReadOnlySpan<byte>(buffer.Data, 0, (int)ggpkBundleFileInfo.DataSize);
        var processingSpan = dataSpan;
        var bundleInfo = ReadBundleInfo(ref processingSpan);

        var uncompressedSize = bundleInfo.UncompressedSize;
        var uncompressedBuffer = GC.AllocateUninitializedArray<byte>((int)uncompressedSize);

        // Decompress all blocks
        DecompressBlocks(bundleInfo, ref processingSpan, uncompressedBuffer, 0, bundleInfo.Head.BlockCount);

        if (bundleFileRecord.FileOffset + bundleFileRecord.FileSize > uncompressedSize)
        {
            throw new InvalidDataException("File record out of bounds");
        }

        // Create new uncompressed buffer
        var sizeDiff = newContent.Length - (int)bundleFileRecord.FileSize;
        var newUncompressedSize = uncompressedSize + sizeDiff;
        var newUncompressedBuffer = new byte[newUncompressedSize];

        // Copy data before modified file
        Array.Copy(uncompressedBuffer, 0, newUncompressedBuffer, 0, (int)bundleFileRecord.FileOffset);

        // Copy new content
        newContent.Span.CopyTo(newUncompressedBuffer.AsSpan((int)bundleFileRecord.FileOffset));

        // Copy data after modified file
        var afterOffset = (int)(bundleFileRecord.FileOffset + bundleFileRecord.FileSize);
        var afterLength = (int)uncompressedSize - afterOffset;
        if (afterLength > 0)
        {
            Array.Copy(uncompressedBuffer, afterOffset, newUncompressedBuffer,
                (int)bundleFileRecord.FileOffset + newContent.Length, afterLength);
        }

        // Recompress
        var granularity = (int)bundleInfo.Head.UncompressedBlockGranularity;
        var compressedBlocks = new List<byte[]>();
        var blockSizes = new List<uint>();
        var totalPayloadSize = 0;

        var sourceSpan = new ReadOnlySpan<byte>(newUncompressedBuffer);
        var codec = (int)bundleInfo.Head.FirstFileEncode;

        for (var i = 0; i < newUncompressedBuffer.Length; i += granularity)
        {
            var chunkSize = Math.Min(granularity, newUncompressedBuffer.Length - i);
            var chunk = sourceSpan.Slice(i, chunkSize).ToArray();
            var destBuffer = new byte[chunkSize + 65536];

            var compressedSize = Oo2Core.Compress(codec, chunk, chunkSize, destBuffer, 4);
            if (compressedSize <= 0)
            {
                throw new InvalidOperationException($"Compression failed for block {i / granularity}");
            }

            var finalCompressed = new byte[compressedSize];
            Array.Copy(destBuffer, finalCompressed, compressedSize);
            compressedBlocks.Add(finalCompressed);
            blockSizes.Add((uint)compressedSize);
            totalPayloadSize += (int)compressedSize;
        }

        // Construct new bundle
        using var ms = new MemoryStream();
        await using var writer = new BinaryWriter(ms);

        writer.Write((uint)newUncompressedSize);
        writer.Write((uint)totalPayloadSize);
        writer.Write((uint)(compressedBlocks.Count * 4 + 48)); // HeadPayloadSize

        writer.Write((uint)bundleInfo.Head.FirstFileEncode);
        writer.Write(bundleInfo.Head.Unk10);
        writer.Write((ulong)newUncompressedSize);
        writer.Write((ulong)totalPayloadSize);
        writer.Write((uint)compressedBlocks.Count);
        writer.Write(bundleInfo.Head.UncompressedBlockGranularity);

        foreach (var u in bundleInfo.Head.Unk28)
        {
            writer.Write(u);
        }

        foreach (var s in blockSizes)
        {
            writer.Write(s);
        }

        foreach (var b in compressedBlocks)
        {
            writer.Write(b);
        }

        return ms.ToArray();
    }

    public async Task UpdateBundleIndexAsync(BundleIndexInfo bundleIndexInfo, GGPKTreeNode indexNode,
        CancellationToken ct)
    {
        var uncompressedData = SerializeBundleIndex(bundleIndexInfo);

        // Create Bundle logic
        const uint granularity = 0x40000;
        var compressedBlocks = new List<byte[]>();
        var blockSizes = new List<uint>();
        var totalPayloadSize = 0;

        var sourceSpan = new ReadOnlySpan<byte>(uncompressedData);
        var codec = 9; // Mermaid

        for (var i = 0; i < uncompressedData.Length; i += (int)granularity)
        {
            var chunkSize = Math.Min((int)granularity, uncompressedData.Length - i);
            var chunk = sourceSpan.Slice(i, chunkSize).ToArray();
            var destBuffer = new byte[chunkSize + 65536];

            var compressedSize = Oo2Core.Compress(codec, chunk, chunkSize, destBuffer, 4);
            if (compressedSize <= 0)
            {
                throw new InvalidOperationException("Compression failed for index bundle");
            }

            var finalCompressed = new byte[compressedSize];
            Array.Copy(destBuffer, finalCompressed, compressedSize);
            compressedBlocks.Add(finalCompressed);
            blockSizes.Add((uint)compressedSize);
            totalPayloadSize += (int)compressedSize;
        }

        using var ms = new MemoryStream();
        await using var writer = new BinaryWriter(ms);

        writer.Write((uint)uncompressedData.Length);
        writer.Write((uint)totalPayloadSize);
        writer.Write((uint)(compressedBlocks.Count * 4 + 48)); // HeadPayloadSize

        writer.Write((uint)codec);
        writer.Write(0u); // Unk10
        writer.Write((ulong)uncompressedData.Length);
        writer.Write((ulong)totalPayloadSize);
        writer.Write((uint)compressedBlocks.Count);
        writer.Write(granularity);

        writer.Write(0u); // Unk28 * 4
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);

        foreach (var s in blockSizes)
        {
            writer.Write(s);
        }

        foreach (var b in compressedBlocks)
        {
            writer.Write(b);
        }

        var newIndexBundleData = ms.ToArray();

        await ggpkParsingService.ReplaceFileDataAsync(newIndexBundleData, indexNode, ct);
    }

    private async Task<GGPKTreeNode?> ProcessBundleAsync(GGPKTreeNode bundleRootNode)
    {
        var indexBinNode = GetIndexBinNode(bundleRootNode);
        if (indexBinNode?.Value is not GGPKFileInfo indexFileInfo)
        {
            return null;
        }

        Debug.WriteLine($"Found index.bin: {indexBinNode.Value}");

        using var buffer = new PooledBuffer((int)indexFileInfo.DataSize);

        await streamManager.ReadAtAsync(buffer.Data.AsMemory(0, (int)indexFileInfo.DataSize),
            indexFileInfo.DataOffset);

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
                if (fileRecordsDict.TryGetValue(MurmurHash.MurmurHash64A(Encoding.ASCII.GetBytes(str)),
                        out var fileRecord))
                {
                    AddFileToBundleTree(rootNode, str, ref fileRecord);
                }
            }
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

    private static byte[] SerializeBundleIndex(BundleIndexInfo info)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(info.BundleCount);
        foreach (var bundle in info.Bundles)
        {
            writer.Write(bundle.NameLength);
            var nameBytes = Encoding.UTF8.GetBytes(bundle.Name);
            writer.Write(nameBytes);
            writer.Write(bundle.UncompressedSize);
        }

        writer.Write(info.FileCount);
        foreach (var file in info.Files)
        {
            writer.Write(file.Hash);
            writer.Write(file.BundleIndex);
            writer.Write(file.FileOffset);
            writer.Write(file.FileSize);
        }

        writer.Write(info.PathRepCount);
        foreach (var rep in info.PathReps)
        {
            writer.Write(rep.Hash);
            writer.Write(rep.PayloadOffset);
            writer.Write(rep.PayloadSize);
            writer.Write(rep.PayloadRecursiveSize);
        }

        writer.Write(info.PathRepBundle);

        return ms.ToArray();
    }
}