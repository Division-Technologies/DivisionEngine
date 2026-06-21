using DivisionEngine;

namespace DivisionEngine.Authoring.Assets;

/// <summary>
///     Passed to an <see cref="IAssetImporter" /> during import. Importers read the source file and
///     emit the runtime objects that make up the asset, declaring one of them as the main object and
///     recording any dependencies on other assets.
/// </summary>
public sealed class AssetImportContext
{
    private readonly List<ScopeId> _dependencies = new();
    private readonly SerializationScope _scope;

    internal AssetImportContext(string sourcePath, SerializationScope scope)
    {
        SourcePath = sourcePath;
        _scope = scope;
    }

    /// <summary>Absolute path of the source file being imported.</summary>
    public string SourcePath { get; }

    /// <summary>The object returned to callers that load this asset. Set via <see cref="SetMainObject" />.</summary>
    public ISerializableObject? MainObject { get; private set; }

    /// <summary>Assets this import depends on; a change to any of them re-triggers this import.</summary>
    public IReadOnlyList<ScopeId> Dependencies => _dependencies;

    /// <summary>Opens the source file for reading.</summary>
    public Stream OpenSource() => File.OpenRead(SourcePath);

    /// <summary>Reads the entire source file.</summary>
    public byte[] ReadAllBytes() => File.ReadAllBytes(SourcePath);

    /// <summary>Adds a produced object to the asset, returning its assigned id.</summary>
    public LocalId AddObject(ISerializableObject obj) => _scope.Add(obj);

    /// <summary>Declares the asset's main object, adding it first if it has not been added yet.</summary>
    public void SetMainObject(ISerializableObject obj)
    {
        if (!ReferenceEquals(obj.Scope, _scope)) AddObject(obj);
        MainObject = obj;
    }

    /// <summary>Records a dependency on another asset by GUID.</summary>
    public void DependsOnAsset(ScopeId guid) => _dependencies.Add(guid);
}
