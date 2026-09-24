using System.Collections.Immutable;
using System.Runtime.ExceptionServices;

namespace DivisionEngine;

/// <summary>
///     The issue side of the job system: schedules jobs with declared access sets, infers their
///     dependencies from issue order, and hands ready nodes to the <see cref="JobScheduler" />.
///     Systems issue statically from the main thread; behavior segments issue dynamically from
///     whichever worker they end on. Issues are serialized by a lock.
///     <para>
///         Phases (<see cref="BeginPhase" /> / <see cref="EndPhase" />) bound behavior rounds:
///         behavior writes are buffered into rounds and committed at <see cref="EndPhase" /> in
///         round order then (turn, sequence) order, so the outcome does not depend on timing.
///         Segments that try to issue while a phase or frame is closing are deferred to the next
///         <see cref="BeginPhase" />, which keeps phases bounded.
///     </para>
/// </summary>
public sealed class JobGraph
{
    private readonly List<PendingWrite> _carried = new();

    /// <summary>Turns that recorded a structural change in the open phase, played back at its end.</summary>
    private readonly List<BehaviorContext> _commandRecorders = new();

    private readonly List<Deferred> _completedWaiters = new();

    /// <summary>Phases declared by the loop, and whether each dispatches behaviors. Empty: not checked.</summary>
    private readonly Dictionary<PhaseId, bool> _declaredPhases = new();

    private readonly List<ExternalArrival> _externalArrivals = new();
    private readonly List<Deferred> _intake = new();

    /// <summary>
    ///     Serializes issuing. Profiled rather than a plain <see cref="Lock" /> because it is the
    ///     prime suspect for why fine-grained segments run slower with more workers: whether the
    ///     workers are queued behind this or asleep on a semaphore looks the same in a benchmark
    ///     and different in a capture.
    /// </summary>
    private readonly ProfiledLock _issueLock = new("JobGraph issue");

    private readonly Dictionary<PhaseId, string[]> _labels = new();

    /// <summary>Every turn started and not yet cancelled, so they can all be stopped at once.</summary>
    private readonly HashSet<BehaviorContext> _liveTurns = new();

    private readonly List<List<Parked>> _phaseQueues = new();
    private readonly ResourceTracker _tracker = new();
    private int _checkedStructuralVersion = -1;
    private int _closedUpTo = -1;
    private bool _closing;
    private string[] _currentLabels = [];
    private PhaseId? _currentPhase;

    /// <summary>Whether the open phase resumes behaviors, i.e. whether a segment may be running.</summary>
    private bool _currentPhaseDispatches;

    private bool _entryOpen;
    private HashSet<ComponentTypeId>? _frozen;
    private int _nextTurnId;
    private List<Deferred> _pendingIntake = [];
    private List<Parked> _pendingResume = [];
    private RoundState[] _rounds = [];

    public JobGraph(JobScheduler scheduler, World world)
    {
        Scheduler = scheduler;
        World = world;
    }

    public JobScheduler Scheduler { get; }
    public World World { get; }

    /// <summary>
    ///     The structural changes systems record during the open phase, applied at its end.
    ///     <para>
    ///         Structural changes conflict with every entity access, so they cannot happen while jobs
    ///         are reading chunks; recording them and applying them at the phase boundary is what turns
    ///         "create this entity" into something a parallel job may say. A job that records here
    ///         declares <c>Write(graph.Commands.Resource)</c>, which orders the recorders against each
    ///         other, and playback follows record order — so the outcome does not depend on timing.
    ///     </para>
    ///     Use <see cref="SchedulePlayback" /> with a buffer of your own to apply changes at some other
    ///     point inside a phase.
    /// </summary>
    public EntityCommandBuffer Commands { get; } = new();

    /// <summary>Time captured into jobs scheduled from now on. Set by system groups before scheduling.</summary>
    public Time Time { get; set; }

    public Realtime Realtime { get; set; }

    /// <summary>The phase currently open, if any.</summary>
    public PhaseId? CurrentPhase => _currentPhase;

    /// <summary>
    ///     Continuations waiting for the next <see cref="BeginPhase" /> (admitted external awaits, deferred segments,
    ///     starts).
    /// </summary>
    public int PendingIntakeCount
    {
        get
        {
            using (_issueLock.EnterScope())
            {
                return _intake.Count;
            }
        }
    }

    /// <summary>External completions that have arrived and wait for <see cref="AdmitExternal" />.</summary>
    public int PendingExternalCount
    {
        get
        {
            using (_issueLock.EnterScope())
            {
                return _externalArrivals.Count;
            }
        }
    }

    /// <summary>
    ///     How many segments one behavior may run per phase before its next segment is deferred to
    ///     the next phase. Bounds a phase against behaviors that chain data awaits without ever
    ///     parking; count-based, so deferral is deterministic.
    /// </summary>
    public int MaxSegmentsPerPhase { get; set; } = 256;

    /// <summary>Turns started and not yet cancelled.</summary>
    public int LiveTurnCount
    {
        get
        {
            using (_issueLock.EnterScope())
            {
                return _liveTurns.Count;
            }
        }
    }

    // ------------------------------------------------------------------ systems

    /// <summary>Schedules a single job on the worker pool.</summary>
    public JobHandle Schedule(string name, AccessSet access, Action<JobContext> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        ThrowIfFrozen(name, access);
        return Issue(new TaskNode(Scheduler, name, access, NewContext(), false) { Body = body });
    }

    /// <summary>Schedules a job that must run on the main thread (OS, window, graphics submission).</summary>
    public JobHandle ScheduleOnMainThread(string name, AccessSet access, Action<JobContext> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        ThrowIfFrozen(name, access);
        return Issue(new TaskNode(Scheduler, name, access, NewContext(), true) { Body = body });
    }

    /// <summary>Schedules <paramref name="body" /> over every chunk matched by <paramref name="query" />, in parallel batches.</summary>
    public JobHandle ScheduleChunks(string name, EntityQuery query, AccessSet access, ChunkJob body)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(body);
        ThrowIfFrozen(name, access);
        return Issue(new TaskNode(Scheduler, name, access, NewContext(), false) { Query = query, ChunkBody = body });
    }

    /// <summary>
    ///     Schedules <paramref name="body" /> over an index range in parallel batches, for work
    ///     that is a list rather than a query.
    /// </summary>
    /// <param name="itemCount">
    ///     Read when the node becomes ready rather than when it is issued, so the list may be
    ///     filled by a job this one is ordered after. Returning 0 completes the node without
    ///     running anything.
    /// </param>
    public JobHandle ScheduleBatches(string name, Func<int> itemCount, AccessSet access, RangeJob body)
    {
        ArgumentNullException.ThrowIfNull(itemCount);
        ArgumentNullException.ThrowIfNull(body);
        ThrowIfFrozen(name, access);
        return Issue(new TaskNode(Scheduler, name, access, NewContext(), false)
            { ItemCount = itemCount, RangeBody = body });
    }

    /// <summary>System-lane writes to a type frozen in the current phase are a configuration error, not something to defer.</summary>
    private void ThrowIfFrozen(string name, AccessSet access)
    {
        var frozen = _frozen;
        if (frozen is null)
        {
            return;
        }

        foreach (var resource in access.Writes)
        {
            if (resource.IsComponent && frozen.Contains(resource.ComponentType))
            {
                throw new InvalidOperationException(
                    $"Job '{name}' writes {resource}, which is frozen during phase {_currentPhase}.");
            }
        }
    }

    /// <summary>
    ///     Schedules the playback of a command buffer: writes the structure (ordered against every
    ///     entity access) and the buffer's own resource (ordered after the jobs that recorded into it).
    /// </summary>
    public JobHandle SchedulePlayback(EntityCommandBuffer buffer, string name = "EntityCommandBuffer.Playback")
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Schedule(name, Access.WriteStructure().Write(buffer.Resource),
            context => buffer.Playback(context.World));
    }

    public void Wait(JobHandle handle)
    {
        if (handle.Node is { } node)
        {
            Scheduler.Wait(node);
        }
    }

    /// <summary>Barrier: runs jobs on the main thread until every issued job has completed.</summary>
    public void WaitAll()
    {
        Scheduler.WaitAll();
    }

    /// <summary>
    ///     Closes the frame: ends an open phase if any, waits for everything (deferring segments
    ///     issued meanwhile) and forgets the frame's nodes so the next frame starts from an empty tracker.
    /// </summary>
    public void EndFrame()
    {
        if (_currentPhase is not null)
        {
            EndPhase();
        }

        WaitAll();
        using (_issueLock.EnterScope())
        {
            _closing = true;
        }

        try
        {
            WaitAll();
            using (_issueLock.EnterScope())
            {
                _tracker.Clear();
            }
        }
        finally
        {
            using (_issueLock.EnterScope())
            {
                _closing = false;
            }
        }
    }

    // ------------------------------------------------------------------- phases

    /// <summary>Declares the intermediate rounds of <paramref name="phase" />, in order, between initial and main.</summary>
    public void RegisterRounds(PhaseId phase, params string[] labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        if (labels.Distinct().Count() != labels.Length)
        {
            throw new ArgumentException("Round labels must be distinct.", nameof(labels));
        }

        using (_issueLock.EnterScope())
        {
            _labels[phase] = labels.ToArray();
        }
    }

    public IReadOnlyList<string> GetRounds(PhaseId phase)
    {
        using (_issueLock.EnterScope())
        {
            return _labels.TryGetValue(phase, out var labels) ? labels : [];
        }
    }

    /// <summary>
    ///     Opens a phase: creates its rounds and, if the phase dispatches behaviors, takes the
    ///     continuations collected since the last dispatching phase (admitted external awaits,
    ///     deferred segments, completed-round readers, starts) and the turns parked on the phase.
    ///     Both are issued by <see cref="ResumePhase" />, after the phase's systems, so that
    ///     "initial" means the state after the systems whatever the worker count.
    /// </summary>
    /// <param name="dispatchBehaviors">
    ///     False for phases in which no behavior segment runs (physics, transform propagation,
    ///     extract).
    /// </param>
    /// <param name="frozenTypes">Component types that may not be written during the phase.</param>
    public void BeginPhase(PhaseId phase, bool dispatchBehaviors = true,
        IReadOnlyCollection<ComponentTypeId>? frozenTypes = null)
    {
        using (_issueLock.EnterScope())
        {
            if (_currentPhase is not null)
            {
                throw new InvalidOperationException($"Phase {_currentPhase} is still open.");
            }

            _currentPhase = phase;
            _currentLabels = _labels.TryGetValue(phase, out var labels) ? labels : [];
            _frozen = frozenTypes is { Count: > 0 } ? new HashSet<ComponentTypeId>(frozenTypes) : null;
            _rounds = new RoundState[_currentLabels.Length + 2]; // 0 = initial (unassigned), 1..n labels, n+1 = main
            for (var i = 0; i < _rounds.Length; i++)
            {
                _rounds[i] = new RoundState(TaskNode.CreateGate(Scheduler, $"{phase}:{RoundName(i)}"));
            }

            _closedUpTo = -1;
            _entryOpen = dispatchBehaviors;
            _currentPhaseDispatches = dispatchBehaviors;
            // Snapshot the parked list now: turns that park on this phase while it runs (for example a
            // behavior whose first segment is issued from the intake below) resume at the next occurrence,
            // so what ResumePhase resumes does not depend on how fast segments happen to run.
            _pendingResume = dispatchBehaviors ? TakeParkedLocked(phase) : [];
            _pendingIntake = dispatchBehaviors ? TakeIntakeLocked() : [];
            if (dispatchBehaviors)
            {
                CancelDetachedTurnsLocked();
            }
        }
    }

    /// <summary>
    ///     Cancels the turns whose behavior component has been removed from its entity, or replaced
    ///     by another instance, so that they do not resume. Without this, removing a behavior would
    ///     leave its turn running, and adding it back would run a second turn on the same entity.
    ///     <para>
    ///         Done here because this is a point where the world is quiescent and nothing is resumed
    ///         yet: a segment itself may not read the structure. It only runs when the structure has
    ///         changed since the last check.
    ///     </para>
    /// </summary>
    private void CancelDetachedTurnsLocked()
    {
        var version = World.StructuralVersion;
        if (version == _checkedStructuralVersion)
        {
            return;
        }

        _checkedStructuralVersion = version;
        List<BehaviorContext>? detached = null;
        foreach (var context in _liveTurns)
        {
            if (!context.IsCancelled && context.IsDetached())
            {
                context.Cancel();
                (detached ??= []).Add(context);
            }
        }

        ForgetTurnsLocked(detached);
    }

    /// <summary>
    ///     Issues what <see cref="BeginPhase" /> took — the intake, then the behaviors that were
    ///     parked on <paramref name="phase" /> when it began, each in turn order — then closes the
    ///     phase for entry so that rounds can start closing. Called once the phase's systems are
    ///     scheduled, so that the tracker orders the segments' reads after the systems' writes.
    /// </summary>
    public void ResumePhase(PhaseId phase)
    {
        List<Deferred> intake;
        List<Parked> parked;
        using (_issueLock.EnterScope())
        {
            if (_currentPhase != phase)
            {
                throw new InvalidOperationException(
                    $"Phase {phase} is not open (current: {_currentPhase?.ToString() ?? "none"}).");
            }

            intake = _pendingIntake;
            _pendingIntake = [];
            parked = _pendingResume;
            _pendingResume = [];
        }

        IssueDeferred(intake);

        List<BehaviorContext>? cancelled = null;
        parked.Sort(static (a, b) => a.Context.TurnId.CompareTo(b.Context.TurnId));
        foreach (var entry in parked)
        {
            if (entry.Context.IsCancelled)
            {
                (cancelled ??= []).Add(entry.Context);
                continue;
            }

            IssueSegment(entry.Context, AccessSet.None, entry.Continuation, null, null);
        }

        using (_issueLock.EnterScope())
        {
            ForgetTurnsLocked(cancelled);
            _entryOpen = false;
            TryCloseRoundsLocked();
        }
    }

    private List<Parked> TakeParkedLocked(PhaseId phase)
    {
        if (_phaseQueues.Count <= phase.Value || _phaseQueues[phase.Value].Count == 0)
        {
            return [];
        }

        var parked = _phaseQueues[phase.Value];
        _phaseQueues[phase.Value] = new List<Parked>();
        return parked;
    }

    /// <summary>
    ///     Closes the phase: waits for every job and segment, closes the remaining rounds, commits
    ///     the buffered writes on the calling (main) thread, applies the recorded structural changes,
    ///     and queues completed-round readers for the next phase.
    ///     <para>
    ///         A failing job or behavior does not stop the phase from closing: the others' writes and
    ///         structural changes are still applied, and the failures are rethrown once the phase is
    ///         closed. If <see cref="ResumePhase" /> was never reached (a system threw while being
    ///         scheduled), what <see cref="BeginPhase" /> took is put back, so the parked turns resume
    ///         at the next occurrence of the phase instead of being lost.
    ///     </para>
    /// </summary>
    public void EndPhase()
    {
        RoundState[] rounds;
        HashSet<ComponentTypeId>? frozen;
        using (_issueLock.EnterScope())
        {
            if (_currentPhase is null)
            {
                throw new InvalidOperationException("No phase is open.");
            }

            _currentPhaseDispatches = false;
            frozen = _frozen;
        }

        var errors = new List<Exception>();
        try
        {
            // Let chains run to quiescence first (bounded by MaxSegmentsPerPhase); only then refuse new issues.
            WaitAllCollecting(errors);
            using (_issueLock.EnterScope())
            {
                _closing = true;
            }

            WaitAllCollecting(errors);
            using (_issueLock.EnterScope())
            {
                _entryOpen = false;
                TryCloseRoundsLocked(true);
                rounds = _rounds;
            }

            Commit(rounds, frozen);
            ApplyStructuralChanges(errors);
        }
        finally
        {
            using (_issueLock.EnterScope())
            {
                RestoreUndispatchedLocked();
                _intake.AddRange(_completedWaiters);
                _completedWaiters.Clear();
                _currentPhase = null;
                _currentLabels = [];
                _frozen = null;
                _rounds = [];
                _closing = false;
            }
        }

        switch (errors.Count)
        {
            case 0:
                return;
            case 1:
                ExceptionDispatchInfo.Throw(errors[0]);
                break;
            default:
                throw new AggregateException(errors);
        }
    }

    /// <summary>A wait that has thrown has also drained the pool, so the phase can go on closing.</summary>
    private void WaitAllCollecting(List<Exception> errors)
    {
        try
        {
            WaitAll();
        }
        catch (JobFailedException ex)
        {
            errors.Add(ex);
        }
    }

    /// <summary>Puts back the intake and parked turns taken by <see cref="BeginPhase" /> that were never issued.</summary>
    private void RestoreUndispatchedLocked()
    {
        if (_pendingIntake.Count > 0)
        {
            _intake.AddRange(_pendingIntake);
            _pendingIntake = [];
        }

        if (_pendingResume.Count > 0 && _currentPhase is { } phase)
        {
            _phaseQueues[phase.Value].AddRange(_pendingResume);
            _pendingResume = [];
        }
    }

    /// <summary>
    ///     Applies the phase's recorded structural changes, with everything already quiescent.
    ///     <para>
    ///         Systems go first and behaviors after: a phase's systems are the simulation, and a
    ///         behavior reacts to what it saw at the start of the phase.
    ///     </para>
    ///     <para>
    ///         Each turn records into a buffer of its own, and the buffers are played back in turn-id
    ///         order. Turn ids are assigned when a behavior starts, so the result is the same however
    ///         the segments happened to be scheduled — the same rule that orders buffered value
    ///         writes. Recording into one shared buffer would instead follow whichever segment ran
    ///         first.
    ///     </para>
    ///     A buffer that fails part-way has still applied its other commands (see
    ///     <see cref="EntityCommandBuffer.Playback" />); the failure is collected and the remaining
    ///     buffers are played back all the same.
    /// </summary>
    private void ApplyStructuralChanges(List<Exception> errors)
    {
        if (!Commands.IsEmpty)
        {
            PlaybackCollecting(Commands, errors);
        }

        List<BehaviorContext> recorders;
        using (_issueLock.EnterScope())
        {
            if (_commandRecorders.Count == 0)
            {
                return;
            }

            recorders = new List<BehaviorContext>(_commandRecorders);
            _commandRecorders.Clear();
        }

        recorders.Sort(static (a, b) => a.TurnId.CompareTo(b.TurnId));
        foreach (var context in recorders)
        {
            if (context.TakeRecordedCommands() is { } commands)
            {
                PlaybackCollecting(commands, errors);
            }
        }
    }

    private void PlaybackCollecting(EntityCommandBuffer buffer, List<Exception> errors)
    {
        try
        {
            buffer.Playback(World);
        }
        catch (Exception ex)
        {
            errors.Add(ex);
        }
    }

    /// <summary>Notes that a turn has something to apply at the end of the phase.</summary>
    internal void OnCommandsRecorded(BehaviorContext context)
    {
        using (_issueLock.EnterScope())
        {
            if (!_commandRecorders.Contains(context))
            {
                _commandRecorders.Add(context);
            }
        }
    }

    // ------------------------------------------------------------ external input

    /// <summary>
    ///     Admits external completions (awaits on things outside the graph, background jobs) into
    ///     the intake, so they are issued at the next dispatching phase. Called once per frame by the
    ///     engine. With <paramref name="expected" /> (a replay), exactly those completions are admitted,
    ///     waiting for each to arrive; everything else stays queued. Returns the admitted keys in order.
    /// </summary>
    public ExternalKey[] AdmitExternal(ImmutableArray<ExternalKey> expected = default, TimeSpan? timeout = null)
    {
        if (expected.IsDefault)
        {
            using (_issueLock.EnterScope())
            {
                _externalArrivals.Sort(static (a, b) => a.Key.CompareTo(b.Key));
                var keys = new ExternalKey[_externalArrivals.Count];
                for (var i = 0; i < _externalArrivals.Count; i++)
                {
                    var arrival = _externalArrivals[i];
                    keys[i] = arrival.Key;
                    _intake.Add(new Deferred(arrival.Context, AccessSet.None, arrival.Continuation, null, null));
                }

                _externalArrivals.Clear();
                return keys;
            }
        }

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        foreach (var key in expected)
        {
            while (true)
            {
                using (_issueLock.EnterScope())
                {
                    var index = _externalArrivals.FindIndex(a => a.Key == key);
                    if (index >= 0)
                    {
                        var arrival = _externalArrivals[index];
                        _externalArrivals.RemoveAt(index);
                        _intake.Add(new Deferred(arrival.Context, AccessSet.None, arrival.Continuation, null, null));
                        break;
                    }
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"Replay: external completion {key} did not arrive.");
                }

                Thread.Sleep(1);
            }
        }

        return expected.ToArray();
    }

    // --------------------------------------------------------------- behaviors

    /// <summary>
    ///     Starts <paramref name="behavior" /> for <paramref name="entity" />. The first segment (no data
    ///     access) is issued at the next <see cref="BeginPhase" /> in turn order, never immediately, so
    ///     when a behavior first runs does not depend on the worker count. The behavior is cancelled
    ///     when the entity is destroyed, when it fails, and — if it was started as a component — when
    ///     that component is removed.
    /// </summary>
    /// <remarks>
    ///     Main thread only: turn ids decide commit order, so they have to be handed out in an order
    ///     that does not depend on timing. A behavior that wants to start another adds it as a
    ///     component through <see cref="BehaviorContext.Commands" />.
    /// </remarks>
    public BehaviorContext Start(Entity entity, Behavior behavior, string? name = null)
    {
        return Start(entity, behavior, null, name);
    }

    /// <param name="attachedAs">
    ///     The component type <paramref name="behavior" /> is attached as. The turn is cancelled once
    ///     the entity no longer holds this instance under that type.
    /// </param>
    internal BehaviorContext Start(Entity entity, Behavior behavior, ComponentTypeId? attachedAs,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(behavior);
        if (Environment.CurrentManagedThreadId != Scheduler.MainThreadId)
        {
            throw new InvalidOperationException(
                "Behaviors are started from the main thread; from a behavior, add one as a component through Commands.");
        }

        var context = new BehaviorContext(this, entity, behavior, ++_nextTurnId, attachedAs,
            name ?? behavior.GetType().Name);
        behavior.Context = context;
        using (_issueLock.EnterScope())
        {
            _liveTurns.Add(context);
            _intake.Add(new Deferred(context, AccessSet.None, context.RunFirstSegment, null, null));
        }

        return context;
    }

    /// <summary>
    ///     Cancels every running turn and drops the continuations waiting to resume them.
    ///     <para>
    ///         A behavior's suspended state lives in a compiler-generated state machine, which cannot
    ///         be serialized and, after a script reload, belongs to a type that no longer exists. So a
    ///         reload stops every turn rather than trying to carry it across; whatever should outlive
    ///         the reload has to be in a component, and the behavior is started again from its entry
    ///         point. Continuations that arrive afterwards — an external await completing late — find
    ///         their turn cancelled and are dropped.
    ///     </para>
    ///     Call between frames, with the graph quiescent (after <see cref="EndFrame" />).
    /// </summary>
    /// <returns>How many turns were cancelled.</returns>
    public int CancelAllTurns()
    {
        using (_issueLock.EnterScope())
        {
            if (_currentPhaseDispatches)
            {
                throw new InvalidOperationException(
                    "Turns cannot be cancelled from a phase that dispatches behaviors; one may be mid-segment. "
                    + "Cancel between frames or from a phase like FrameBegin.");
            }

            var cancelled = _liveTurns.Count;
            foreach (var context in _liveTurns)
            {
                context.Cancel();
            }

            _liveTurns.Clear();
            _intake.Clear();
            _completedWaiters.Clear();
            _externalArrivals.Clear();
            foreach (var queue in _phaseQueues)
            {
                queue.Clear();
            }

            return cancelled;
        }
    }

    /// <summary>
    ///     Issues the next segment of a behavior with the access it declared at the await.
    ///     <paramref name="waitRound" /> makes the segment wait for that round to close (labelled reads).
    /// </summary>
    internal void IssueSegment(BehaviorContext context, AccessSet access, Action continuation, EntityAccess? handle,
        Round? waitRound)
    {
        TaskNode node = null!;
        node = new TaskNode(Scheduler, context.Name, access, NewContext(), false)
        {
            Body = _ => context.RunSegment(node, continuation, handle)
        };

        using (_issueLock.EnterScope())
        {
            if (_closing)
            {
                _intake.Add(new Deferred(context, access, continuation, handle, waitRound));
                return;
            }

            if (_currentPhase is not null)
            {
                if (!context.InPhase)
                {
                    if (!_entryOpen)
                    {
                        _intake.Add(new Deferred(context, access, continuation, handle, waitRound));
                        return;
                    }

                    EnterPhaseLocked(context);
                }
                else if (context.SegmentsThisPhase >= MaxSegmentsPerPhase)
                {
                    _intake.Add(new Deferred(context, access, continuation, handle, waitRound));
                    return;
                }

                context.SegmentsThisPhase++;

                var target = context.CurrentRound;
                if (waitRound is { } wait)
                {
                    var waitIndex = ResolveRoundLocked(wait);
                    target = Math.Max(target, waitIndex + 1);
                }

                if (handle is not null)
                {
                    handle.ResolveRounds(ResolveRoundLocked(handle.ReadRound), ResolveRoundLocked(handle.WriteRound));
                    if (handle.Access.EntityWrites.Length > 0)
                    {
                        // Only declared writes move the turn to their round; a read-only segment keeps its options open.
                        if (handle.WriteRoundIndex < context.CurrentRound)
                        {
                            throw new InvalidOperationException(
                                $"{context} cannot write to round '{handle.WriteRound}' after advancing to '{RoundName(context.CurrentRound)}'.");
                        }

                        target = Math.Max(target, handle.WriteRoundIndex);
                    }
                }

                AdvanceLocked(context, Math.Min(target, _rounds.Length - 1));
                context.ContinuedInPhase = true;
            }

            IssueLocked(node);
            if (context.CurrentNode is { } previous)
            {
                node.DependOn(previous); // turn order: one segment of a behavior at a time
            }

            if (waitRound is { } round && _currentPhase is not null)
            {
                var index = ResolveRoundLocked(round);
                if (index > _closedUpTo)
                {
                    node.DependOn(_rounds[index].Gate);
                }
            }
        }

        node.Release();
    }

    /// <summary>
    ///     Declares a phase of the loop that drives this graph, and whether it dispatches behaviors.
    ///     Once any phase is declared, a behavior that awaits a phase not declared as dispatching fails
    ///     at the await, rather than waiting for a resume that never comes.
    /// </summary>
    public void DeclarePhase(PhaseId phase, bool dispatchesBehaviors)
    {
        using (_issueLock.EnterScope())
        {
            _declaredPhases[phase] = dispatchesBehaviors;
        }
    }

    internal void ThrowIfCannotPark(PhaseId phase)
    {
        bool known;
        using (_issueLock.EnterScope())
        {
            if (_declaredPhases.Count == 0 || _declaredPhases.GetValueOrDefault(phase))
            {
                return;
            }

            known = _declaredPhases.ContainsKey(phase);
        }

        throw new InvalidOperationException(known
            ? $"Phase {phase} does not dispatch behaviors, so a behavior waiting for it would never resume."
            : $"Phase {phase} is not part of the loop, so a behavior waiting for it would never resume.");
    }

    /// <summary>Parks a continuation until <see cref="ResumePhase" /> is called for <paramref name="phase" />.</summary>
    internal void ParkOnPhase(BehaviorContext context, PhaseId phase, Action continuation)
    {
        using (_issueLock.EnterScope())
        {
            while (_phaseQueues.Count <= phase.Value)
            {
                _phaseQueues.Add(new List<Parked>());
            }

            _phaseQueues[phase.Value].Add(new Parked(context, continuation));
        }
    }

    /// <summary>Parks a completed-round read until the phase has committed; it is issued at the next phase.</summary>
    internal void ParkOnCompleted(BehaviorContext context, AccessSet access, Action continuation, EntityAccess handle)
    {
        using (_issueLock.EnterScope())
        {
            _completedWaiters.Add(new Deferred(context, access, continuation, handle, null));
        }
    }

    /// <summary>
    ///     Records the completion of an external await; it enters the graph at the next
    ///     <see cref="AdmitExternal" /> (frame begin), never on the completing thread.
    /// </summary>
    internal void EnqueueExternal(BehaviorContext context, int sequence, Action continuation)
    {
        using (_issueLock.EnterScope())
        {
            _externalArrivals.Add(new ExternalArrival(context, new ExternalKey(context.TurnId, sequence),
                continuation));
        }
    }

    /// <summary>
    ///     Issues the continuations collected since the last drain, in turn order. Normally done by
    ///     <see cref="BeginPhase" />.
    /// </summary>
    public void DrainIntake()
    {
        List<Deferred> deferred;
        using (_issueLock.EnterScope())
        {
            deferred = TakeIntakeLocked();
        }

        IssueDeferred(deferred);
    }

    /// <summary>Called by a segment when it ends; a turn that did not continue in the phase leaves it.</summary>
    internal void OnSegmentEnded(BehaviorContext context)
    {
        using (_issueLock.EnterScope())
        {
            if (context.InPhase && !context.ContinuedInPhase)
            {
                LeavePhaseLocked(context);
            }

            // A turn that has run out or been cancelled is no longer something a reload has to stop;
            // pruning here keeps the set from growing for the lifetime of the graph.
            if (context.IsCancelled || context.Task.IsCompleted)
            {
                _liveTurns.Remove(context);
            }
        }
    }

    /// <summary>Records a buffered write for the current segment's turn. Called from inside a segment.</summary>
    internal PendingWrite RecordWrite(BehaviorContext context, Round round, Func<int, PendingWrite> create)
    {
        int index;
        using (_issueLock.EnterScope())
        {
            if (_currentPhase is null || !context.InPhase)
            {
                throw new InvalidOperationException("Behavior writes are only possible inside a phase.");
            }

            index = ResolveRoundLocked(round);
            if (index < context.CurrentRound)
            {
                throw new InvalidOperationException(
                    $"{context} cannot write to round '{round}' after advancing to '{RoundName(context.CurrentRound)}'.");
            }

            if (index > _closedUpTo && index <= _rounds.Length - 1)
            {
                var write = create(index);
                _rounds[index].Add(write);
                return write;
            }

            throw new InvalidOperationException($"Round '{round}' is already closed.");
        }
    }

    /// <summary>
    ///     The value of a component as seen by a segment: the in-place value, the closed rounds up to
    ///     <paramref name="readRoundIndex" /> in commit order, then the turn's own pending writes.
    /// </summary>
    internal void ReadOverlay(BehaviorContext context, Entity entity, ComponentTypeInfo info, int readRoundIndex,
        Span<byte> destination)
    {
        if (readRoundIndex == 0 && context.OwnWrites.Count == 0)
        {
            return; // the common case: initial read, nothing pending from this turn
        }

        var buffer = destination.ToArray(); // small: component size
        RoundState[] rounds;
        using (_issueLock.EnterScope())
        {
            rounds = _rounds;
        }

        var limit = Math.Min(readRoundIndex, rounds.Length - 1);
        for (var r = 1; r <= limit; r++)
        {
            foreach (var write in rounds[r].WritesFor(entity, info.Id))
            {
                write.Apply(buffer);
            }
        }

        foreach (var write in context.OwnWrites)
        {
            if (write.Round > limit && write.Entity == entity && write.Type == info.Id)
            {
                write.Apply(buffer);
            }
        }

        buffer.AsSpan().CopyTo(destination);
    }

    internal int ResolveRound(Round round)
    {
        using (_issueLock.EnterScope())
        {
            return ResolveRoundLocked(round);
        }
    }

    // ------------------------------------------------------------------ internal

    private JobHandle Issue(TaskNode node)
    {
        using (_issueLock.EnterScope())
        {
            IssueLocked(node);
        }

        node.Release(); // issue latch: all dependencies are registered
        return new JobHandle(node);
    }

    private void IssueLocked(TaskNode node)
    {
        Scheduler.Register(node);
        _tracker.Issue(node);
    }

    private JobContext NewContext()
    {
        return new JobContext(Time, Realtime, World);
    }

    private List<Deferred> TakeIntakeLocked()
    {
        var deferred = new List<Deferred>(_intake);
        _intake.Clear();
        return deferred;
    }

    private void IssueDeferred(List<Deferred> deferred)
    {
        if (deferred.Count == 0)
        {
            return;
        }

        List<BehaviorContext>? cancelled = null;
        deferred.Sort(static (a, b) => a.Context.TurnId.CompareTo(b.Context.TurnId));
        foreach (var entry in deferred)
        {
            if (entry.Context.IsCancelled)
            {
                (cancelled ??= []).Add(entry.Context);
                continue;
            }

            IssueSegment(entry.Context, entry.Access, entry.Continuation, entry.Handle, entry.WaitRound);
        }

        if (cancelled is not null)
        {
            using (_issueLock.EnterScope())
            {
                ForgetTurnsLocked(cancelled);
            }
        }
    }

    /// <summary>
    ///     Drops turns that were cancelled while waiting. Their continuation is discarded here instead
    ///     of running a segment, so <see cref="OnSegmentEnded" /> would never prune them.
    /// </summary>
    private void ForgetTurnsLocked(List<BehaviorContext>? cancelled)
    {
        if (cancelled is null)
        {
            return;
        }

        foreach (var context in cancelled)
        {
            _liveTurns.Remove(context);
        }
    }

    private int ResolveRoundLocked(Round round)
    {
        switch (round.Kind)
        {
            case RoundKind.Initial:
                return 0;
            case RoundKind.Main:
                return _currentLabels.Length + 1;
            case RoundKind.Completed:
                return _currentLabels.Length + 2;
            case RoundKind.Label:
                var index = Array.IndexOf(_currentLabels, round.LabelName);
                if (index < 0)
                {
                    throw new InvalidOperationException(
                        $"Round '{round.LabelName}' is not registered for phase {_currentPhase}.");
                }

                return index + 1;
            default:
                throw new ArgumentOutOfRangeException(nameof(round));
        }
    }

    private string RoundName(int index)
    {
        if (index == 0)
        {
            return "initial";
        }

        if (index == _currentLabels.Length + 1)
        {
            return "main";
        }

        if (index == _currentLabels.Length + 2)
        {
            return "completed";
        }

        return _currentLabels[index - 1];
    }

    private void EnterPhaseLocked(BehaviorContext context)
    {
        context.InPhase = true;
        context.CurrentRound = 0;
        context.SegmentsThisPhase = 0;
        context.OwnWrites.Clear();
        _rounds[0].Active++;
    }

    private void AdvanceLocked(BehaviorContext context, int round)
    {
        if (round <= context.CurrentRound)
        {
            return;
        }

        _rounds[context.CurrentRound].Active--;
        _rounds[round].Active++;
        context.CurrentRound = round;
        TryCloseRoundsLocked();
    }

    private void LeavePhaseLocked(BehaviorContext context)
    {
        _rounds[context.CurrentRound].Active--;
        context.InPhase = false;
        TryCloseRoundsLocked();
    }

    /// <summary>
    ///     Closes rounds in order while no turn can still write to them. Entry must be closed first, so late starters
    ///     cannot write to a closed round.
    /// </summary>
    private void TryCloseRoundsLocked(bool force = false)
    {
        if (_entryOpen && !force)
        {
            return;
        }

        while (_closedUpTo + 1 < _rounds.Length)
        {
            var next = _rounds[_closedUpTo + 1];
            if (next.Active > 0 && !force)
            {
                return;
            }

            _closedUpTo++;
            next.Close();
        }
    }

    /// <summary>
    ///     Applies buffered writes in place: first those carried over from phases that froze their
    ///     type, then this phase's rounds in order. Writes to a type frozen in this phase are carried
    ///     to the next phase that allows them.
    /// </summary>
    private void Commit(RoundState[] rounds, HashSet<ComponentTypeId>? frozen)
    {
        var buffer = Array.Empty<byte>();
        if (_carried.Count > 0)
        {
            var carried = _carried.ToArray();
            _carried.Clear();
            Array.Sort(carried, PendingWrite.Compare);
            foreach (var write in carried)
            {
                Apply(write);
            }
        }

        for (var r = 1; r < rounds.Length; r++)
        {
            foreach (var write in rounds[r].All)
            {
                Apply(write);
            }
        }

        void Apply(PendingWrite write)
        {
            if (frozen is not null && frozen.Contains(write.Type))
            {
                _carried.Add(write);
                return;
            }

            if (!World.IsAlive(write.Entity) || !World.HasComponent(write.Entity, write.Type))
            {
                return;
            }

            var info = ComponentTypeRegistry.GetInfo(write.Type);
            if (buffer.Length < info.Size)
            {
                buffer = new byte[info.Size];
            }

            var bytes = buffer.AsSpan(0, info.Size);
            World.CopyComponent(write.Entity, write.Type, bytes);
            write.Apply(buffer);
            World.SetComponent(write.Entity, write.Type, bytes);
        }
    }

    private readonly record struct Parked(BehaviorContext Context, Action Continuation);

    private readonly record struct ExternalArrival(BehaviorContext Context, ExternalKey Key, Action Continuation);

    private readonly record struct Deferred(
        BehaviorContext Context,
        AccessSet Access,
        Action Continuation,
        EntityAccess? Handle,
        Round? WaitRound);

    /// <summary>Buffered writes of one round of the open phase, plus the turn count that can still write to it.</summary>
    private sealed class RoundState(TaskNode gate)
    {
        private readonly Dictionary<(int entity, int type), List<PendingWrite>> _byTarget = new();
        private readonly Lock _lock = new();
        private bool _closed;

        public TaskNode Gate { get; } = gate;
        public int Active { get; set; }
        public List<PendingWrite> All { get; } = new();

        public void Add(PendingWrite write)
        {
            lock (_lock)
            {
                if (_closed)
                {
                    throw new InvalidOperationException("Round is closed.");
                }

                All.Add(write);
                var key = (write.Entity.Index, write.Type.Value);
                if (!_byTarget.TryGetValue(key, out var list))
                {
                    list = new List<PendingWrite>();
                    _byTarget.Add(key, list);
                }

                list.Add(write);
            }
        }

        public IReadOnlyList<PendingWrite> WritesFor(Entity entity, ComponentTypeId type)
        {
            // Only called for closed rounds, whose lists are immutable and sorted.
            return _byTarget.TryGetValue((entity.Index, type.Value), out var list) ? list : [];
        }

        public void Close()
        {
            lock (_lock)
            {
                _closed = true;
                All.Sort(PendingWrite.Compare);
                foreach (var list in _byTarget.Values)
                {
                    list.Sort(PendingWrite.Compare);
                }
            }

            Gate.Open();
        }
    }
}