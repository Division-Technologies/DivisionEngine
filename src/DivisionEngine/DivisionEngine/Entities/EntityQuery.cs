using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace DivisionEngine;

/// <summary>
///     Which archetypes a query matches: all of <see cref="All" />, none of <see cref="None" />, and (if non-empty)
///     at least one of <see cref="Any" />.
/// </summary>
public readonly struct QueryDescription : IEquatable<QueryDescription>
{
    internal QueryDescription(
        ImmutableArray<ComponentTypeId> all,
        ImmutableArray<ComponentTypeId> any,
        ImmutableArray<ComponentTypeId> none)
    {
        All = all;
        Any = any;
        None = none;
    }

    public ImmutableArray<ComponentTypeId> All { get; }
    public ImmutableArray<ComponentTypeId> Any { get; }
    public ImmutableArray<ComponentTypeId> None { get; }

    /// <summary>
    ///     Builds a description from explicit type lists, normalizing each so that two descriptions
    ///     naming the same types compare equal and hit the same cached query. Used by the code
    ///     generated for <c>[EntityJob]</c>, which knows its types at compile time and so can build
    ///     the description once into a static field rather than through the fluent builder.
    /// </summary>
    public static QueryDescription Create(
        ReadOnlySpan<ComponentTypeId> all,
        ReadOnlySpan<ComponentTypeId> any = default,
        ReadOnlySpan<ComponentTypeId> none = default)
    {
        return new QueryDescription(Normalize(all), Normalize(any), Normalize(none));
    }

    private static ImmutableArray<ComponentTypeId> Normalize(ReadOnlySpan<ComponentTypeId> types)
    {
        if (types.Length == 0)
        {
            return [];
        }

        var sorted = types.ToArray();
        Array.Sort(sorted, static (a, b) => a.Value.CompareTo(b.Value));

        var write = 1;
        for (var read = 1; read < sorted.Length; read++)
        {
            if (sorted[read] != sorted[write - 1])
            {
                sorted[write++] = sorted[read];
            }
        }

        return ImmutableCollectionsMarshal.AsImmutableArray(write == sorted.Length ? sorted : sorted[..write]);
    }

    public bool Matches(Archetype archetype)
    {
        foreach (var type in All)
        {
            if (!archetype.Has(type))
            {
                return false;
            }
        }

        foreach (var type in None)
        {
            if (archetype.Has(type))
            {
                return false;
            }
        }

        if (Any.Length == 0)
        {
            return true;
        }

        foreach (var type in Any)
        {
            if (archetype.Has(type))
            {
                return true;
            }
        }

        return false;
    }

    public bool Equals(QueryDescription other)
    {
        return All.AsSpan().SequenceEqual(other.All.AsSpan())
               && Any.AsSpan().SequenceEqual(other.Any.AsSpan())
               && None.AsSpan().SequenceEqual(other.None.AsSpan());
    }

    public override bool Equals(object? obj)
    {
        return obj is QueryDescription other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(ArchetypeKey.Hash(All.AsSpan()), ArchetypeKey.Hash(Any.AsSpan()),
            ArchetypeKey.Hash(None.AsSpan()));
    }
}

/// <summary>
///     Fluent builder for <see cref="EntityQuery" />; <see cref="Build" /> returns the world's cached query for the
///     description.
/// </summary>
/// <remarks>
///     A value: each call returns a new builder and leaves the one it was called on as it was, so a
///     partly built query can be branched (<c>var b = q.With&lt;A&gt;(); b.With&lt;B&gt;()</c> does not
///     change <c>b</c>).
/// </remarks>
public readonly struct QueryBuilder
{
    private readonly World _world;
    private readonly ImmutableArray<ComponentTypeId> _all;
    private readonly ImmutableArray<ComponentTypeId> _any;
    private readonly ImmutableArray<ComponentTypeId> _none;

    internal QueryBuilder(World world) : this(world, [], [], [])
    {
    }

    private QueryBuilder(World world, ImmutableArray<ComponentTypeId> all, ImmutableArray<ComponentTypeId> any,
        ImmutableArray<ComponentTypeId> none)
    {
        _world = world;
        _all = all;
        _any = any;
        _none = none;
    }

    public QueryBuilder With<T>()
    {
        return new QueryBuilder(_world, _all.Add(ComponentType<T>.Id), _any, _none);
    }

    public QueryBuilder WithAny<T>()
    {
        return new QueryBuilder(_world, _all, _any.Add(ComponentType<T>.Id), _none);
    }

    public QueryBuilder Without<T>()
    {
        return new QueryBuilder(_world, _all, _any, _none.Add(ComponentType<T>.Id));
    }

    public EntityQuery Build()
    {
        return _world.GetQuery(new QueryDescription(Normalize(_all), Normalize(_any), Normalize(_none)));
    }

    private static ImmutableArray<ComponentTypeId> Normalize(ImmutableArray<ComponentTypeId> types)
    {
        if (types.IsDefaultOrEmpty)
        {
            return [];
        }

        return [.. types.Distinct().OrderBy(t => t.Value)];
    }
}

/// <summary>
///     A cached set of matching archetypes. Enumerating yields <see cref="ArchetypeChunk" />s;
///     structural changes during enumeration are detected and rejected.
/// </summary>
public sealed class EntityQuery
{
    private readonly Lock _lock = new();
    private readonly List<Archetype> _matched = new();
    private int _scannedArchetypes;

    internal EntityQuery(World world, QueryDescription description)
    {
        World = world;
        Description = description;
    }

    public World World { get; }
    public QueryDescription Description { get; }

    /// <summary>
    ///     Matching archetypes, extended lazily with archetypes created since the last call. Safe
    ///     to call from several jobs at once (they only ever run while no structural change does).
    /// </summary>
    internal List<Archetype> MatchedArchetypes
    {
        get
        {
            var archetypes = World.Archetypes;
            if (_scannedArchetypes == archetypes.Count)
            {
                return _matched;
            }

            lock (_lock)
            {
                for (; _scannedArchetypes < archetypes.Count; _scannedArchetypes++)
                {
                    var archetype = archetypes[_scannedArchetypes];
                    if (Description.Matches(archetype))
                    {
                        _matched.Add(archetype);
                    }
                }
            }

            return _matched;
        }
    }

    /// <summary>
    ///     Forgets the archetypes matched so far, so the next use rescans. Called when the world drops
    ///     its archetypes (<see cref="World.Clear" />); the query object itself stays alive because
    ///     systems hold on to it across a reload.
    /// </summary>
    internal void Reset()
    {
        lock (_lock)
        {
            _matched.Clear();
            _scannedArchetypes = 0;
        }
    }

    /// <summary>Snapshot of the chunks currently matched, in archetype then chunk order.</summary>
    internal List<Chunk> CollectChunks(List<Chunk> into)
    {
        foreach (var archetype in MatchedArchetypes)
        {
            into.AddRange(archetype.Chunks);
        }

        return into;
    }

    public int CalculateEntityCount()
    {
        var count = 0;
        foreach (var archetype in MatchedArchetypes)
        {
            count += archetype.EntityCount;
        }

        return count;
    }

    public int CalculateChunkCount()
    {
        var count = 0;
        foreach (var archetype in MatchedArchetypes)
        {
            count += archetype.ChunkCount;
        }

        return count;
    }

    public ChunkEnumerator GetEnumerator()
    {
        return new ChunkEnumerator(World, MatchedArchetypes);
    }

    public struct ChunkEnumerator
    {
        private readonly List<Archetype> _archetypes;
        private readonly World _world;
        private readonly int _structuralVersion;
        private int _archetypeIndex;
        private int _chunkIndex;

        internal ChunkEnumerator(World world, List<Archetype> archetypes)
        {
            _world = world;
            _archetypes = archetypes;
            _structuralVersion = world.StructuralVersion;
            _archetypeIndex = 0;
            _chunkIndex = -1;
            Current = default;
        }

        public ArchetypeChunk Current { get; private set; }

        public bool MoveNext()
        {
            if (_world.StructuralVersion != _structuralVersion)
            {
                throw new InvalidOperationException(
                    "The world was structurally changed while a query was being enumerated.");
            }

            while (_archetypeIndex < _archetypes.Count)
            {
                var chunks = _archetypes[_archetypeIndex].Chunks;
                _chunkIndex++;
                if (_chunkIndex < chunks.Count)
                {
                    Current = new ArchetypeChunk(chunks[_chunkIndex]);
                    return true;
                }

                _archetypeIndex++;
                _chunkIndex = -1;
            }

            return false;
        }
    }
}

/// <summary>
///     A view of one chunk for iteration. Spans are valid until the next structural change.
/// </summary>
public readonly struct ArchetypeChunk
{
    private readonly Chunk _chunk;

    internal ArchetypeChunk(Chunk chunk)
    {
        _chunk = chunk;
    }

    public Archetype Archetype => _chunk.Archetype;
    public int Count => _chunk.Count;
    public ReadOnlySpan<Entity> Entities => _chunk.Entities;

    public bool Has<T>()
    {
        return _chunk.Archetype.Has(ComponentType<T>.Id);
    }

    /// <summary>Writable view of a component array. Requires write access to <typeparamref name="T" /> inside a job.</summary>
    public Span<T> GetSpan<T>() where T : unmanaged
    {
        var info = ComponentType<T>.Info;
        JobSafety.AssertWrite(ResourceId.Component(info.Id));
        return _chunk.GetSpan<T>(SlotOf(info));
    }

    /// <summary>Read-only view of a component array. Requires read access to <typeparamref name="T" /> inside a job.</summary>
    public ReadOnlySpan<T> GetReadOnlySpan<T>() where T : unmanaged
    {
        var info = ComponentType<T>.Info;
        JobSafety.AssertRead(ResourceId.Component(info.Id));
        return _chunk.GetSpan<T>(SlotOf(info));
    }

    /// <summary>The managed component references of this chunk (replaceable). Elements are non-null for live entities.</summary>
    public Span<T> GetManagedSpan<T>() where T : class
    {
        var info = ComponentType<T>.Info;
        JobSafety.AssertWrite(ResourceId.Component(info.Id));
        return ManagedArray<T>(info).AsSpan(0, _chunk.Count);
    }

    public ReadOnlySpan<T> GetManagedReadOnlySpan<T>() where T : class
    {
        var info = ComponentType<T>.Info;
        JobSafety.AssertRead(ResourceId.Component(info.Id));
        return ManagedArray<T>(info).AsSpan(0, _chunk.Count);
    }

    private T[] ManagedArray<T>(ComponentTypeInfo info) where T : class
    {
        if (!info.IsManaged)
        {
            throw new ArgumentException($"{info.Type} is not a managed component.");
        }

        return (T[])_chunk.GetManagedArray(SlotOfRequired(info));
    }

    private int SlotOf(ComponentTypeInfo info)
    {
        if (!info.HasChunkData)
        {
            throw new ArgumentException(info.IsManaged
                ? $"{info.Type} is a managed component; use GetManagedSpan."
                : $"{info.Type} is a tag component and has no data.");
        }

        return SlotOfRequired(info);
    }

    private int SlotOfRequired(ComponentTypeInfo info)
    {
        var slot = _chunk.Archetype.SlotOf(info.Id);
        if (slot < 0)
        {
            throw new InvalidOperationException($"{_chunk.Archetype} has no {info.Type} component.");
        }

        return slot;
    }
}