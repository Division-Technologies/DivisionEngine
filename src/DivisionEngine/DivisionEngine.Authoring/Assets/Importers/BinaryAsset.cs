namespace DivisionEngine.Authoring.Assets;

/// <summary>
///     A raw imported asset: the source file's bytes, produced by <see cref="RawBinaryImporter" /> for
///     file types without a dedicated importer. Consumers read <see cref="Data" /> directly.
/// </summary>
[AutoSerialization]
public sealed partial class BinaryAsset : ISerializableObject
{
    [Serialize] public byte[] Data = [];

    /// <summary>The source file's extension (e.g. ".bin"), for consumers that dispatch on type.</summary>
    [Serialize] public string Extension = "";

    public SerializationScope Scope { get; set; } = null!;
    public LocalId Id { get; set; }
}