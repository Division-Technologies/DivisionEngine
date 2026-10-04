namespace DivisionEngine;

/// <summary>
///     Thrown when an <see cref="Entity" /> is serialized without an active
///     <see cref="EntitySerializationContext" /> to give it a persistable identity.
/// </summary>
public sealed class EntitySerializationException(string message) : InvalidOperationException(message);

/// <summary>
///     Translates between live <see cref="Entity" /> handles and the ids they are stored under.
///     An entity handle is an index into a world plus a reuse counter, so it means nothing once the
///     world is rebuilt; what gets written instead is a scene-local entity id, and this context is
///     what maps between the two.
///     <para>
///         The context is ambient (per thread) rather than threaded through every call because the
///         serialization layer reaches <see cref="Entity" /> through <see cref="IValueFormatter{T}" />,
///         several frames below the code that knows about worlds. Being ambient also means it works
///         uniformly for an entity sitting in an unmanaged component, in a managed component, or
///         nested inside a list — anywhere a formatter runs.
///     </para>
/// </summary>
public sealed class EntitySerializationContext
{
    /// <summary>The persisted value standing for <see cref="Entity.Null" /> and for unmapped entities.</summary>
    public const int NullId = -1;

    [ThreadStatic] private static EntitySerializationContext? _current;

    private readonly bool _loading;
    private readonly Dictionary<int, Entity> _toLive = new();
    private readonly Dictionary<Entity, int> _toPersistent = new();

    public EntitySerializationContext()
    {
    }

    private EntitySerializationContext(bool loading)
    {
        _loading = loading;
    }

    public static EntitySerializationContext? Current => _current;

    public int Count => _toPersistent.Count;

    /// <summary>
    ///     A context for reading, where the entities do not exist yet. Ids map to placeholder handles
    ///     (<see cref="Entity.IsDeferred" />) which a later pass — <see cref="EntityScene.ApplyTo" /> —
    ///     resolves to the entities it creates. This is the same representation a command buffer uses,
    ///     so one remapping step serves both.
    /// </summary>
    public static EntitySerializationContext ForLoading()
    {
        return new EntitySerializationContext(true);
    }

    /// <summary>The placeholder handle standing for a scene-local entity id.</summary>
    public static Entity PlaceholderFor(int id)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id);
        return new Entity(-1 - id, 0);
    }

    /// <summary>
    ///     Makes this context ambient for the calling thread until the returned scope is disposed.
    ///     Nesting is supported; the previous context is restored.
    /// </summary>
    public Scope Enter()
    {
        var previous = _current;
        _current = this;
        return new Scope(previous);
    }

    /// <summary>Declares that <paramref name="entity" /> is persisted as <paramref name="id" />, in both directions.</summary>
    public void Map(Entity entity, int id)
    {
        MapToPersistent(entity, id);
        MapToLive(id, entity);
    }

    /// <summary>
    ///     Declares only the writing direction. The two directions are separate because copying a
    ///     managed component reads it under one meaning of "entity" and writes it under another —
    ///     capturing turns live handles into placeholders, applying turns placeholders into the
    ///     entities just created.
    /// </summary>
    public void MapToPersistent(Entity entity, int id)
    {
        ThrowIfReserved(id);
        _toPersistent[entity] = id;
    }

    /// <summary>Declares only the reading direction.</summary>
    public void MapToLive(int id, Entity entity)
    {
        ThrowIfReserved(id);
        _toLive[id] = entity;
    }

    private static void ThrowIfReserved(int id)
    {
        if (id < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id),
                $"Entity ids are not negative; {NullId} is reserved for the null entity.");
        }
    }

    /// <summary>
    ///     The id <paramref name="entity" /> is written as. Entities outside the scene being saved —
    ///     a reference to something that was not included — collapse to <see cref="NullId" /> rather
    ///     than writing a handle that would dangle on load.
    /// </summary>
    public int ToPersistent(Entity entity)
    {
        return _toPersistent.TryGetValue(entity, out var id) ? id : NullId;
    }

    /// <summary>The entity an id refers to, or <see cref="Entity.Null" /> if the scene did not contain it.</summary>
    public Entity ToLive(int id)
    {
        if (id < 0)
        {
            return Entity.Null; // no valid id is negative; a corrupt file must not produce a real handle
        }

        if (_loading)
        {
            return PlaceholderFor(id);
        }

        return _toLive.TryGetValue(id, out var entity) ? entity : Entity.Null;
    }

    internal static EntitySerializationContext Require()
    {
        return _current ?? throw new EntitySerializationException(
            "An Entity was (de)serialized with no EntitySerializationContext active. Entity handles are only "
            + "meaningful relative to a scene, so serialize them inside EntitySerializationContext.Enter().");
    }

    public readonly struct Scope(EntitySerializationContext? previous) : IDisposable
    {
        public void Dispose()
        {
            _current = previous;
        }
    }
}

/// <summary>
///     Writes an <see cref="Entity" /> as its scene-local id, taken from the ambient
///     <see cref="EntitySerializationContext" />.
/// </summary>
[CustomFormatter(typeof(Entity))]
internal readonly struct EntityFormatter : IValueFormatter<Entity>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in Entity value)
        where TS : ISerializer, allows ref struct
    {
        s.I32(id, hint, value.IsNull
            ? EntitySerializationContext.NullId
            : EntitySerializationContext.Require().ToPersistent(value));
    }

    public Entity Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct
    {
        var persistent = d.I32(id, hint);
        return persistent == EntitySerializationContext.NullId
            ? Entity.Null
            : EntitySerializationContext.Require().ToLive(persistent);
    }
}