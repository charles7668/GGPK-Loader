namespace GGPK_Loader.Models;

internal record GGPKDirectoryEntry(
    // Hash is calculated by Murmur2
    // seed is 0
    // entry name is encoded by Unicode
    int EntryNameHash,
    ulong Offset);