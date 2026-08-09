namespace DivisionEngine.Authoring.Assets;

/// <summary>
///     Marks an <see cref="IAssetImporter" /> implementation as the importer for one or more file
///     extensions. Discovered by <see cref="AssetImporterRegistry" /> via assembly scanning.
///     Extensions may be written with or without a leading dot (e.g. "png" or ".png").
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class AssetImporterAttribute(params string[] extensions) : Attribute
{
    public string[] Extensions { get; } = extensions;
}