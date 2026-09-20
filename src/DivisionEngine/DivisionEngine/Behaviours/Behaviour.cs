using System.Runtime.InteropServices;

namespace DivisionEngine;

/// <summary>
///     Per-entity logic written as an async method. Each segment between awaits runs as a job with
///     the access declared at the await; segments of one behaviour never overlap (turn-based
///     concurrency), segments of different behaviours run in parallel by default.
///     Writes are buffered into rounds and committed at the end of the phase (see <see cref="Round" />).
/// </summary>
///     <para>
///         A behaviour is also a managed component: adding one to an entity is what makes it run, and
///         <see cref="BehaviourStartSystem" /> starts any that is not already running. That is what
///         lets behaviours come back after a scene load or a script reload — a turn's suspended state
///         cannot be carried across either, so what is saved is "this entity has this behaviour", and
///         it starts again from the top. Durable state belongs in ordinary components.
///     </para>
///     Serializing contributes nothing by default; a behaviour with fields worth saving marks itself
///     <c>[AutoSerialization]</c>, whose generated implementation takes over.
/// </summary>
public abstract class Behaviour : ISerializable
{
    public BehaviourContext? Context { get; internal set; }

    /// <summary>True while a turn of this behaviour is running and has not been cancelled.</summary>
    public bool IsRunning => Context is { IsCancelled: false };

    void ISerializable.Serialize<T>(ref T serializer)
    {
    }

    void ISerializable.Deserialize<T>(ref T deserializer)
    {
    }

    /// <summary>The behaviour's logic. Started by <see cref="JobGraph.Start" /> inside the first segment.</summary>
    protected abstract BehaviourTask Run(BehaviourContext context);

    internal BehaviourTask InvokeRun(BehaviourContext context)
    {
        return Run(context);
    }
}

/// <summary>Wraps an exception thrown by a behaviour; rethrown on the main thread by the wait that observes it.</summary>
public sealed class BehaviourFailedException(string behaviour, Exception inner)
    : Exception($"Behaviour '{behaviour}' failed. See InnerException.", inner)
{
    public string Behaviour { get; } = behaviour;
}

/// <summary>
///     The running behaviour's view of the engine: which entity it belongs to, and the awaitables
///     that declare access (<see cref="Read{T}" />, <see cref="Write{T}" />, <see cref="Access" />),
///     wait for a phase, or run work in the background.
/// </summary>
public sealed class BehaviourContext
{
    [ThreadStatic] internal static BehaviourContext? Current;

    private volatile bool _cancelled;
    private int _externalSequence;
    private int _writeSequence;

    internal BehaviourContext(JobGraph graph, Entity entity, Behaviour behaviour, int turnId, string name)
    {
        Graph = graph;
        Entity = entity;
        Behaviour = behaviour;
        TurnId = turnId;
        Name = name;
    }

    public JobGraph Graph { get; }
    public World World => Graph.World;
    public Entity Entity { get; }
    public Behaviour Behaviour { get; }

    /// <summary>Deterministic ordering key: behaviours started earlier resume earlier and commit earlier.</summary>
    public int TurnId { get; }

    public string Name { get; }

    /// <summary>The run's completion state; available once the first segment has executed.</summary>
    public BehaviourTask Task { get; private set; }

    public bool IsCancelled => _cancelled;

    public bool IsAlive => World.IsAlive(Entity);

    /// <summary>The round this turn is in for the current phase (0 = has not written yet).</summary>
    public int CurrentRound { get; internal set; }

    internal TaskNode? CurrentNode { get; private set; }
    internal bool InPhase { get; set; }
    internal bool ContinuedInPhase { get; set; }
    internal int SegmentsThisPhase { get; set; }

    /// <summary>This turn's buffered writes in the current phase, for read-your-own-writes.</summary>
    internal List<PendingWrite> OwnWrites { get; } = new();

    /// <summary>Stops the behaviour: pending continuations are dropped when they would resume.</summary>
    public void Cancel()
    {
        _cancelled = true;
    }

    // --------------------------------------------------------------- awaitables

    /// <summary>Declares access to several entities' components for the next segment.</summary>
    public EntityAccessBuilder Access()
    {
        return new EntityAccessBuilder(this);
    }

    /// <summary>
    ///     Reads a copy of <typeparamref name="T" /> of <paramref name="target" /> as of <paramref name="round" />
    ///     (default: initial, which never waits). Throws <see cref="EntityNotAliveException" /> if the entity is gone.
    /// </summary>
    public ReadAwaitable<T> Read<T>(Entity target, Round? round = null) where T : unmanaged
    {
        return new ReadAwaitable<T>(this, target, round ?? Round.Initial);
    }

    /// <summary>Like <see cref="Read{T}" /> but yields null when the entity or component is gone.</summary>
    public TryReadAwaitable<T> TryRead<T>(Entity target, Round? round = null) where T : unmanaged
    {
        return new TryReadAwaitable<T>(this, target, round ?? Round.Initial);
    }

    /// <summary>
    ///     A buffered write to <typeparamref name="T" /> of <paramref name="target" /> in <paramref name="round" />
    ///     (default: main). The buffer starts from the value this turn would read and is committed at the end of the phase.
    /// </summary>
    public WriteAwaitable<T> Write<T>(Entity target, Round? round = null) where T : unmanaged
    {
        return new WriteAwaitable<T>(this, target, round ?? Round.Main);
    }

    /// <summary>
    ///     Records a modification applied at commit time to the value current then, in (turn, sequence)
    ///     order with the other writes of the round. The delegate must be pure. Does not end the segment.
    /// </summary>
    public void Modify<T>(Entity target, Func<T, T> modify, Round? round = null) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(modify);
        if (Current != this)
        {
            throw new InvalidOperationException("Modify must be called from inside one of the behaviour's own segments.");
        }

        var write = Graph.RecordWrite(this, round ?? Round.Main,
            index => PendingWrite.ForModify(TurnId, NextWriteSequence(), target, index, modify));
        OwnWrites.Add(write);
    }

    /// <summary>Parks until the phase is next resumed by the loop.</summary>
    public PhaseAwaitable Phase(PhaseId phase)
    {
        return new PhaseAwaitable(this, phase);
    }

    /// <summary>Runs <paramref name="work" /> on the pool with no data access; the result arrives at the next phase.</summary>
    public BackgroundAwaitable<T> RunBackground<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return new BackgroundAwaitable<T>(this, work);
    }

    // ------------------------------------------------------------------ segments

    internal int NextWriteSequence()
    {
        return ++_writeSequence;
    }

    /// <summary>Per-turn counter for external awaits; assigned inside the segment, so it forms a timing-independent key.</summary>
    internal int NextExternalSequence()
    {
        return ++_externalSequence;
    }

    internal void RunFirstSegment()
    {
        Task = Behaviour.InvokeRun(this);
    }

    internal void RunSegment(TaskNode node, Action continuation, EntityAccess? handle)
    {
        ContinuedInPhase = false;
        if (_cancelled || !World.IsAlive(Entity))
        {
            _cancelled = true;
            Graph.OnSegmentEnded(this);
            return;
        }

        CurrentNode = node;
        Current = this;
        try
        {
            handle?.Activate();
            continuation();
        }
        finally
        {
            handle?.Deactivate();
            Current = null;
            Graph.OnSegmentEnded(this);
        }
    }

    public override string ToString()
    {
        return $"{Name}@{Entity}";
    }
}

/// <summary>
///     The access a segment declared, usable only while that segment runs. Reads return the value
///     as of the handle's read round plus the turn's own pending writes; writes go to a buffer
///     committed at the end of the phase.
/// </summary>
public sealed class EntityAccess
{
    private const int MaxStackBytes = 1024;
    private readonly BehaviourContext _context;
    private Dictionary<(int entity, int type), PendingWrite>? _writes;
    private volatile bool _active;
    private Exception? _activationError;

    internal EntityAccess(BehaviourContext context, AccessSet access, Round readRound, Round writeRound)
    {
        _context = context;
        Access = access;
        ReadRound = readRound;
        WriteRound = writeRound;
    }

    public World World => _context.World;
    public AccessSet Access { get; }
    public Round ReadRound { get; private set; }
    public Round WriteRound { get; }

    internal int ReadRoundIndex { get; private set; }
    internal int WriteRoundIndex { get; private set; }

    /// <summary>The component's value as this turn sees it (read round, then own pending writes).</summary>
    public T Get<T>(Entity entity) where T : unmanaged
    {
        var info = ComponentType<T>.Info;
        Check(entity, info.Id, false);
        if (_writes is not null && _writes.TryGetValue((entity.Index, info.Id.Value), out var pending))
        {
            return MemoryMarshal.Read<T>(pending.Value);
        }

        Span<byte> buffer = info.Size <= MaxStackBytes ? stackalloc byte[info.Size] : new byte[info.Size];
        World.CopyComponentUnchecked(entity, info, buffer);
        _context.Graph.ReadOverlay(_context, entity, info, ReadRoundIndex, buffer);
        return MemoryMarshal.Read<T>(buffer);
    }

    public bool TryGet<T>(Entity entity, out T value) where T : unmanaged
    {
        Check(entity, ComponentType<T>.Id, false);
        if (!World.IsAlive(entity) || !World.HasComponent<T>(entity))
        {
            value = default;
            return false;
        }

        value = Get<T>(entity);
        return true;
    }

    /// <summary>Reference into the write buffer for the component; committed at the end of the phase.</summary>
    public ref T Ref<T>(Entity entity) where T : unmanaged
    {
        var info = ComponentType<T>.Info;
        Check(entity, info.Id, true);
        if (_writes is null || !_writes.TryGetValue((entity.Index, info.Id.Value), out var pending))
        {
            // The buffer could not be created at activation: the entity or component is gone.
            if (!World.IsAlive(entity))
            {
                throw new EntityNotAliveException(entity);
            }

            throw new InvalidOperationException($"{entity} has no {info.Type} component.");
        }

        return ref MemoryMarshal.AsRef<T>(pending.Value.AsSpan());
    }

    internal void ResolveRounds(int readRoundIndex, int writeRoundIndex)
    {
        ReadRoundIndex = readRoundIndex;
        WriteRoundIndex = writeRoundIndex;
    }

    /// <summary>Completed-round readers are re-issued at the next phase, where the committed state is the initial one.</summary>
    internal void DemoteToInitialRead()
    {
        ReadRound = Round.Initial;
    }

    internal void Activate()
    {
        _active = true;
        try
        {
            foreach (var declared in Access.EntityWrites)
            {
                var info = ComponentTypeRegistry.GetInfo(declared.Type);
                if (info.IsManaged || !World.IsAlive(declared.Entity) || !World.HasComponent(declared.Entity, declared.Type))
                {
                    continue;
                }

                var initial = new byte[info.Size];
                World.CopyComponentUnchecked(declared.Entity, info, initial);
                _context.Graph.ReadOverlay(_context, declared.Entity, info, ReadRoundIndex, initial);
                var write = _context.Graph.RecordWrite(_context, WriteRound,
                    index => PendingWrite.ForValue(_context.TurnId, _context.NextWriteSequence(), declared.Entity, info, index, initial));
                _context.OwnWrites.Add(write);
                (_writes ??= new Dictionary<(int entity, int type), PendingWrite>())[(declared.Entity.Index, declared.Type.Value)] = write;
            }
        }
        catch (Exception ex)
        {
            // Surface inside the behaviour (a catchable behaviour failure) rather than as a scheduler error.
            _activationError = ex;
        }
    }

    internal void Deactivate()
    {
        _active = false;
    }

    private void Check(Entity entity, ComponentTypeId type, bool write)
    {
        if (!_active)
        {
            throw new InvalidOperationException("This access handle belongs to a segment that has already ended; await again to declare access.");
        }

        if (_activationError is { } error)
        {
            throw new InvalidOperationException("The segment's write buffers could not be created.", error);
        }

        if (!JobSafety.Enabled)
        {
            return;
        }

        var allowed = write ? Access.CanWriteEntity(entity, type) : Access.CanReadEntity(entity, type);
        if (!allowed)
        {
            throw new JobAccessViolationException(
                $"Segment {(write ? "writes" : "reads")} {entity}.{ComponentTypeRegistry.GetInfo(type).Type.Name} but declared only {Access}.");
        }
    }
}
