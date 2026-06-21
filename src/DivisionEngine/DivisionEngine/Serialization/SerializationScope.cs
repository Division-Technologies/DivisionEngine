using System.Buffers;
using VYaml.Emitter;
using VYaml.Parser;

namespace DivisionEngine;

public sealed class SerializationScope : IDisposable
{
    private readonly Dictionary<LocalId, ISerializableObject> _objects = new();
    private ISerializationScopeLoader _loader;
    private int _nextLocalId;

    internal SerializationScope(ScopeId id, ISerializationScopeLoader loader)
    {
        _loader = loader;
        Id = id;
    }

    internal SerializationScope(SerializationScope source) : this(source.Id, source._loader)
    {
        foreach (var (localId, old) in source._objects)
        {
            var typeName = old.GetType().FullName;
            if (typeName == null) continue;
            var newType = Type.GetType(typeName);
            if (newType == null) continue;
            var newObj = (ISerializableObject?)Activator.CreateInstance(newType);
            if (newObj == null) continue;
            newObj.Id = localId;
            newObj.Scope = this;

            _objects[localId] = newObj;
        }
    }

    public ScopeId Id { get; }

    /// <summary>
    ///     All objects currently materialized in this scope, keyed by their <see cref="LocalId" />.
    ///     Used by the authoring layer to serialize a scope to disk.
    /// </summary>
    internal IReadOnlyDictionary<LocalId, ISerializableObject> Objects => _objects;

    /// <summary>
    ///     Registers a new object in the scope, assigning it the next available <see cref="LocalId" />.
    ///     Used when authoring a new asset in memory (objects normally enter a scope lazily via the
    ///     loader). The assigned id is required for other objects to reference this one.
    /// </summary>
    internal LocalId Add(ISerializableObject obj)
    {
        var id = new LocalId(_nextLocalId++);
        obj.Scope = this;
        obj.Id = id;
        _objects[id] = obj;
        return id;
    }

    public void Dispose()
    {
        _loader.Dispose();
    }

    internal ISerializableObject? Resolve(LocalId id, out bool needReload)
    {
        if (!_objects.TryGetValue(id, out var obj))
        {
            obj = _loader.Load(id);
            if (obj is null)
            {
                needReload = false;
                return null;
            }

            obj.Scope = this;
            obj.Id = id;
            _objects[id] = obj ?? throw new InvalidOperationException();
            needReload = true;
        }
        else
        {
            needReload = false;
        }

        return obj;
    }

    internal void Reload(ISerializableObject obj, ISerializedObjectResolver resolver)
    {
        _loader.Deserialize(obj, resolver);
    }

    internal void Reload(ISerializationScopeLoader loader, ISerializedObjectResolver resolver)
    {
        _loader.Dispose();
        _loader = loader;
        foreach (var (_, obj) in _objects) _loader.Deserialize(obj, resolver);
    }

    internal void Transfer(SerializationScope source, ISerializedObjectResolver resolver)
    {
        var writer = new ArrayBufferWriter<byte>();
        foreach (var (localId, old) in source._objects)
        {
            if (!_objects.TryGetValue(localId, out var newObj)) continue;
            var emitter = new Utf8YamlEmitter(writer);
            var serializer = new YamlSerializer(emitter);
            old.Serialize(ref serializer);

            var parser = new YamlParser(new ReadOnlySequence<byte>(writer.WrittenMemory));
            var deserializer = new YamlDeserializer(parser, resolver);
            newObj.Deserialize(ref deserializer);
        }
    }
}