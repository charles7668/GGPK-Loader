namespace GGPK_Loader.Models;

internal struct GGPKRoot
{
    public uint Length;

    public char[] Tag; // ="GGPK"

    public uint Version;

    /// <summary>
    /// length is 2
    /// </summary>
    public GGPKEntry[] Entries;
}