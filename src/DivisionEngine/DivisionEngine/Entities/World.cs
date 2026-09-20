using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DivisionEngine;

/// <summary>Thrown when an operation targets an entity handle that is null, stale, or deferred.</summary>
public sealed class EntityNotAliveException(Entity entity)
    : InvalidOperationException($"{entity} is not alive in this world.")
{
    public Entity Entity { get; } = entity;
}

/// <summary>
///     Entity storage: a single id space, archetype/chunk storage for unmanaged components and
///     per-chunk reference arrays for managed components, with structural changes applied
///     immediately. Not thread-safe; concurrent access is scheduled by the job system
///     (Notes/Core/JobSystem.md), and deferred structural changes go through
///     <see cref="EntityCommandBuffer" />.
/// </summary>
public sealed class World : IDisposable
{
    private const int MaxStackTypes = 64;

    private readonly Dictionary<ArchetypeKey, Archetype>.AlternateLookup<ReadOnlySpan<ComponentTypeId>> _archetypeLookup;
    private readonly List<Archetype> _archetypes = new();
    private readonly Dictionary<ArchetypeKey, Archetype> _archetypesByKey = new(ArchetypeKey.Comparer.Instance);
    private readonly Stack<int> _freeIndices = new();
    private readonly Dictionary<QueryDescription, EntityQuery> _queries = new();
    private readonly Archetype _rootArchetype;
    private bool _disposed;
    private EntityLocation[] _locations = new EntityLocation[256];
    private int _nextIndex;
    private int[] _versions = new int[256];

    public World()
    {
        _archetypeLookup = _archetypesByKey.GetAlternateLookup<ReadOnlySpan<ComponentTypeId>>();
        _rootArchetype = GetOrCreateArchetype([]);
    }

    public int EntityCount { get; private set; }

    public IReadOnlyList<Archetype> Archetypes => _archetypes;

    /// <summary>Incremented on every structural change; used to detect changes during iteration.</summary>
    public int StructuralVersion { get; private set; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var archetype in _archetypes)
        {
            foreach (var chunk in archetype.Chunks)
            {
                chunk.Free();
            }

            archetype.Chunks.Clear();
        }
    }

    // ---------------------------------------------------------------- entities

    public Entity CreateEntity()
    {
        return CreateEntity(_rootArchetype);
    }

    /// <summary>Creates an entity whose components (zero-initialized / null) are exactly <paramref name="types" />.</summary>
    public Entity CreateEntity(params ReadOnlySpan<ComponentTypeId> types)
    {
        return CreateEntity(GetOrCreateArchetypeUnsorted(types));
    }

    private Entity CreateEntity(Archetype archetype)
    {
        ThrowIfDisposed();
        JobSafety.AssertWrite(ResourceId.Structure);
        if (!_freeIndices.TryPop(out var index))
        {
            index = _nextIndex++;
            if (index == _versions.Length)
            {
                Array.Resize(ref _versions, index * 2);
                Array.Resize(ref _locations, index * 2);
            }

            _versions[index] = 1;
        }

        var entity = new Entity(index, _versions[index]);
        var (chunk, slot) = AllocateSlot(archetype);
        chunk.SetEntity(slot, entity);
        chunk.ClearData(slot);
        _locations[index] = new EntityLocation(chunk, slot);
        EntityCount++;
        StructuralVersion++;
        return entity;
    }

    public void DestroyEntity(Entity entity)
    {
        JobSafety.AssertWrite(ResourceId.Structure);
        ref var location = ref GetLocation(entity);
        RemoveFromChunk(location.Chunk!, location.Index);
        location = default;
        var version = _versions[entity.Index] + 1;
        _versions[entity.Index] = version == 0 ? 1 : version;
        _freeIndices.Push(entity.Index);
        EntityCount--;
        StructuralVersion++;
    }

    public bool IsAlive(Entity entity)
    {
        return (uint)entity.Index < (uint)_nextIndex
               && entity.Version != 0
               && _versions[entity.Index] == entity.Version
               && _locations[entity.Index].Chunk is not null;
    }

    public Archetype GetArchetype(Entity entity)
    {
        return GetLocation(entity).Chunk!.Archetype;
    }

    // -------------------------------------------------------------- components

    public bool HasComponent<T>(Entity entity)
    {
        return HasComponent(entity, ComponentType<T>.Id);
    }

    public bool HasComponent(Entity entity, ComponentTypeId type)
    {
        return GetLocation(entity).Chunk!.Archetype.Has(type);
    }

    /// <summary>Adds an unmanaged component. For classes use <see cref="AddManagedComponent{T}" />.</summary>
    public void AddComponent<T>(Entity entity, in T value = default) where T : unmanaged
    {
        var info = ComponentType<T>.Info;
        ref var location = ref GetLocation(entity);
        AddToArchetype(entity, ref location, info);
        if (info.HasChunkData)
        {
            location.Chunk!.GetRef<T>(location.Chunk.Archetype.SlotOf(info.Id), location.Index) = value;
        }
    }

    /// <summary>Non-generic form of <see cref="AddComponent{T}" />; <paramref name="value" /> must be exactly the component's size.</summary>
    public void AddComponent(Entity entity, ComponentTypeId type, ReadOnlySpan<byte> value)
    {
        var info = ComponentTypeRegistry.GetInfo(type);
        ThrowIfManaged(info);
        ThrowIfWrongSize(info, value);
        ref var location = ref GetLocation(entity);
        AddToArchetype(entity, ref location, info);
        WriteData(location, info, value);
    }

    public void SetComponent<T>(Entity entity, in T value) where T : unmanaged
    {
        GetComponent<T>(entity) = value;
    }

    /// <summary>Copies an unmanaged component's bytes out; <paramref name="destination" /> must be exactly the component's size.</summary>
    public void CopyComponent(Entity entity, ComponentTypeId type, Span<byte> destination)
    {
        var info = ComponentTypeRegistry.GetInfo(type);
        JobSafety.AssertRead(ResourceId.Component(type));
        ThrowIfManaged(info);
        ThrowIfWrongSize(info, destination);
        ref var location = ref GetLocation(entity);
        ThrowIfMissing(location, info);
        CopyComponentUnchecked(location, info, destination);
    }

    /// <summary>Component bytes without the type-level safety check; callers have verified entity-level access.</summary>
    internal unsafe void CopyComponentUnchecked(Entity entity, ComponentTypeInfo info, Span<byte> destination)
    {
        ref var location = ref GetLocation(entity);
        ThrowIfMissing(location, info);
        CopyComponentUnchecked(location, info, destination);
    }

    private static unsafe void CopyComponentUnchecked(in EntityLocation location, ComponentTypeInfo info, Span<byte> destination)
    {
        if (info.Size == 0)
        {
            return;
        }

        var chunk = location.Chunk!;
        new ReadOnlySpan<byte>(chunk.GetPointer(chunk.Archetype.SlotOf(info.Id), location.Index), info.Size).CopyTo(destination);
    }

    public void SetComponent(Entity entity, ComponentTypeId type, ReadOnlySpan<byte> value)
    {
        var info = ComponentTypeRegistry.GetInfo(type);
        JobSafety.AssertWrite(ResourceId.Component(type));
        ThrowIfManaged(info);
        ThrowIfWrongSize(info, value);
        ref var location = ref GetLocation(entity);
        ThrowIfMissing(location, info);
        WriteData(location, info, value);
    }

    /// <summary>
    ///     Returns a writable reference to an unmanaged component (requires write access inside a
    ///     job). The reference is invalidated by any structural change.
    /// </summary>
    public ref T GetComponent<T>(Entity entity) where T : unmanaged
    {
        var info = ComponentType<T>.Info;
        JobSafety.AssertWrite(ResourceId.Component(info.Id));
        return ref GetComponentRef<T>(entity, info);
    }

    /// <summary>Read-only counterpart of <see cref="GetComponent{T}" /> (requires read access inside a job).</summary>
    public ref readonly T GetComponentReadOnly<T>(Entity entity) where T : unmanaged
    {
        var info = ComponentType<T>.Info;
        JobSafety.AssertRead(ResourceId.Component(info.Id));
        return ref GetComponentRef<T>(entity, info);
    }

    /// <summary>Component reference without the type-level safety check; callers have verified entity-level access.</summary>
    internal ref T GetComponentRefUnchecked<T>(Entity entity) where T : unmanaged
    {
        return ref GetComponentRef<T>(entity, ComponentType<T>.Info);
    }

    private ref T GetComponentRef<T>(Entity entity, ComponentTypeInfo info) where T : unmanaged
    {
        ThrowIfManaged(info);
        if (info.IsTag)
        {
            throw new InvalidOperationException($"{info.Type} is a tag component and has no data.");
        }

        ref var location = ref GetLocation(entity);
        var slot = location.Chunk!.Archetype.SlotOf(info.Id);
        if (slot < 0)
        {
            throw new InvalidOperationException($"{entity} has no {info.Type} component.");
        }

        return ref location.Chunk.GetRef<T>(slot, location.Index);
    }

    public void RemoveComponent<T>(Entity entity)
    {
        RemoveComponent(entity, ComponentType<T>.Id);
    }

    public void RemoveComponent(Entity entity, ComponentTypeId type)
    {
        ref var location = ref GetLocation(entity);
        var source = location.Chunk!.Archetype;
        if (!source.Has(type))
        {
            throw new InvalidOperationException($"{entity} has no {ComponentTypeRegistry.GetInfo(type).Type} component.");
        }

        MoveEntity(entity, ref location, ArchetypeWithRemoved(source, type));
    }

    public void AddManagedComponent<T>(Entity entity, T value) where T : class
    {
        AddManagedComponent(entity, ComponentType<T>.Id, value);
    }

    public void AddManagedComponent(Entity entity, ComponentTypeId type, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var info = ComponentTypeRegistry.GetInfo(type);
        ThrowIfNotManaged(info, value);
        ref var location = ref GetLocation(entity);
        AddToArchetype(entity, ref location, info);
        WriteManaged(location, info, value);
    }

    public T GetManagedComponent<T>(Entity entity) where T : class
    {
        var info = ComponentType<T>.Info;
        JobSafety.AssertRead(ResourceId.Component(info.Id));
        ThrowIfNotManaged(info, null);
        ref var location = ref GetLocation(entity);
        var slot = location.Chunk!.Archetype.SlotOf(info.Id);
        if (slot < 0)
        {
            throw new InvalidOperationException($"{entity} has no {info.Type} component.");
        }

        return (T)location.Chunk.GetManagedArray(slot)[location.Index]!;
    }

    public void SetManagedComponent<T>(Entity entity, T value) where T : class
    {
        SetManagedComponent(entity, ComponentType<T>.Id, value);
    }

    public void SetManagedComponent(Entity entity, ComponentTypeId type, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var info = ComponentTypeRegistry.GetInfo(type);
        JobSafety.AssertWrite(ResourceId.Component(type));
        ThrowIfNotManaged(info, value);
        ref var location = ref GetLocation(entity);
        ThrowIfMissing(location, info);
        WriteManaged(location, info, value);
    }

    // ------------------------------------------------------------------ queries

    public QueryBuilder Query()
    {
        return new QueryBuilder(this);
    }

    public EntityQuery GetQuery(QueryDescription description)
    {
        ThrowIfDisposed();
        if (!_queries.TryGetValue(description, out var query))
        {
            query = new EntityQuery(this, description);
            _queries.Add(description, query);
        }

        return query;
    }

    // --------------------------------------------------------------- archetypes

    internal Archetype GetOrCreateArchetype(ReadOnlySpan<ComponentTypeId> sortedTypes)
    {
        if (_archetypeLookup.TryGetValue(sortedTypes, out var archetype))
        {
            return archetype;
        }

        archetype = new Archetype(_archetypes.Count, sortedTypes.ToArray());
        _archetypesByKey.Add(new ArchetypeKey(archetype.Types), archetype);
        _archetypes.Add(archetype);
        return archetype;
    }

    private Archetype GetOrCreateArchetypeUnsorted(ReadOnlySpan<ComponentTypeId> types)
    {
        ThrowIfDisposed();
        if (types.Length == 0)
        {
            return _rootArchetype;
        }

        Span<ComponentTypeId> sorted = types.Length <= MaxStackTypes
            ? stackalloc ComponentTypeId[types.Length]
            : new ComponentTypeId[types.Length];
        types.CopyTo(sorted);
        SortByValue(sorted);

        // Dedupe in place.
        var write = 1;
        for (var read = 1; read < sorted.Length; read++)
        {
            if (sorted[read] != sorted[write - 1])
            {
                sorted[write++] = sorted[read];
            }
        }

        return GetOrCreateArchetype(sorted[..write]);
    }

    private Archetype ArchetypeWithAdded(Archetype source, ComponentTypeId type)
    {
        if (source.AddEdges.TryGetValue(type, out var target))
        {
            return target;
        }

        var count = source.Types.Length + 1;
        Span<ComponentTypeId> types = count <= MaxStackTypes ? stackalloc ComponentTypeId[count] : new ComponentTypeId[count];
        var write = 0;
        var inserted = false;
        foreach (var existing in source.Types)
        {
            if (!inserted && type.Value < existing.Value)
            {
                types[write++] = type;
                inserted = true;
            }

            types[write++] = existing;
        }

        if (!inserted)
        {
            types[write] = type;
        }

        target = GetOrCreateArchetype(types);
        source.AddEdges[type] = target;
        target.RemoveEdges[type] = source;
        return target;
    }

    private Archetype ArchetypeWithRemoved(Archetype source, ComponentTypeId type)
    {
        if (source.RemoveEdges.TryGetValue(type, out var target))
        {
            return target;
        }

        var count = source.Types.Length - 1;
        Span<ComponentTypeId> types = count <= MaxStackTypes ? stackalloc ComponentTypeId[count] : new ComponentTypeId[count];
        var write = 0;
        foreach (var existing in source.Types)
        {
            if (existing != type)
            {
                types[write++] = existing;
            }
        }

        target = GetOrCreateArchetype(types);
        source.RemoveEdges[type] = target;
        target.AddEdges[type] = source;
        return target;
    }

    private static void SortByValue(Span<ComponentTypeId> types)
    {
        // Insertion sort: type lists are short.
        for (var i = 1; i < types.Length; i++)
        {
            var current = types[i];
            var j = i - 1;
            while (j >= 0 && types[j].Value > current.Value)
            {
                types[j + 1] = types[j];
                j--;
            }

            types[j + 1] = current;
        }
    }

    // ------------------------------------------------------------ chunk storage

    private static (Chunk chunk, int index) AllocateSlot(Archetype archetype)
    {
        var chunks = archetype.Chunks;
        Chunk chunk;
        if (chunks.Count == 0 || chunks[^1].IsFull)
        {
            chunk = new Chunk(archetype);
            chunks.Add(chunk);
        }
        else
        {
            chunk = chunks[^1];
        }

        var index = chunk.Count;
        chunk.Count = index + 1;
        archetype.EntityCount++;
        return (chunk, index);
    }

    /// <summary>
    ///     Vacates a slot, keeping the archetype dense: the chunk's last entity fills the hole, and if
    ///     the chunk is not the archetype's last chunk, the last chunk's last entity moves in so that
    ///     only the last chunk is ever partially filled. Empty trailing chunks are freed.
    /// </summary>
    private void RemoveFromChunk(Chunk chunk, int index)
    {
        var archetype = chunk.Archetype;
        var chunks = archetype.Chunks;

        var last = chunk.Count - 1;
        if (index != last)
        {
            chunk.CopyWithin(last, index);
            _locations[chunk.Entities[index].Index] = new EntityLocation(chunk, index);
        }

        chunk.ClearManaged(last);
        chunk.Count = last;
        archetype.EntityCount--;

        var lastChunk = chunks[^1];
        if (chunk != lastChunk)
        {
            var sourceIndex = lastChunk.Count - 1;
            var targetIndex = chunk.Count;
            Chunk.CopyAcross(lastChunk, sourceIndex, chunk, targetIndex);
            chunk.Count = targetIndex + 1;
            _locations[chunk.Entities[targetIndex].Index] = new EntityLocation(chunk, targetIndex);
            lastChunk.ClearManaged(sourceIndex);
            lastChunk.Count = sourceIndex;
        }

        if (lastChunk.Count == 0)
        {
            chunks.RemoveAt(chunks.Count - 1);
            lastChunk.Free();
        }
    }

    private void MoveEntity(Entity entity, ref EntityLocation location, Archetype target)
    {
        JobSafety.AssertWrite(ResourceId.Structure);
        var sourceChunk = location.Chunk!;
        var sourceIndex = location.Index;
        var (targetChunk, targetIndex) = AllocateSlot(target);
        Chunk.CopyAcross(sourceChunk, sourceIndex, targetChunk, targetIndex);
        location = new EntityLocation(targetChunk, targetIndex);
        RemoveFromChunk(sourceChunk, sourceIndex);
        StructuralVersion++;
    }

    private void AddToArchetype(Entity entity, ref EntityLocation location, ComponentTypeInfo info)
    {
        var source = location.Chunk!.Archetype;
        if (source.Has(info.Id))
        {
            throw new InvalidOperationException($"{entity} already has a {info.Type} component.");
        }

        MoveEntity(entity, ref location, ArchetypeWithAdded(source, info.Id));
    }

    private static unsafe void WriteData(in EntityLocation location, ComponentTypeInfo info, ReadOnlySpan<byte> value)
    {
        if (info.Size == 0)
        {
            return;
        }

        var chunk = location.Chunk!;
        var destination = new Span<byte>(chunk.GetPointer(chunk.Archetype.SlotOf(info.Id), location.Index), info.Size);
        value.CopyTo(destination);
    }

    private static void WriteManaged(in EntityLocation location, ComponentTypeInfo info, object value)
    {
        var chunk = location.Chunk!;
        chunk.GetManagedArray(chunk.Archetype.SlotOf(info.Id))[location.Index] = value;
    }

    // -------------------------------------------------------------- validation

    private ref EntityLocation GetLocation(Entity entity)
    {
        ThrowIfDisposed();
        JobSafety.AssertRead(ResourceId.Structure);
        if (!IsAlive(entity))
        {
            throw new EntityNotAliveException(entity);
        }

        return ref _locations[entity.Index];
    }

    private static void ThrowIfMissing(in EntityLocation location, ComponentTypeInfo info)
    {
        if (!location.Chunk!.Archetype.Has(info.Id))
        {
            throw new InvalidOperationException($"{location.Chunk.Entities[location.Index]} has no {info.Type} component.");
        }
    }

    private static void ThrowIfManaged(ComponentTypeInfo info)
    {
        if (info.IsManaged)
        {
            throw new ArgumentException($"{info.Type} is a managed component; use the *ManagedComponent methods.");
        }
    }

    private static void ThrowIfNotManaged(ComponentTypeInfo info, object? value)
    {
        if (!info.IsManaged)
        {
            throw new ArgumentException($"{info.Type} is an unmanaged component; use the non-managed methods.");
        }

        if (value is not null && !info.Type.IsInstanceOfType(value))
        {
            throw new ArgumentException($"Value of type {value.GetType()} is not a {info.Type}.");
        }
    }

    private static void ThrowIfWrongSize(ComponentTypeInfo info, ReadOnlySpan<byte> value)
    {
        if (value.Length != info.Size)
        {
            throw new ArgumentException($"{info.Type} is {info.Size} bytes but {value.Length} were given.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private readonly struct EntityLocation(Chunk chunk, int index)
    {
        public readonly Chunk? Chunk = chunk;
        public readonly int Index = index;
    }
}
