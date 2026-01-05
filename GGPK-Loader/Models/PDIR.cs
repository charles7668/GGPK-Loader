namespace GGPK_Loader.Models;

internal struct PDIR
{
    public uint Length;
    public byte[] Tag; // ="PDIR"
    public uint NameLength;
    public uint TotalEntries;
    public byte[] SHA256Hash;
    public char[] Name;
    public DirectoryEntry[] DirectoryEntries;
}