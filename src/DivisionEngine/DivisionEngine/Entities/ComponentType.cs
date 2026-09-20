using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
///     Resolves the placeholders an <see cref="EntityCommandBuffer" /> handed out to the entities
///     actually created during playback. Passed to the remappers registered with
///     <see cref="ComponentTypeRegistry.RegisterEntityFields{T}" />.
/// </summary>
public readonly ref struct DeferredEntityMap
{
    private readonly ReadOnlySpan<Entity> _resolved;

    internal DeferredEntityMap(ReadOnlySpan<Entity> resolved)
    {
        _resolved = resolved;
    }

    /// <summary>Returns <paramref name="entity" /> unchanged unless it is a placeholder, in which case the entity it stands for.</summary>
    public Entity Resolve(Entity entity)
    {
        if (!entity.IsDeferred)
        {
            return entity;
        }

        var index = -1 - entity.Index;
        if ((uint)index >= (uint)_resolved.Length)
        {
            throw new InvalidOperationException($"{entity} was not created by the command buffer being played back.");
        }

        var resolved = _resolved[index];
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
public delegate void EntityFieldRemapper<T>(ref T value, DeferredEntityMap map) where T : unmanaged;

internal delegate void RawEntityFieldRemapper(Span<byte> value, DeferredEntityMap map);

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
    }

    public ComponentTypeId Id { get; }
    public Type Type { get; }

    /// <summary>Bytes per instance in chunk storage. 0 for managed and tag components.</summary>
    public int Size { get; }

    public int Alignment { get; }
    public bool IsManaged { get; }
    public bool IsTag { get; }
    public bool HasChunkData => !IsManaged && !IsTag;

    /// <summary>Set when the type stores <see cref="Entity" /> fields that command-buffer playback must remap.</summary>
    internal RawEntityFieldRemapper? EntityRemapper { get; set; }

    public bool HasEntityFields => EntityRemapper is not null;

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
    private static ComponentTypeInfo[] _infos = new ComponentTypeInfo[64];
    private static int _count;

    public static int Count => Volatile.Read(ref _count);

    public static ComponentTypeInfo GetInfo(ComponentTypeId id)
    {
        var infos = Volatile.Read(ref _infos);
        if ((uint)id.Value >= (uint)Volatile.Read(ref _count))
        {
            throw new ArgumentOutOfRangeException(nameof(id), $"Unknown component type id {id.Value}.");
        }

        return infos[id.Value];
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
    internal static void RemapEntityFields(ComponentTypeId type, Span<byte> value, DeferredEntityMap map)
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
                var grown = new ComponentTypeInfo[infos.Length * 2];
                Array.Copy(infos, grown, infos.Length);
                infos = grown;
            }

            var id = new ComponentTypeId(index);
            infos[index] = new ComponentTypeInfo(id, type, size, alignment, isManaged, isTag);
            Volatile.Write(ref _infos, infos);
            Volatile.Write(ref _count, index + 1);
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
