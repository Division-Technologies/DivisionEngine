namespace DivisionEngine.Authoring.Assets;

/// <summary>
///     Fallback importer for file extensions no other importer handles: it imports the file as raw
///     bytes into a <see cref="BinaryAsset" />. It is not registered for any extension (no
///     <see cref="AssetImporterAttribute" />); the <see cref="AssetDatabase" /> selects it when no
///     specific importer and no direct-asset <c>.meta</c> apply.
/// </summary>
public sealed class RawBinaryImporter : IAssetImporter
{
    public static readonly RawBinaryImporter Instance = new();

    public void Import(AssetImportContext context)
    {
        context.SetMainObject(new BinaryAsset
        {
            Data = context.ReadAllBytes(),
            Extension = Path.GetExtension(context.SourcePath)
        });
    }

    // Stateless: no persisted settings (no [AutoSerialization]).
    public void Serialize<T>(ref T serializer) where T : ISerializer, allows ref struct
    {
    }

    public void Deserialize<T>(ref T deserializer) where T : IDeserializer, allows ref struct
    {
    }
}