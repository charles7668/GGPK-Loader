using JetBrains.Annotations;

namespace GGPK_Loader.Models;

public enum BundleEncodeType : uint
{
    Kraken6 = 8,
    MermaidA = 9,
    LeviathanC = 13
}

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public struct BundleInfo
{
    public uint UncompressedSize;
    public uint TotalPayloadSize;
    public uint HeadPayloadSize;
    public HeadPayloadT Head;

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public struct HeadPayloadT
    {
        public BundleEncodeType FirstFileEncode;
        public uint Unk10;
        public ulong UncompressedSize2;
        public ulong TotalPayloadSize2;
        public uint BlockCount;
        public uint UncompressedBlockGranularity;
        public uint[] Unk28;
        public uint[] BlockSizes;
    }
}