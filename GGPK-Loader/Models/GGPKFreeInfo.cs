namespace GGPK_Loader.Models;

internal record GGPKFreeInfo(
    uint Length,
    byte[] Tag, // = "FREE"
    ulong NextOffset,
    ulong DataOffset,
    ulong DataLength,
    GGPKFreeInfo? PreviousFreeInfo
)
{
    // Lenght(4) + Tag(4) + NextOffset(8)
    public const ulong HeaderSize = 16;
};