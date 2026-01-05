namespace GGPK_Loader.Models;

internal struct File
{
    public uint Length;
    public char[] Tag; // ="FILE"
    public uint NameLength;
    public byte[] SHA256Hash;
    public string[] Name;
    public byte[] Data;
}