using DivisionEngine.Authoring.Assets;

namespace DivisionEngine.Tests.Scripting;

/// <summary>
///     Test importer that produces a <see cref="CompiledAssembly" /> (as the real C# project importer
///     does) without invoking MSBuild, so script-tracking can be tested deterministically.
/// </summary>
[AssetImporter(".dnscript")]
public sealed class FakeScriptImporter : IAssetImporter
{
    public static string DllPath = "";

    public void Import(AssetImportContext context)
    {
        context.SetMainObject(new CompiledAssembly { Success = true, AssemblyName = "Fake", DllPath = DllPath });
    }

    public void Serialize<T>(ref T serializer) where T : ISerializer, allows ref struct
    {
    }

    public void Deserialize<T>(ref T deserializer) where T : IDeserializer, allows ref struct
    {
    }
}