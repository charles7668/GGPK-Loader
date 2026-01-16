namespace GGPK_Loader.Models;

internal record GGPKFreeInfo(
    uint Length,
    byte[] Tag, // = "FREE"
    ulong NextOffset,
    ulong DataOffset,
    ulong DataLength
);