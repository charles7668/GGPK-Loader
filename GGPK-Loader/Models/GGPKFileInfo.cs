using JetBrains.Annotations;

namespace GGPK_Loader.Models;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public record GGPKFileInfo(
    uint Length,
    byte[] Tag,
    uint FileNameLength,
    byte[] SHA256Hash,
    string FileName,
    long DataOffset,
    ulong DataSize
)
{
    public override string ToString()
    {
        return FileName;
    }
}