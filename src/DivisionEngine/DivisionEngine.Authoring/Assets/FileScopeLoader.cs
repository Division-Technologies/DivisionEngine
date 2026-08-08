using System.Buffers;
using VYaml.Parser;

namespace DivisionEngine.Authoring.Assets;

/// <summary>
///     Loads a <see cref="SerializationScope" /> from a YAML document stream held in memory
///     (a direct asset file, or an import cache file). Each object in the scope is a separate
///     YAML document of the form <c>{0: localId, 1: typeName, 2: {fields}}</c>.
///     <para>
///         Implements the two-pass contract described in the serialization design notes:
///         <see cref="Load" /> creates an uninitialized instance (pass 1) and
///         <see cref="Deserialize" /> populates its fields (pass 2). Splitting the passes lets
///         object references be resolved against instances that exist but are not yet filled.
///     </para>
/// </summary>
public sealed class FileScopeLoader : ISerializationScopeLoader
{
    private readonly byte[] _data;
    private readonly Dictionary<LocalId, Type> _index = new();
    private readonly ITypeResolver? _typeResolver;

    /// <param name="data">The serialized scope (a stream of YAML object documents).</param>
    /// <param name="typeResolver">
    ///     Resolves object type names; pass the active user-ALC resolver during script reload so user
    ///     types bind to the freshly loaded assemblies. Null uses the default AppDomain scan.
    /// </param>
    public FileScopeLoader(byte[] data, ITypeResolver? typeResolver = null)
    {
        _data = data;
        _typeResolver = typeResolver;
        BuildIndex();
    }

    /// <summary>The id of the first object in the file — the scope's main/root object by convention.</summary>
    public LocalId MainId { get; private set; }

    /// <summary>The ids of every object in the scope.</summary>
    public IReadOnlyCollection<LocalId> ObjectIds => _index.Keys;

    public ISerializableObject? Load(LocalId id)
    {
        if (!_index.TryGetValue(id, out var type))
        {
            return null;
        }

        return (ISerializableObject?)Activator.CreateInstance(type);
    }

    public void Deserialize(ISerializableObject obj, ISerializedObjectResolver resolver)
    {
        // Re-scan from the start until the matching document is found, then deserialize in place.
        // O(n) per object; acceptable for the small scopes assets produce today.
        var deserializer =
            new YamlDeserializer(new YamlParser(new ReadOnlySequence<byte>(_data)), resolver, _typeResolver);
        while (deserializer.TryBeginObject(out var id, out _))
        {
            if (id == obj.Id)
            {
                obj.Deserialize(ref deserializer);
                deserializer.EndObject();
                return;
            }

            deserializer.EndObject();
        }
    }

    public void Dispose()
    {
    }

    /// <summary>
    ///     Returns the set of scopes referenced by objects in this scope, by deserializing each
    ///     object against a resolver that records (rather than follows) references. Used to index
    ///     the transitive closure of referenced scopes up front, so the real load never has to parse
    ///     another file while one is already being parsed (VYaml parsers are not re-entrant).
    /// </summary>
    public HashSet<ScopeId> CollectReferencedScopes()
    {
        var sink = new HashSet<ScopeId>();
        var collector = new ReferenceCollectingResolver(sink);
        foreach (var (id, type) in _index)
        {
            if (Activator.CreateInstance(type) is not ISerializableObject obj)
            {
                continue;
            }

            obj.Id = id;
            Deserialize(obj, collector);
        }

        return sink;
    }

    private void BuildIndex()
    {
        var deserializer = new YamlDeserializer(new YamlParser(new ReadOnlySequence<byte>(_data)), null, _typeResolver);
        var first = true;
        while (deserializer.TryBeginObject(out var id, out var type))
        {
            _index[id] = type;
            if (first)
            {
                MainId = id;
                first = false;
            }

            deserializer.EndObject();
        }
    }

    /// <summary>A resolver that records the scope of every reference and resolves them all to null.</summary>
    private sealed class ReferenceCollectingResolver(HashSet<ScopeId> sink) : ISerializedObjectResolver
    {
        public ISerializableObject? Resolve(GlobalId id)
        {
            sink.Add(id.ScopeId);
            return null;
        }
    }
}