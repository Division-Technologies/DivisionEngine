using DivisionEngine.Authoring.Assets;

namespace DivisionEngine.Tests.Assets;

/// <summary>Test importer: turns a text file's contents into an <see cref="AssetNode" />.</summary>
[AssetImporter(".dntxt")]
[AutoSerialization]
public partial class TextNodeImporter : IAssetImporter
{
    /// <summary>Number of times <see cref="Import" /> has run, across all instances. Reset in tests.</summary>
    public static int ImportCount;

    [Serialize] public bool Upper;

    public void Import(AssetImportContext context)
    {
        Interlocked.Increment(ref ImportCount);
        var text = File.ReadAllText(context.SourcePath);
        var node = new AssetNode { Name = Upper ? text.ToUpperInvariant() : text };
        context.SetMainObject(node);
    }
}

/// <summary>
///     Test importer whose source file contains the GUID (in "N" format) of an asset it depends on.
/// </summary>
[AssetImporter(".dndep")]
[AutoSerialization]
public partial class DependentImporter : IAssetImporter
{
    public static int ImportCount;

    public void Import(AssetImportContext context)
    {
        Interlocked.Increment(ref ImportCount);
        var text = File.ReadAllText(context.SourcePath).Trim();
        if (Guid.TryParse(text, out var dependency))
        {
            context.DependsOnAsset(new ScopeId(dependency));
        }

        context.SetMainObject(new AssetNode { Name = text });
    }
}