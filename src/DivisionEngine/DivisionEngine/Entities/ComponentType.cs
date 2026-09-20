using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;

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
