namespace GGPK_Loader.Models;

internal struct Free
{
    public uint Length;
    public byte[] Tag; // = "FREE"
    public byte[] Data;
}