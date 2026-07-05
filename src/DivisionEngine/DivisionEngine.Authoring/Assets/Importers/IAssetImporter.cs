namespace DivisionEngine.Authoring.Assets;

/// <summary>
///     Converts a source asset file into the runtime objects that represent it. Implementations are
///     user code, marked with <see cref="AssetImporterAttribute" /> to bind them to file extensions.
///     <para>
///         An importer is itself serializable (<see cref="ISerializable" />) so that its settings are
///         persisted alongside the asset in its <c>.meta</c> file; apply <c>[AutoSerialization]</c> to
///         generate the serialization code.
///     </para>
/// </summary>
public interface IAssetImporter : ISerializable
{
    /// <summary>
    ///     Bumped when the importer's logic changes, to invalidate caches produced by older versions.
    /// </summary>
    int Version => 0;

    /// <summary>Reads <see cref="AssetImportContext.SourcePath" /> and emits the asset's objects.</summary>
    void Import(AssetImportContext context);
}