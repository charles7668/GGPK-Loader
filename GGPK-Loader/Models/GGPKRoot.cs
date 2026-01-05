namespace GGPK_Loader.Models;

internal struct GGPKRoot
{
    public uint Length;

    public uint Version;

    public char[] Tag;

    public GGPKEntry[] Entries;
}