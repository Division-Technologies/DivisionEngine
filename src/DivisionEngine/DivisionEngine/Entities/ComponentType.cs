using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace DivisionEngine;

/// <summary>Process-wide id of a component type. Ids are dense and assigned on first use.</summary>
public readonly record struct ComponentTypeId(int Value)
{
    public override string ToString()
    {
        return ComponentTypeRegistry.GetInfo(this).Type.Name;
    }
}

/// <summary>
///     Rewrites the entity handles held inside a component value. Passed to the remappers registered
///     with <see cref="ComponentTypeRegistry.RegisterEntityFields{T}" />.
///     <para>
///         Two shapes of the same operation share this type. A <em>deferred</em> map resolves the
///         placeholders an <see cref="EntityCommandBuffer" /> handed out, or the ones an
///         <see cref="EntityScene" /> was loaded with, to the entities actually created — indexed, so
///         it is allocation-free on the hot playback path. A <em>table</em> map translates arbitrary
///         handles, which is what capturing a scene needs when it replaces live entities with the
///         placeholders it stores them under.
///     </para>
/// </summary>
public readonly ref struct EntityRemap
{
    private readonly ReadOnlySpan<Entity> _byPlaceholderIndex;
    private readonly IReadOnlyDictionary<Entity, Entity>? _byHandle;

    internal EntityRemap(ReadOnlySpan<Entity> resolved)
    {
        _byPlaceholderIndex = resolved;
    }

    internal EntityRemap(IReadOnlyDictionary<Entity, Entity> table)
    {
        _byHandle = table;
    }

    /// <summary>
    ///     The handle <paramref name="entity" /> should become. A deferred map leaves real handles
    ///     alone and resolves placeholders; a table map translates whatever it knows and collapses
    ///     everything else to <see cref="Entity.Null" />, so a reference leaving the captured set
    ///     becomes explicitly absent rather than a handle that would dangle.
    /// </summary>
    public Entity Resolve(Entity entity)
    {
        if (_byHandle is not null)
        {
            return entity.IsNull || !_byHandle.TryGetValue(entity, out var mapped) ? Entity.Null : mapped;
        }

        if (!entity.IsDeferred)
        {
            return entity;
        }

        var index = -1 - entity.Index;
        if ((uint)index >= (uint)_byPlaceholderIndex.Length)
        {
            throw new InvalidOperationException($"{entity} does not belong to the set being resolved.");
        }

        var resolved = _byPlaceholderIndex[index];
        if (resolved.IsNull)
        {
            throw new InvalidOperationException($"{entity} is used before the command that creates it.");
        }

        return resolved;
    }
}

/// <summary>
///     Rewrites the <see cref="Entity" /> fields held inside a component value, so that placeholders
///     recorded into a command buffer survive playback. Register one per component type that stores
///     entities; see <see cref="ComponentTypeRegistry.RegisterEntityFields{T}" />.
/// </summary>
public delegate void EntityFieldRemapper<T>(ref T value, EntityRemap map) where T : unmanaged;

internal delegate void RawEntityFieldRemapper(Span<byte> value, EntityRemap map);

/// <summary>
///     Reads and writes one unmanaged component value held as raw chunk bytes, without the caller
///     knowing the component's type. The methods stay generic over the serializer so the
///     <c>allows ref struct</c> backends keep their specialized, allocation-free code paths; the
///     interface itself is non-generic so a <see cref="ComponentTypeId" /> alone is enough to reach it.
/// </summary>
public interface IComponentValueSerializer
{
    void Serialize<TSerializer>(ref TSerializer serializer, int id, ReadOnlySpan<byte> hintUtf8, ReadOnlySpan<byte> value)
        where TSerializer : ISerializer, allows ref struct;

    void Deserialize<TDeserializer>(ref TDeserializer deserializer, int id, ReadOnlySpan<byte> hintUtf8, Span<byte> value)
        where TDeserializer : IDeserializer, allows ref struct;
}

/// <summary>Bridges <see cref="IComponentValueSerializer" /> to the type's registered <see cref="IValueFormatter{T}" />.</summary>
internal sealed class ComponentValueSerializer<T> : IComponentValueSerializer where T : unmanaged
{
    public void Serialize<TSerializer>(ref TSerializer serializer, int id, ReadOnlySpan<byte> hintUtf8, ReadOnlySpan<byte> value)
        where TSerializer : ISerializer, allows ref struct
    {
        FormatterStore<T>.Formatter.Serialize(ref serializer, id, hintUtf8, in MemoryMarshal.AsRef<T>(value));
    }

    public void Deserialize<TDeserializer>(ref TDeserializer deserializer, int id, ReadOnlySpan<byte> hintUtf8, Span<byte> value)
        where TDeserializer : IDeserializer, allows ref struct
    {
        MemoryMarshal.AsRef<T>(value) = FormatterStore<T>.Formatter.Deserialize(ref deserializer, id, hintUtf8);
    }
}

/// <summary>
///     Static description of a component type. Unmanaged components (blittable structs) are stored
///     inline in chunks; managed components (classes) are stored as references in per-chunk arrays.
///     A struct with no fields is a tag: it participates in archetype matching but has no storage.
/// </summary>
public sealed class ComponentTypeInfo
{
    internal ComponentTypeInfo(ComponentTypeId id, Type type, int size, int alignment, bool isManaged, bool isTag)
    {
        Id = id;
        Type = type;
        Size = size;
        Alignment = alignment;
        IsManaged = isManaged;
        IsTag = isTag;
        IsBehaviour = isManaged && typeof(Behaviour).IsAssignableFrom(type);
        SerializedTypeId = DivisionEngine.SerializedTypeId.Get(type);
    }

    public ComponentTypeId Id { get; }
    public Type Type { get; }

    /// <summary>Bytes per instance in chunk storage. 0 for managed and tag components.</summary>
    public int Size { get; }

    public int Alignment { get; }
    public bool IsManaged { get; }
    public bool IsTag { get; }
    public bool HasChunkData => !IsManaged && !IsTag;

    /// <summary>Whether this component is a <see cref="Behaviour" />, i.e. logic to run rather than data.</summary>
    public bool IsBehaviour { get; }

    /// <summary>Set when the type stores <see cref="Entity" /> fields that command-buffer playback must remap.</summary>
    internal RawEntityFieldRemapper? EntityRemapper { get; set; }

    public bool HasEntityFields => EntityRemapper is not null;

    /// <summary>
    ///     The type's stable, persisted identity (see <see cref="DivisionEngine.SerializedTypeId" />):
    ///     the <see cref="TypeIdAttribute" /> GUID when pinned, otherwise a hash of the type name.
    ///     Unlike <see cref="Id" />, this survives across runs and across assembly reloads.
    /// </summary>
    public string SerializedTypeId { get; }

    /// <summary>
    ///     Reads and writes this component's values field-wise, set by
    ///     <see cref="ComponentTypeRegistry.RegisterValueSerializer{T}" />. Null for managed and tag
    ///     components, and for unmanaged components nobody declared as serializable.
    /// </summary>
    public IComponentValueSerializer? ValueSerializer { get; internal set; }

    public override string ToString()
    {
        return Type.Name;
    }
}

/// <summary>
///     Registry of component types. Registration happens once per type through
///     <see cref="ComponentType{T}" />; lookups by id are lock-free.
/// </summary>
public static class ComponentTypeRegistry
{
    private const int MaxAlignment = 16;
    private static readonly Lock RegistrationLock = new();
    private static readonly Dictionary<string, ComponentTypeId> BySerializedTypeId = new(StringComparer.Ordinal);
    private static ComponentTypeInfo?[] _infos = new ComponentTypeInfo?[64];
    private static int _count;

    public static int Count => Volatile.Read(ref _count);

    /// <summary>
    ///     Maps a persisted type id (<see cref="ComponentTypeInfo.SerializedTypeId" />) back to the
    ///     component type id of this run. Only types already in the registry are found, which is why
    ///     <see cref="ComponentAttribute" /> exists: it makes the generator register a component as
    ///     soon as its assembly loads, rather than when some code first mentions it.
    /// </summary>
    public static bool TryResolveBySerializedTypeId(string serializedTypeId, out ComponentTypeId id)
    {
        ArgumentNullException.ThrowIfNull(serializedTypeId);
        lock (RegistrationLock)
        {
            return BySerializedTypeId.TryGetValue(serializedTypeId, out id);
        }
    }

    /// <summary>
    ///     Brings a component type into the registry. Emitted from a <c>[ModuleInitializer]</c> for
    ///     every <see cref="ComponentAttribute" /> type, so saved scenes can resolve it by its
    ///     persisted id before any code has otherwise touched it.
    /// </summary>
    public static ComponentTypeId RegisterComponent<T>()
    {
        return ComponentType<T>.Id;
    }

    /// <summary>
    ///     Declares that <typeparamref name="T" />'s values can be read and written field-wise through
    ///     its <see cref="IValueFormatter{T}" />, which is what makes a component's data survive a
    ///     field being added, removed or reordered. Without it, the component is skipped when a scene
    ///     is saved. Emitted alongside <see cref="RegisterComponent{T}" /> for serializable components.
    /// </summary>
    public static void RegisterValueSerializer<T>() where T : unmanaged
    {
        var info = GetInfo(ComponentType<T>.Id);
        if (!info.HasChunkData)
        {
            throw new ArgumentException($"{info.Type} has no chunk data to serialize.");
        }

        lock (RegistrationLock)
        {
            info.ValueSerializer ??= new ComponentValueSerializer<T>();
        }
    }

    public static ComponentTypeInfo GetInfo(ComponentTypeId id)
    {
        var infos = Volatile.Read(ref _infos);
        if ((uint)id.Value >= (uint)Volatile.Read(ref _count))
        {
            throw new ArgumentOutOfRangeException(nameof(id), $"Unknown component type id {id.Value}.");
        }

        return infos[id.Value] ?? throw new InvalidOperationException(
            $"Component type id {id.Value} belonged to an assembly that has been unloaded.");
    }

    /// <summary>
    ///     Drops every component type defined in a collectible load context — that is, every component
    ///     from user scripts — so the assembly holding them can actually be unloaded.
    ///     <para>
    ///         The registry holds a <see cref="Type" /> per component, and a strong reference to a type
    ///         keeps its whole load context alive. Nothing else in the engine pins user assemblies, so
    ///         without this an <c>AssemblyLoadContext.Unload</c> never completes and every reload leaks
    ///         a copy of the user's code.
    ///     </para>
    ///     <para>
    ///         Call it only with the world emptied (<see cref="World.Clear" />): archetypes are keyed on
    ///         the ids being dropped. Ids are never reused, so handles cached in
    ///         <see cref="ComponentType{T}" /> for an unloaded type fail loudly rather than silently
    ///         naming a different component.
    ///     </para>
    /// </summary>
    /// <returns>How many types were dropped.</returns>
    public static int UnregisterUnloadable()
    {
        lock (RegistrationLock)
        {
            var infos = _infos;
            var removed = 0;
            for (var i = 0; i < _count; i++)
            {
                var info = infos[i];
                if (info is null || !IsUnloadable(info.Type))
                {
                    continue;
                }

                BySerializedTypeId.Remove(info.SerializedTypeId);
                infos[i] = null;
                removed++;
            }

            return removed;
        }
    }

    private static bool IsUnloadable(Type type)
    {
        return AssemblyLoadContext.GetLoadContext(type.Assembly) is { IsCollectible: true };
    }

    /// <summary>
    ///     Declares how to rewrite the <see cref="Entity" /> fields of <typeparamref name="T" /> when an
    ///     <see cref="EntityCommandBuffer" /> is played back. Without a registration, placeholders stored
    ///     inside component values survive into the world as dangling handles.
    ///     Register from a <c>[ModuleInitializer]</c> so the type is ready before any world uses it;
    ///     the source generator emits these registrations for user components.
    /// </summary>
    public static void RegisterEntityFields<T>(EntityFieldRemapper<T> remapper) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(remapper);
        var info = GetInfo(ComponentType<T>.Id);
        if (!info.HasChunkData)
        {
            throw new ArgumentException($"{info.Type} has no chunk data and cannot hold entity fields.");
        }

        lock (RegistrationLock)
        {
            info.EntityRemapper = (bytes, map) => remapper(ref MemoryMarshal.AsRef<T>(bytes), map);
        }
    }

    /// <summary>Applies the registered remapper, if any, to a component value held as raw bytes.</summary>
    internal static void RemapEntityFields(ComponentTypeId type, Span<byte> value, EntityRemap map)
    {
        GetInfo(type).EntityRemapper?.Invoke(value, map);
    }

    internal static ComponentTypeId Register<T>()
    {
        var type = typeof(T);
        var isManaged = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
        if (isManaged && !type.IsClass)
        {
            throw new InvalidOperationException(
                $"{type} contains references but is not a class. Components must be either unmanaged structs or classes.");
        }

        var isTag = !isManaged && type.IsValueType &&
                    type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length == 0;
        var size = isManaged || isTag ? 0 : Unsafe.SizeOf<T>();
        var alignment = size == 0 ? 1 : (int)Math.Min(MaxAlignment, BitOperations.RoundUpToPowerOf2((uint)size));

        lock (RegistrationLock)
        {
            var index = _count;
            var infos = _infos;
            if (index == infos.Length)
            {
                var grown = new ComponentTypeInfo?[infos.Length * 2];
                Array.Copy(infos, grown, infos.Length);
                infos = grown;
            }

            var id = new ComponentTypeId(index);
            var info = new ComponentTypeInfo(id, type, size, alignment, isManaged, isTag);
            infos[index] = info;
            Volatile.Write(ref _infos, infos);
            Volatile.Write(ref _count, index + 1);

            // Two component types sharing a persisted id would silently load each other's data.
            if (!BySerializedTypeId.TryAdd(info.SerializedTypeId, id))
            {
                var existing = GetInfo(BySerializedTypeId[info.SerializedTypeId]).Type;
                throw new InvalidOperationException(
                    $"{type} and {existing} share the serialized type id {info.SerializedTypeId}. Pin one of them with [TypeId].");
            }

            return id;
        }
    }
}

/// <summary>Per-type cache of the registered <see cref="ComponentTypeId" />.</summary>
public static class ComponentType<T>
{
    public static readonly ComponentTypeId Id = ComponentTypeRegistry.Register<T>();

    public static ComponentTypeInfo Info => ComponentTypeRegistry.GetInfo(Id);
}
