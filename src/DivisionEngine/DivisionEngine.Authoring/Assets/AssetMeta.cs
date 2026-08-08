using System.Buffers;
using VYaml.Emitter;
using VYaml.Parser;

namespace DivisionEngine.Authoring.Assets;

/// <summary>
///     The persisted state of an imported asset, stored as a <c>.meta</c> sidecar next to the source
///     file. Records the asset's stable GUID, the importer instance (with its settings), the source
///     content hash and importer version used to produce the cache, the asset's main object id, and
///     the assets this import depends on.
/// </summary>
public sealed class AssetMeta
{
    public List<Guid> Dependencies = new();
    public Guid Guid;

    /// <summary>The importer that produced this asset, or null for a direct (serialization-native) asset.</summary>
    public IAssetImporter? Importer;

    public uint ImporterVersion;

    /// <summary>
    ///     Extra source files this import consumed (beyond the source asset itself), stored relative to
    ///     the source file's directory. Their combined content feeds <see cref="SourceHash" /> so a
    ///     change to any of them invalidates the cache (e.g. the .cs files compiled by a .csproj).
    /// </summary>
    public List<string> InputFiles = new();

    public int MainLocalId;
    public long SourceHash;

    /// <summary>
    ///     Reads a <c>.meta</c> file: a header document followed by the importer serialized as a typed
    ///     document (so its concrete type and settings are restored). Pass the active user-ALC
    ///     resolver so importer types defined in user code resolve against the loaded scripts.
    /// </summary>
    public static AssetMeta Read(string path, ITypeResolver? typeResolver = null)
    {
        var data = File.ReadAllBytes(path);
        var deserializer = new YamlDeserializer(new YamlParser(new ReadOnlySequence<byte>(data)), null, typeResolver);

        if (!deserializer.TryBeginObject(out _, out var headerType))
        {
            throw new InvalidDataException($"Malformed meta header in '{path}'.");
        }

        var header = (AssetMetaHeader)Activator.CreateInstance(headerType)!;
        ((ISerializable)header).Deserialize(ref deserializer);
        deserializer.EndObject();

        IAssetImporter? importer = null;
        if (header.HasImporter)
        {
            if (!deserializer.TryBeginObject(out _, out var importerType))
            {
                throw new InvalidDataException($"Missing importer in meta '{path}'.");
            }

            importer = (IAssetImporter)Activator.CreateInstance(importerType)!;
            importer.Deserialize(ref deserializer);
            deserializer.EndObject();
        }

        return new AssetMeta
        {
            Guid = header.Guid,
            SourceHash = header.SourceHash,
            ImporterVersion = header.ImporterVersion,
            MainLocalId = header.MainLocalId,
            Dependencies = header.Dependencies,
            InputFiles = header.InputFiles,
            Importer = importer
        };
    }

    /// <summary>
    ///     Writes a <c>.meta</c> file: a header document, optionally followed by the importer
    ///     serialized as a typed document (omitted for direct assets).
    /// </summary>
    public static void Write(string path, AssetMeta meta)
    {
        var writer = new ArrayBufferWriter<byte>();
        var emitter = new Utf8YamlEmitter(writer);
        var serializer = new YamlSerializer(emitter);

        var header = new AssetMetaHeader
        {
            Guid = meta.Guid,
            HasImporter = meta.Importer is not null,
            SourceHash = meta.SourceHash,
            ImporterVersion = meta.ImporterVersion,
            MainLocalId = meta.MainLocalId,
            Dependencies = meta.Dependencies,
            InputFiles = meta.InputFiles
        };
        serializer.BeginObject(new LocalId(0), typeof(AssetMetaHeader));
        ((ISerializable)header).Serialize(ref serializer);
        serializer.EndObject();

        if (meta.Importer is not null)
        {
            serializer.BeginObject(new LocalId(1), meta.Importer.GetType());
            meta.Importer.Serialize(ref serializer);
            serializer.EndObject();
        }

        File.WriteAllBytes(path, writer.WrittenSpan.ToArray());
    }
}

/// <summary>Scalar/collection fields of an <see cref="AssetMeta" />, serialized via the source generator.</summary>
[AutoSerialization]
internal partial class AssetMetaHeader
{
    [Serialize] public List<Guid> Dependencies = new();
    [Serialize] public Guid Guid;
    [Serialize] public bool HasImporter;
    [Serialize] public uint ImporterVersion;
    [Serialize] public List<string> InputFiles = new();
    [Serialize] public int MainLocalId;
    [Serialize] public long SourceHash;
}