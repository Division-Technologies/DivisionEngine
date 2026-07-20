using DivisionEngine.Authoring.Assets;

namespace DivisionEngine.Tests.Assets;

/// <summary>
///     Test importer whose source file (.dndf) contains the path of a companion data file it reads.
///     It declares that file as an input via <see cref="AssetImportContext.DependsOnFile" />, so a
///     change to the data file must re-trigger this import.
/// </summary>
[AssetImporter(".dndf")]
public sealed class FileDependentImporter : IAssetImporter
{
    public void Import(AssetImportContext context)
    {
        var dataPath = File.ReadAllText(context.SourcePath).Trim();
        context.DependsOnFile(dataPath);
        var value = File.Exists(dataPath) ? File.ReadAllText(dataPath).Trim() : "";
        context.SetMainObject(new AssetNode { Name = value });
    }

    public void Serialize<T>(ref T serializer) where T : ISerializer, allows ref struct
    {
    }

    public void Deserialize<T>(ref T deserializer) where T : IDeserializer, allows ref struct
    {
    }
}