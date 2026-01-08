using JetBrains.Annotations;

namespace GGPK_Loader.Models;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public record BundleIndexInfo
{
    public uint BundleCount { get; init; }
    public BundleRecord[] Bundles { get; init; } = [];
    public uint FileCount { get; init; }
    public FileRecord[] Files { get; init; } = [];
    public uint PathRepCount { get; init; }
    public PathRepRecord[] PathReps { get; init; } = [];
    public byte[] PathRepBundle { get; init; } = [];

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public record BundleRecord(uint NameLength, string Name, uint UncompressedSize);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public record FileRecord(ulong Hash, uint BundleIndex, uint FileOffset, uint FileSize);

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public record PathRepRecord(ulong Hash, uint PayloadOffset, uint PayloadSize, uint PayloadRecursiveSize);
}