using DivisionEngine.Authoring.Assets;

namespace DivisionEngine.Tests.Assets;

/// <summary>Test importer that always throws, to verify a failing import is logged and skipped.</summary>
[AssetImporter(".dnthrow")]
public sealed class ThrowingImporter : IAssetImporter
{
    public void Import(AssetImportContext context)
    {
        throw new InvalidOperationException("intentional import failure");
    }

    public void Serialize<T>(ref T serializer) where T : ISerializer, allows ref struct
    {
    }

    public void Deserialize<T>(ref T deserializer) where T : IDeserializer, allows ref struct
    {
    }
}