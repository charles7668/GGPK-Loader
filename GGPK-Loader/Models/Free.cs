namespace GGPK_Loader.Models;

internal struct Free
{
    public uint Length;
    public char[] Tag; // = "FREE"
    public byte[] Data;
}