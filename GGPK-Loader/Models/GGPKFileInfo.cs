using JetBrains.Annotations;
using System.Diagnostics.CodeAnalysis;

namespace GGPK_Loader.Models;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public struct GGPKFileInfo
{
    public uint Length;
    public byte[] Tag; // ="FILE"
    public uint FileNameLength;
    public byte[] SHA256Hash;
    public string FileName;
    public long DataOffset;
    public ulong DataSize;
}