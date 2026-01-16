namespace GGPK_Loader.Models;

internal record GGPKDirInfo(
    uint Length,
    byte[] Tag,
    uint NameLength,
    uint TotalEntries,
    byte[] SHA256Hash,
    string Name,
    GGPKDirectoryEntry[] DirectoryEntries)
{
    public override string ToString()
    {
        return Name;
    }
}