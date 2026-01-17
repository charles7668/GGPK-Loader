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
    public ulong HeaderSize => 4 + 4 + 4 + 32 + FileNameLength * 2;

    public ulong HashOffset => (ulong)(DataOffset - FileNameLength * 2 - 32);

    public override string ToString()
    {
        return FileName;
    }
}