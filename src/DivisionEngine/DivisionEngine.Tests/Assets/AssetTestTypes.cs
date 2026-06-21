using DivisionEngine;

namespace DivisionEngine.Tests.Assets;

/// <summary>A minimal scope-managed object with a (possibly cross-scope) reference, for asset tests.</summary>
[AutoSerialization]
public partial class AssetNode : ISerializableObject
{
    [Serialize] public string Name = "";
    [field: Serialize] public AssetNode? Ref { get; set; }

    public SerializationScope Scope { get; set; } = null!;
    public LocalId Id { get; set; }
}
