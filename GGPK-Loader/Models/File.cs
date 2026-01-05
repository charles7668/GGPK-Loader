namespace GGPK_Loader.Models;

internal struct File
{
    public uint Length;
    public byte[] Tag; // ="FILE"
    public uint NameLength;
    public byte[] SHA256Hash;
    public char[] Name;
    public byte[] Data;
}