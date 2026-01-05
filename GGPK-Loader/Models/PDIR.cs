namespace GGPK_Loader.Models;

internal struct PDIR
{
    public uint Length;
    public char[] Tag; // ="PDIR"
    public uint NameLength;
    public uint TotalEntries;
    public byte[] SHA256Hash;
    public string[] Name;
    public DirectoryEntry[] DirectoryEntries;
}