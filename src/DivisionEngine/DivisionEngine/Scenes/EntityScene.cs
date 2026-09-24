using System.Buffers;
using System.Runtime.InteropServices;
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
///         Every component is held in its written form — field by field, entity references as
///         scene-local ids, asset references as their global ids — and only read into a type when the
///         scene is applied. So a scene holds nothing of the types it was made from: it can be kept
///         across a swap of the assemblies that define them, and a component that gains, loses or
///         reorders a field still loads, laid out for the types of the moment.
///     </para>
///     <para>
///         Applying is tolerant. A component whose type is gone, or whose value no longer reads as its
///         type, does not stop the load: it is set aside on the entity as a
///         <see cref="MissingComponents" /> entry, reported, and written back as it was when the entity
///         is next captured.
///     </para>
///     <para>
///         Scene-local ids are the entities' indices in the world they were captured from. That keeps
///         them stable across a script reload, which rebuilds each entity under its old handle; a
///         scene applied to any other world gets new entities, and the ids only link records within
///         the scene.
///     </para>
/// </summary>
[TypeId("2d5b8e07-14af-4c93-a6d2-9f01b3e6c800")]
public sealed class EntityScene : ISerializableObject
{
    private const int FieldEntities = 0;
    private const int NodeId = 0;

    private readonly List<string> _warnings = new();
    private List<EntityRecord> _entities = new();

    public int EntityCount => _entities.Count;

    /// <summary>What was dropped or set aside while the scene was captured.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    public SerializationScope Scope { get; set; } = null!;
    public LocalId Id { get; set; }

    // ------------------------------------------------------------- serialization

    void ISerializable.Serialize<TSerializer>(ref TSerializer serializer)
    {
        serializer.BeginArray(FieldEntities, "entities"u8, _entities.Count);
        foreach (var record in _entities)
        {
            record.Serialize(ref serializer);
        }

        serializer.EndArray();
    }

    void ISerializable.Deserialize<TDeserializer>(ref TDeserializer deserializer)
    {
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

    /// <summary>Writes the scene to a self-contained byte buffer.</summary>
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

    /// <summary>Reads back a scene written by <see cref="ToBytes" />. No component type is needed for this.</summary>
    public static EntityScene FromBytes(ReadOnlyMemory<byte> bytes)
    {
        var parser = new YamlParser(new ReadOnlySequence<byte>(bytes));
        var deserializer = new YamlDeserializer(parser, null);
        deserializer.TryBeginObject(out _, out _);
        var scene = new EntityScene();
        ((ISerializable)scene).Deserialize(ref deserializer);
        return scene;
    }

    // ------------------------------------------------------------------ capture

    /// <summary>Captures every entity in the world, ordered by entity index so the output is stable.</summary>
    public static EntityScene CaptureFrom(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return CaptureFrom(world, CollectAll(world));
    }

    /// <summary>
    ///     Captures the given entities, in the order supplied. References to entities outside the set
    ///     are written as null. What cannot be written — a managed component that is not
    ///     <see cref="ISerializable" />, an unmanaged one with no value serializer, a reference to an
    ///     object that belongs to no asset — is left out and listed in <see cref="Warnings" />.
    /// </summary>
    public static EntityScene CaptureFrom(World world, IEnumerable<Entity> entities)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(entities);

        var scene = new EntityScene();
        var list = entities as IReadOnlyCollection<Entity> ?? entities.ToList();

        // The whole set has to be known before anything is written, so references between captured
        // entities come out as ids.
        var context = new EntitySerializationContext();
        foreach (var entity in list)
        {
            if (!world.IsAlive(entity))
            {
                throw new EntityNotAliveException(entity);
            }

            context.MapToPersistent(entity, entity.Index);
        }

        using var _ = context.Enter();
        foreach (var entity in list)
        {
            var record = new EntityRecord { Id = entity.Index, Version = entity.Version };
            foreach (var type in world.GetArchetype(entity).Types)
            {
                scene.CaptureComponent(world, entity, ComponentTypeRegistry.GetInfo(type), record);
            }

            scene._entities.Add(record);
        }

        return scene;
    }

    private void CaptureComponent(World world, Entity entity, ComponentTypeInfo info, EntityRecord record)
    {
        if (info.IsManaged)
        {
            var instance = world.GetManagedComponent(entity, info.Id);
            if (instance is MissingComponents missing)
            {
                // Written back as the components they were, so they load again once their types do.
                foreach (var entry in missing.Entries)
                {
                    record.Components.Add(new ComponentRecord(entry.TypeId, entry.Value));
                }

                return;
            }

            if (instance is not ISerializable serializable)
            {
                _warnings.Add(instance is null
                    ? $"{entity}: {info.Type} was never assigned a value, so it was not saved."
                    : $"{entity}: {info.Type} does not implement ISerializable, so it was not saved. "
                      + "Derive it from SerializableObject and mark it [AutoSerialization].");
                return;
            }

            record.Components.Add(new ComponentRecord(info.SerializedTypeId, WriteNode(entity, info,
                (ref s) =>
                {
                    s.BeginStruct(NodeId, ""u8);
                    serializable.Serialize(ref s);
                    s.EndStruct();
                })));
            return;
        }

        if (info.Size == 0)
        {
            record.Components.Add(new ComponentRecord(info.SerializedTypeId, null));
            return;
        }

        if (info.ValueSerializer is not { } valueSerializer)
        {
            _warnings.Add($"{entity}: {info.Type} has no value serializer, so it was not saved. "
                          + "Mark it [Component] and [AutoSerialization].");
            return;
        }

        var bytes = new byte[info.Size];
        world.CopyComponentUnchecked(entity, info, bytes);
        record.Components.Add(new ComponentRecord(info.SerializedTypeId, WriteNode(entity, info,
            (ref s) => valueSerializer.Serialize(ref s, NodeId, ""u8, bytes))));
    }

    private byte[] WriteNode(Entity entity, ComponentTypeInfo info, NodeWriter write)
    {
        var writer = new ArrayBufferWriter<byte>();
        var serializer = YamlSerializer.ForNode(new Utf8YamlEmitter(writer), unscoped =>
            _warnings.Add($"{entity}: {info.Type} refers to a {unscoped.GetType().Name} that belongs to no asset; "
                          + "the reference was saved as null."));
        write(ref serializer);
        return writer.WrittenSpan.ToArray();
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
    ///     Creates the scene's entities in <paramref name="world" /> and returns them in record order.
    ///     Entity-valued fields are rewritten to the entities just created; asset references are
    ///     resolved through <paramref name="references" /> (null leaves them null, with a warning).
    ///     Components that cannot be loaded are set aside as <see cref="MissingComponents" /> and
    ///     reported to <paramref name="warnings" />. Each application builds its own instances, so the
    ///     same scene can be applied any number of times.
    /// </summary>
    public Entity[] ApplyTo(World world, ISerializedObjectResolver? references = null,
        ICollection<string>? warnings = null)
    {
        return ApplyTo(world, references, warnings, false);
    }

    /// <param name="preserveHandles">
    ///     Rebuild each entity under the handle it was captured with (<see cref="World.CreateEntityAt" />)
    ///     rather than a new one: what a reload does, so that nothing holding a handle notices.
    /// </param>
    internal Entity[] ApplyTo(World world, ISerializedObjectResolver? references, ICollection<string>? warnings,
        bool preserveHandles)
    {
        ArgumentNullException.ThrowIfNull(world);
        warnings ??= new List<string>();

        // Everything that could refuse the scene is checked before the world is touched, so a bad
        // scene leaves the world as it was rather than half built.
        var ids = new HashSet<int>();
        foreach (var record in _entities)
        {
            if (record.Id < 0 || !ids.Add(record.Id))
            {
                throw new EntitySceneException($"The scene has an invalid or duplicate entity id {record.Id}.");
            }

            if (preserveHandles && (record.Version <= 0 || world.IsIndexInUse(record.Id)))
            {
                throw new EntitySceneException(
                    $"Entity {record.Id} cannot be rebuilt under its old handle: the scene carries no version for it, "
                    + "or the index is in use.");
            }
        }

        var plans = new List<ComponentPlan>[_entities.Count];
        var created = new Entity[_entities.Count];
        var types = new List<ComponentTypeId>();
        for (var i = 0; i < _entities.Count; i++)
        {
            var record = _entities[i];
            var plan = plans[i] = Plan(record);
            types.Clear();
            foreach (var component in plan)
            {
                if (component.Info is { } info)
                {
                    types.Add(info.Id);
                }
            }

            if (plan.Exists(c => c.Info is null))
            {
                types.Add(ComponentType<MissingComponents>.Id);
            }

            var span = CollectionsMarshal.AsSpan(types);
            created[i] = preserveHandles
                ? world.CreateEntityAt(new Entity(record.Id, record.Version), span)
                : world.CreateEntity(span);
        }

        // The records hold scene-local ids; only now do the entities they stand for exist.
        var context = new EntitySerializationContext();
        for (var i = 0; i < _entities.Count; i++)
        {
            context.MapToLive(_entities[i].Id, created[i]);
        }

        var resolver = new ReportingResolver(references, warnings);
        using var _ = context.Enter();
        for (var i = 0; i < _entities.Count; i++)
        {
            var entity = created[i];
            MissingComponents? missing = null;
            foreach (var component in plans[i])
            {
                var reason = component.Info is { } info
                    ? TryLoad(world, entity, info, component.Record.Value, resolver, out var failure)
                        ? null
                        : failure
                    : component.Reason;
                if (reason is null)
                {
                    continue;
                }

                if (component.Info is { } unloaded)
                {
                    world.RemoveComponent(entity, unloaded.Id);
                }

                missing ??= new MissingComponents();
                missing.Add(new MissingComponent(component.Record.TypeId, component.Record.Value, reason));
                warnings.Add($"{entity}: component {component.Record.TypeId} was kept aside: {reason}");
            }

            if (missing is null)
            {
                continue;
            }

            // Created with the entity when a type was already known to be missing; added now when
            // only reading the value failed.
            if (world.HasComponent<MissingComponents>(entity))
            {
                world.SetManagedComponent(entity, missing);
            }
            else
            {
                world.AddManagedComponent(entity, missing);
            }
        }

        return created;
    }

    /// <summary>Which of a record's components can be created, and why the others cannot.</summary>
    private static List<ComponentPlan> Plan(EntityRecord record)
    {
        var plan = new List<ComponentPlan>(record.Components.Count);
        var seen = new HashSet<ComponentTypeId>();
        foreach (var component in record.Components)
        {
            if (!ComponentTypeRegistry.TryResolveBySerializedTypeId(component.TypeId, out var type))
            {
                plan.Add(new ComponentPlan(component, null,
                    "no component type is registered under this id (was its script deleted or renamed?)"));
                continue;
            }

            if (!seen.Add(type) || type == ComponentType<MissingComponents>.Id)
            {
                continue;
            }

            plan.Add(new ComponentPlan(component, ComponentTypeRegistry.GetInfo(type), null));
        }

        return plan;
    }

    /// <summary>Reads one component's value into the entity. On failure, returns why.</summary>
    private static bool TryLoad(World world, Entity entity, ComponentTypeInfo info, byte[]? node,
        ISerializedObjectResolver resolver, out string? failure)
    {
        failure = null;
        try
        {
            if (info.IsManaged)
            {
                var instance = Activator.CreateInstance(info.Type) as ISerializable ?? throw new EntitySceneException(
                    $"{info.Type} does not implement ISerializable.");
                if (node is not null)
                {
                    var deserializer = YamlDeserializer.OverNode(node, resolver);
                    if (deserializer.TryBeginStruct(NodeId, ""u8))
                    {
                        instance.Deserialize(ref deserializer);
                        deserializer.EndStruct();
                    }
                }

                world.SetManagedComponent(entity, info.Id, instance);
                return true;
            }

            if (info.Size == 0 || node is null)
            {
                return true; // a tag, or a value written before the component had fields: default
            }

            var reader = info.ValueSerializer ?? throw new EntitySceneException(
                $"{info.Type} has no value serializer. Mark it [Component] and [AutoSerialization].");
            var value = new byte[info.Size];
            var valueDeserializer = YamlDeserializer.OverNode(node, resolver);
            reader.Deserialize(ref valueDeserializer, NodeId, ""u8, value);
            world.SetComponent(entity, info.Id, value);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = $"its value does not read as {info.Type.Name} ({ex.Message})";
            return false;
        }
    }

    private delegate void NodeWriter(ref YamlSerializer serializer);

    private readonly record struct ComponentPlan(ComponentRecord Record, ComponentTypeInfo? Info, string? Reason);

    /// <summary>Resolves through the given resolver and reports references that come back empty.</summary>
    private sealed class ReportingResolver(ISerializedObjectResolver? inner, ICollection<string> warnings)
        : ISerializedObjectResolver
    {
        public ISerializableObject? Resolve(GlobalId id)
        {
            if (id.ScopeId.Value == Guid.Empty)
            {
                return null; // written as null
            }

            var resolved = inner?.Resolve(id);
            if (resolved is null)
            {
                warnings.Add($"A reference to {id} could not be resolved and was left null.");
            }

            return resolved;
        }
    }

    private sealed class EntityRecord
    {
        private const int FieldId = 0;
        private const int FieldComponents = 1;
        private const int FieldVersion = 2;

        public int Id;

        /// <summary>The captured handle's version; what lets a reload restore the handle itself.</summary>
        public int Version;

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
            serializer.I32(FieldVersion, "version"u8, Version);
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

            record.Version = deserializer.I32(FieldVersion, "version"u8);
            deserializer.EndStruct();
            return record;
        }
    }

    /// <summary>
    ///     One component of one entity: its persisted type id and its value as written (null for a
    ///     tag). Nothing here needs the type to exist, which is what lets a scene outlive it.
    /// </summary>
    private readonly record struct ComponentRecord(string TypeId, byte[]? Value)
    {
        private const int FieldType = 0;
        private const int FieldValue = 1;

        public void Serialize<TSerializer>(ref TSerializer serializer)
            where TSerializer : ISerializer, allows ref struct
        {
            serializer.BeginStruct(0, ""u8);
            SerializerExtensions.Utf16(ref serializer, FieldType, "type"u8, TypeId);
            if (Value is not null)
            {
                serializer.RawNode(FieldValue, "value"u8, Value);
            }

            serializer.EndStruct();
        }

        public static ComponentRecord Deserialize<TDeserializer>(ref TDeserializer deserializer)
            where TDeserializer : IDeserializer, allows ref struct
        {
            if (!deserializer.TryBeginStruct(0, ""u8))
            {
                return new ComponentRecord("", null);
            }

            var typeId = DeserializerExtensions.String(ref deserializer, FieldType, "type"u8);
            var value = deserializer.RawNode(FieldValue, "value"u8);
            deserializer.EndStruct();
            return new ComponentRecord(typeId, value);
        }
    }
}