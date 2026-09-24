using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DivisionEngine;

/// <summary>
///     The set of component types shared by a group of entities, plus the chunk layout derived from
///     it and the chunks holding those entities. Invariant: every chunk except the last is full, so
///     iteration is dense and chunk-parallel work splits evenly.
/// </summary>
public sealed class Archetype
{
    private readonly int[] _slotOfType;

    internal Archetype(int index, ReadOnlySpan<ComponentTypeId> types)
    {
        Index = index;
        Types = ImmutableCollectionsMarshal.AsImmutableArray(types.ToArray());

        var infos = new ComponentTypeInfo[types.Length];
        var offsets = new int[types.Length];
        var managedIndex = new int[types.Length];

        var maxTypeId = -1;
        var managedCount = 0;
        var bytesPerEntity = Unsafe.SizeOf<Entity>();
        for (var slot = 0; slot < types.Length; slot++)
        {
            var info = ComponentTypeRegistry.GetInfo(types[slot]);
            infos[slot] = info;
            maxTypeId = Math.Max(maxTypeId, info.Id.Value);
            managedIndex[slot] = info.IsManaged ? managedCount++ : -1;
            bytesPerEntity += info.Size;
        }

        ManagedTypeCount = managedCount;
        _slotOfType = new int[maxTypeId + 1];
        Array.Fill(_slotOfType, -1);
        for (var slot = 0; slot < types.Length; slot++)
        {
            _slotOfType[types[slot].Value] = slot;
        }

        // Largest capacity whose aligned layout fits in a chunk.
        var capacity = Chunk.SizeInBytes / bytesPerEntity;
        while (capacity > 0 && LayoutSize(infos, offsets, capacity) > Chunk.SizeInBytes)
        {
            capacity--;
        }

        if (capacity == 0)
        {
            throw new InvalidOperationException(
                $"Component set is too large for a {Chunk.SizeInBytes}-byte chunk ({bytesPerEntity} bytes per entity).");
        }

        Capacity = capacity;
        LayoutSize(infos, offsets, capacity); // writes final offsets

        // Frozen rather than copied: the arrays were built here and are not referenced elsewhere.
        Infos = ImmutableCollectionsMarshal.AsImmutableArray(infos);
        Offsets = ImmutableCollectionsMarshal.AsImmutableArray(offsets);
        ManagedIndex = ImmutableCollectionsMarshal.AsImmutableArray(managedIndex);
    }

    /// <summary>Index of this archetype within its world (creation order).</summary>
    public int Index { get; }

    /// <summary>Component types, sorted by id.</summary>
    public ImmutableArray<ComponentTypeId> Types { get; }

    public ImmutableArray<ComponentTypeInfo> Infos { get; }

    /// <summary>Byte offset of each slot's array within a chunk (0 for slots without chunk data).</summary>
    internal ImmutableArray<int> Offsets { get; }

    /// <summary>For each slot, its index among the archetype's managed types, or -1.</summary>
    internal ImmutableArray<int> ManagedIndex { get; }

    internal int ManagedTypeCount { get; }

    /// <summary>Entities per chunk.</summary>
    public int Capacity { get; }

    internal List<Chunk> Chunks { get; } = new();

    internal Dictionary<ComponentTypeId, Archetype> AddEdges { get; } = new();
    internal Dictionary<ComponentTypeId, Archetype> RemoveEdges { get; } = new();

    public int EntityCount { get; internal set; }

    public int ChunkCount => Chunks.Count;

    internal int SlotOf(ComponentTypeId id)
    {
        return (uint)id.Value < (uint)_slotOfType.Length ? _slotOfType[id.Value] : -1;
    }

    public bool Has(ComponentTypeId id)
    {
        return SlotOf(id) >= 0;
    }

    private static int LayoutSize(ComponentTypeInfo[] infos, int[] offsets, int capacity)
    {
        var offset = capacity * Unsafe.SizeOf<Entity>();
        for (var slot = 0; slot < infos.Length; slot++)
        {
            var info = infos[slot];
            if (info.Size == 0)
            {
                offsets[slot] = 0;
                continue;
            }

            offset = (offset + info.Alignment - 1) & ~(info.Alignment - 1);
            offsets[slot] = offset;
            offset += info.Size * capacity;
        }

        return offset;
    }

    public override string ToString()
    {
        return $"Archetype[{string.Join(", ", Infos.Select(i => i.Type.Name))}]";
    }
}

/// <summary>Sorted, distinct component-type set used as the archetype dictionary key.</summary>
internal readonly struct ArchetypeKey(ImmutableArray<ComponentTypeId> types) : IEquatable<ArchetypeKey>
{
    public readonly ImmutableArray<ComponentTypeId> Types = types;

    public bool Equals(ArchetypeKey other)
    {
        return Types.AsSpan().SequenceEqual(other.Types.AsSpan());
    }

    public override bool Equals(object? obj)
    {
        return obj is ArchetypeKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Hash(Types.AsSpan());
    }

    public static int Hash(ReadOnlySpan<ComponentTypeId> types)
    {
        var hash = new HashCode();
        foreach (var t in types)
        {
            hash.Add(t.Value);
        }

        return hash.ToHashCode();
    }

    /// <summary>Lets the archetype dictionary be probed with a span without allocating a key.</summary>
    public sealed class Comparer : IEqualityComparer<ArchetypeKey>,
        IAlternateEqualityComparer<ReadOnlySpan<ComponentTypeId>, ArchetypeKey>
    {
        public static readonly Comparer Instance = new();

        public ArchetypeKey Create(ReadOnlySpan<ComponentTypeId> alternate)
        {
            return new ArchetypeKey(ImmutableCollectionsMarshal.AsImmutableArray(alternate.ToArray()));
        }

        public bool Equals(ReadOnlySpan<ComponentTypeId> alternate, ArchetypeKey other)
        {
            return alternate.SequenceEqual(other.Types.AsSpan());
        }

        public int GetHashCode(ReadOnlySpan<ComponentTypeId> alternate)
        {
            return Hash(alternate);
        }

        public bool Equals(ArchetypeKey x, ArchetypeKey y)
        {
            return x.Equals(y);
        }

        public int GetHashCode(ArchetypeKey obj)
        {
            return obj.GetHashCode();
        }
    }
}