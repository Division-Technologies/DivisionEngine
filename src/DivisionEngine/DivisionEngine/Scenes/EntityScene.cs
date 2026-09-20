using System.Buffers;
using VYaml.Emitter;
using VYaml.Parser;

namespace DivisionEngine;

/// <summary>Thrown when a scene cannot be captured from, or applied to, a world.</summary>
public sealed class EntitySceneException(string message) : InvalidOperationException(message);

/// <summary>
///     A world's entities in a form that can be written to an asset and read back — the persisted
///     shape of a scene (Notes/Core/SceneManagement.md).
///     <para>
///         It is a document, not a view: <see cref="CaptureFrom(World)" /> copies entities out of a
///         world and <see cref="ApplyTo" /> builds them in one, with serialization in between. That
///         keeps <see cref="World" /> itself free of any knowledge of the serialization layer.
///     </para>
///     <para>
///         Entities are stored under scene-local ids rather than their live handles, which are
///         indices into a particular world and mean nothing once it is rebuilt. Component values keep
///         their fields, not their bytes, so a component that gains, loses or reorders a field still
///         loads — the layout is re-derived from the types of the current run.
///     </para>
/// </summary>
public sealed class EntityScene : ISerializableObject
{
    private const int FieldEntities = 0;

    private List<EntityRecord> _entities = new();

    public SerializationScope Scope { get; set; } = null!;
    public LocalId Id { get; set; }

    public int EntityCount => _entities.Count;

    // ---------------------------------------------------------------- snapshots

    /// <summary>
    ///     Writes the scene to a self-contained byte buffer.
    ///     <para>
    ///         This is the form that survives a change to the component types themselves, and the
    ///         reason a reload has to pass through it rather than just capture and re-apply: capture
    ///         copies component values as raw chunk bytes, laid out for the types of the moment,
    ///         whereas writing them out goes field by field. Read back against changed types, the
    ///         fields land where they now belong.
    ///     </para>
    /// </summary>
    public byte[] ToBytes()
    {
        var writer = new ArrayBufferWriter<byte>();
        var emitter = new Utf8YamlEmitter(writer);
        var serializer = new YamlSerializer(emitter);
        serializer.BeginObject(Id, typeof(EntityScene));
        ((ISerializable)this).Serialize(ref serializer);
        serializer.EndObject();
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    ///     Reads back a scene written by <see cref="ToBytes" />. Component types are resolved by their
    ///     persisted ids against whatever is registered now, so this must run after any assembly swap
    ///     that redefines them.
    /// </summary>
    public static EntityScene FromBytes(ReadOnlyMemory<byte> bytes)
    {
        var parser = new YamlParser(new ReadOnlySequence<byte>(bytes));
        var deserializer = new YamlDeserializer(parser, NoReferences.Instance);
        deserializer.TryBeginObject(out _, out _);
        var scene = new EntityScene();
        ((ISerializable)scene).Deserialize(ref deserializer);
        return scene;
    }

    /// <summary>A scene holds no object references today, so nothing should ask to resolve one.</summary>
    private sealed class NoReferences : ISerializedObjectResolver
    {
        public static readonly NoReferences Instance = new();

        public ISerializableObject? Resolve(GlobalId id)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ capture

    /// <summary>Captures every entity in the world, ordered by entity index so the output is stable.</summary>
    public static EntityScene CaptureFrom(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return CaptureFrom(world, CollectAll(world));
    }

    /// <summary>Captures the given entities, in the order supplied.</summary>
    public static EntityScene CaptureFrom(World world, IEnumerable<Entity> entities)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(entities);

        var scene = new EntityScene();
        var toPlaceholder = new Dictionary<Entity, Entity>();

        foreach (var entity in entities)
        {
            if (!world.IsAlive(entity))
            {
                throw new EntityNotAliveException(entity);
            }

            var record = new EntityRecord { Id = scene._entities.Count };
            toPlaceholder[entity] = EntitySerializationContext.PlaceholderFor(record.Id);

            foreach (var type in world.GetArchetype(entity).Types)
            {
                var info = ComponentTypeRegistry.GetInfo(type);
                if (info.IsManaged)
                {
                    // Managed components are written by value, not as a reference into a scope: a
                    // class component belongs to its entity, so a scene stays self-contained and
                    // applying it twice gives each copy its own instance.
                    if (world.GetManagedComponent(entity, type) is not ISerializable serializable)
                    {
                        throw new EntitySceneException(
                            $"{info.Type} is a managed component that does not implement ISerializable, so it cannot be saved. "
                            + "Derive it from SerializableObject and mark it [AutoSerialization].");
                    }

                    record.Components.Add(new ComponentRecord(info.SerializedTypeId, serializable));
                    continue;
                }

                var value = info.Size == 0 ? [] : new byte[info.Size];
                if (info.Size > 0)
                {
                    world.CopyComponentUnchecked(entity, info, value);
                }

                record.Components.Add(new ComponentRecord(info.SerializedTypeId, value));
            }

            scene._entities.Add(record);
        }

        // Only now is the whole set known, so references between captured entities can be rewritten.
        // A scene never holds live handles: after this pass its contents are identical in shape to a
        // freshly loaded one, so capturing and applying without saving in between works too.
        var map = new EntityRemap(toPlaceholder);

        // Managed components cannot be rewritten at the byte level, so they are copied through the
        // serializer instead, under a context that reads live handles and writes placeholders.
        var capture = new EntitySerializationContext();
        foreach (var (live, placeholder) in toPlaceholder)
        {
            var id = -1 - placeholder.Index;
            capture.MapToPersistent(live, id);
            capture.MapToLive(id, placeholder);
        }

        using (capture.Enter())
        {
            foreach (var record in scene._entities)
            {
                for (var i = 0; i < record.Components.Count; i++)
                {
                    var component = record.Components[i];
                    var type = Resolve(component.TypeId);
                    if (component.Managed is not null)
                    {
                        record.Components[i] = component.Detached(ComponentTypeRegistry.GetInfo(type));
                    }
                    else if (component.Value.Length > 0)
                    {
                        ComponentTypeRegistry.RemapEntityFields(type, component.Value, map);
                    }
                }
            }
        }

        return scene;
    }

    private static List<Entity> CollectAll(World world)
    {
        var entities = new List<Entity>(world.EntityCount);
        foreach (var chunk in world.Query().Build())
        {
            entities.AddRange(chunk.Entities);
        }

        entities.Sort(static (a, b) => a.Index.CompareTo(b.Index));
        return entities;
    }

    // -------------------------------------------------------------------- apply

    /// <summary>
    ///     Creates the scene's entities in <paramref name="world" /> and returns them indexed by
    ///     scene-local id. Entity-valued component fields are rewritten to the newly created entities
    ///     for every component type that declared its entity fields
    ///     (<see cref="ComponentTypeRegistry.RegisterEntityFields{T}" />).
    /// </summary>
    public Entity[] ApplyTo(World world)
    {
        ArgumentNullException.ThrowIfNull(world);

        var created = new Entity[_entities.Count];
        var types = new List<ComponentTypeId>();

        for (var i = 0; i < _entities.Count; i++)
        {
            var record = _entities[i];
            types.Clear();
            foreach (var component in record.Components)
            {
                types.Add(Resolve(component.TypeId));
            }

            created[record.Id] = world.CreateEntity(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(types));
        }

        // The records hold placeholders; only now do the entities they stand for exist. The rewrite
        // goes through a scratch copy so the scene keeps its placeholders and can be applied again,
        // which is what instantiating the same scene more than once relies on.
        var map = new EntityRemap(created);

        // The same translation for managed components, which are copied through the serializer:
        // placeholders on the way out, the entities just created on the way back in.
        var apply = new EntitySerializationContext();
        foreach (var record in _entities)
        {
            apply.MapToPersistent(EntitySerializationContext.PlaceholderFor(record.Id), record.Id);
            apply.MapToLive(record.Id, created[record.Id]);
        }

        using var _ = apply.Enter();

        Span<byte> scratch = stackalloc byte[256];
        for (var i = 0; i < _entities.Count; i++)
        {
            var record = _entities[i];
            var entity = created[record.Id];
            foreach (var component in record.Components)
            {
                var type = Resolve(component.TypeId);
                var info = ComponentTypeRegistry.GetInfo(type);
                if (info.IsManaged)
                {
                    world.SetManagedComponent(entity, type, component.CloneManaged(info));
                    continue;
                }

                if (info.Size == 0)
                {
                    continue;
                }

                var value = info.Size <= scratch.Length ? scratch[..info.Size] : new byte[info.Size];
                component.Value.AsSpan().CopyTo(value);
                ComponentTypeRegistry.RemapEntityFields(type, value, map);
                world.SetComponent(entity, type, value);
            }
        }

        return created;
    }

    private static ComponentTypeId Resolve(string serializedTypeId)
    {
        if (!ComponentTypeRegistry.TryResolveBySerializedTypeId(serializedTypeId, out var type))
        {
            throw new EntitySceneException(
                $"No component type is registered under the serialized type id {serializedTypeId}. "
                + "Component types must carry [Component] so they register when their assembly loads.");
        }

        return type;
    }

    // ------------------------------------------------------------- serialization

    void ISerializable.Serialize<TSerializer>(ref TSerializer serializer)
    {
        // Entity-valued fields resolve through this context while the component formatters run.
        var context = new EntitySerializationContext();
        foreach (var record in _entities)
        {
            context.Map(EntitySerializationContext.PlaceholderFor(record.Id), record.Id);
        }

        using var _ = context.Enter();

        serializer.BeginArray(FieldEntities, "entities"u8, _entities.Count);
        foreach (var record in _entities)
        {
            record.Serialize(ref serializer);
        }

        serializer.EndArray();
    }

    void ISerializable.Deserialize<TDeserializer>(ref TDeserializer deserializer)
    {
        using var _ = EntitySerializationContext.ForLoading().Enter();

        _entities = new List<EntityRecord>();
        if (!deserializer.TryBeginArray(FieldEntities, "entities"u8, out var count))
        {
            return;
        }

        for (var i = 0; i < count; i++)
        {
            _entities.Add(EntityRecord.Deserialize(ref deserializer));
        }

        deserializer.EndArray();
    }

    private sealed class EntityRecord
    {
        private const int FieldId = 0;
        private const int FieldComponents = 1;

        public int Id;
        public List<ComponentRecord> Components { get; } = new();

        public void Serialize<TSerializer>(ref TSerializer serializer)
            where TSerializer : ISerializer, allows ref struct
        {
            serializer.BeginStruct(0, ""u8);
            serializer.I32(FieldId, "id"u8, Id);
            serializer.BeginArray(FieldComponents, "components"u8, Components.Count);
            foreach (var component in Components)
            {
                component.Serialize(ref serializer);
            }

            serializer.EndArray();
            serializer.EndStruct();
        }

        public static EntityRecord Deserialize<TDeserializer>(ref TDeserializer deserializer)
            where TDeserializer : IDeserializer, allows ref struct
        {
            var record = new EntityRecord();
            if (!deserializer.TryBeginStruct(0, ""u8))
            {
                return record;
            }

            record.Id = deserializer.I32(FieldId, "id"u8);
            if (deserializer.TryBeginArray(FieldComponents, "components"u8, out var count))
            {
                for (var i = 0; i < count; i++)
                {
                    record.Components.Add(ComponentRecord.Deserialize(ref deserializer));
                }

                deserializer.EndArray();
            }

            deserializer.EndStruct();
            return record;
        }
    }

    /// <summary>
    ///     One component of one entity. An unmanaged component is held as the chunk bytes it will be
    ///     written back as; a managed one is held as a prototype instance that
    ///     <see cref="EntityScene.ApplyTo" /> copies, so applying a scene twice gives each entity its
    ///     own object rather than two references to the same one.
    /// </summary>
    private readonly struct ComponentRecord
    {
        private const int FieldType = 0;
        private const int FieldValue = 1;

        public ComponentRecord(string typeId, byte[] value)
        {
            TypeId = typeId;
            Value = value;
        }

        public ComponentRecord(string typeId, ISerializable managed)
        {
            TypeId = typeId;
            Value = [];
            Managed = managed;
        }

        public string TypeId { get; }
        public byte[] Value { get; }
        public ISerializable? Managed { get; }

        public void Serialize<TSerializer>(ref TSerializer serializer)
            where TSerializer : ISerializer, allows ref struct
        {
            serializer.BeginStruct(0, ""u8);
            SerializerExtensions.Utf16(ref serializer, FieldType, "type"u8, TypeId);

            var info = ComponentTypeRegistry.GetInfo(Resolve(TypeId));
            if (Managed is { } managed)
            {
                // Inline rather than as an object reference, so the scene file stays readable and
                // self-contained.
                serializer.BeginStruct(FieldValue, "value"u8);
                managed.Serialize(ref serializer);
                serializer.EndStruct();
            }
            else if (info.Size > 0)
            {
                var writer = info.ValueSerializer ?? throw new EntitySceneException(
                    $"{info.Type} has no registered value serializer. Mark it [Component] and [AutoSerialization].");
                writer.Serialize(ref serializer, FieldValue, "value"u8, Value);
            }

            serializer.EndStruct();
        }

        public static ComponentRecord Deserialize<TDeserializer>(ref TDeserializer deserializer)
            where TDeserializer : IDeserializer, allows ref struct
        {
            if (!deserializer.TryBeginStruct(0, ""u8))
            {
                return new ComponentRecord("", []);
            }

            var typeId = DeserializerExtensions.String(ref deserializer, FieldType, "type"u8);

            // The value is read into this run's shape, which is what makes a changed field list load.
            var info = ComponentTypeRegistry.GetInfo(Resolve(typeId));
            ComponentRecord record;
            if (info.IsManaged)
            {
                var managed = NewManaged(info);
                if (deserializer.TryBeginStruct(FieldValue, "value"u8))
                {
                    managed.Deserialize(ref deserializer);
                    deserializer.EndStruct();
                }

                record = new ComponentRecord(typeId, managed);
            }
            else
            {
                var value = info.Size == 0 ? [] : new byte[info.Size];
                if (info.Size > 0)
                {
                    var reader = info.ValueSerializer ?? throw new EntitySceneException(
                        $"{info.Type} has no registered value serializer. Mark it [Component] and [AutoSerialization].");
                    reader.Deserialize(ref deserializer, FieldValue, "value"u8, value);
                }

                record = new ComponentRecord(typeId, value);
            }

            deserializer.EndStruct();
            return record;
        }

        /// <summary>
        ///     A fresh copy of the prototype, so each application of the scene owns its instance, and
        ///     so its entity fields are rewritten: the copy goes out through the serializer and back,
        ///     and the ambient <see cref="EntitySerializationContext" /> decides what an entity handle
        ///     means on each side.
        /// </summary>
        public object CloneManaged(ComponentTypeInfo info)
        {
            var prototype = Managed ?? throw new InvalidOperationException("Not a managed component.");

            var writer = new ArrayBufferWriter<byte>();
            var emitter = new Utf8YamlEmitter(writer);
            var serializer = new YamlSerializer(emitter);
            serializer.BeginObject(default, info.Type);
            prototype.Serialize(ref serializer);
            serializer.EndObject();

            var parser = new YamlParser(new ReadOnlySequence<byte>(writer.WrittenMemory));
            var deserializer = new YamlDeserializer(parser, NoReferences.Instance);
            deserializer.TryBeginObject(out _, out _);
            var clone = NewManaged(info);
            clone.Deserialize(ref deserializer);
            return clone;
        }

        /// <summary>The prototype with its live entity handles replaced by the scene's placeholders.</summary>
        public ComponentRecord Detached(ComponentTypeInfo info)
        {
            return new ComponentRecord(TypeId, (ISerializable)CloneManaged(info));
        }

        private static ISerializable NewManaged(ComponentTypeInfo info)
        {
            if (Activator.CreateInstance(info.Type) is not ISerializable instance)
            {
                throw new EntitySceneException(
                    $"{info.Type} is a managed component that does not implement ISerializable, so it cannot be loaded. "
                    + "Derive it from SerializableObject and mark it [AutoSerialization].");
            }

            return instance;
        }
    }
}
