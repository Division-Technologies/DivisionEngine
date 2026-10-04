namespace DivisionEngine;

/// <summary>Per-job data handed to job bodies. Values are captured when the job is scheduled.</summary>
public readonly struct JobContext(Time time, Realtime realtime, World world)
{
    public readonly Time Time = time;
    public readonly Realtime Realtime = realtime;
    public readonly World World = world;
}

public delegate void ChunkJob(in JobContext context, ArchetypeChunk chunk);

/// <summary>
///     A job body over a half-open slice of an index range, for work that is a runtime-sized list
///     rather than a query. The slice bounds come from the same batching as chunk jobs.
/// </summary>
public delegate void RangeJob(in JobContext context, int start, int end);

/// <summary>Handle to a scheduled job.</summary>
public readonly struct JobHandle
{
    internal readonly TaskNode? Node;

    internal JobHandle(TaskNode node)
    {
        Node = node;
    }

    public bool IsCompleted => Node?.IsCompleted ?? true;

    public string Name => Node?.Name ?? "";
}

/// <summary>
///     A node of the dependency graph: a job body, its declared access set, the predecessors it
///     still waits for, and the successors it releases on completion. A node may fan out into
///     several work items (chunk batches) that complete it jointly.
/// </summary>
internal sealed class TaskNode
{
    private static readonly ProfilerZoneSource ExecuteZone = Profiler.DeclareZone("Job");

    private readonly Lock _lock = new();
    private readonly JobScheduler _scheduler;
    private int _batchSize;
    private List<Chunk>? _chunks;
    private volatile bool _completed;
    private volatile bool _hasWaiter;
    private int _itemCount;
    private int _pendingDependencies = 1; // issue latch: released once all dependencies are registered
    private int _pendingWork;
    private List<TaskNode>? _successors;

    internal TaskNode(JobScheduler scheduler, string name, AccessSet access, JobContext context, bool mainThread)
    {
        _scheduler = scheduler;
        Name = name;
        Access = access;
        Context = context;
        MainThread = mainThread;
    }

    public string Name { get; }
    public AccessSet Access { get; }
    public JobContext Context { get; }
    public bool MainThread { get; }

    /// <summary>False for gates: nodes that never run and are completed by hand, so waits must not count them.</summary>
    public bool CountsAsLive { get; private init; } = true;

    public Action<JobContext>? Body { get; init; }
    public ChunkJob? ChunkBody { get; init; }
    public EntityQuery? Query { get; init; }
    public RangeJob? RangeBody { get; init; }

    /// <summary>
    ///     How many items <see cref="RangeBody" /> covers. Read when the node becomes ready, not
    ///     when it is issued, so the count may be produced by a job this one depends on.
    /// </summary>
    public Func<int>? ItemCount { get; init; }

    public bool IsCompleted => _completed;

    public Exception? Error { get; private set; }

    /// <summary>A node with no body that others can depend on; it completes when <see cref="Open" /> is called.</summary>
    internal static TaskNode CreateGate(JobScheduler scheduler, string name)
    {
        return new TaskNode(scheduler, name, AccessSet.None, default, false) { CountsAsLive = false };
    }

    internal void Open()
    {
        Complete();
    }

    /// <summary>Registers <paramref name="predecessor" /> as a dependency unless it has already completed.</summary>
    public void DependOn(TaskNode predecessor)
    {
        if (ReferenceEquals(predecessor, this))
        {
            return;
        }

        lock (predecessor._lock)
        {
            if (predecessor._completed)
            {
                return;
            }

            (predecessor._successors ??= new List<TaskNode>()).Add(this);
        }

        Interlocked.Increment(ref _pendingDependencies);
    }

    /// <summary>Called once per satisfied dependency (and once to release the issue latch).</summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _pendingDependencies) == 0)
        {
            Ready();
        }
    }

    public void MarkWaited()
    {
        _hasWaiter = true;
    }

    private void Ready()
    {
        int work;
        try
        {
            work = Prepare();
        }
        catch (Exception ex)
        {
            // A failing item count fails the node like a failing body would: recorded, rethrown by
            // the next wait, and the node still completes so that its successors and waits are
            // released. Ready runs on whichever thread released the last dependency, a worker
            // included, so letting the exception escape would take that thread down.
            Fail(ex);
            work = 0;
        }

        if (work == 0)
        {
            Complete();
            return;
        }

        _pendingWork = work;
        _scheduler.Enqueue(this, work);
    }

    /// <summary>
    ///     Splits the job into work items. Chunk jobs snapshot the query's chunks here, at the moment
    ///     the node becomes ready, so structural changes ordered before this node are visible.
    /// </summary>
    private int Prepare()
    {
        if (RangeBody is not null)
        {
            _itemCount = ItemCount!();
            return Split(_itemCount);
        }

        if (Query is null)
        {
            return 1;
        }

        _chunks = Query.CollectChunks(new List<Chunk>());
        return Split(_chunks.Count);
    }

    /// <summary>Divides <paramref name="count" /> items into batches and returns how many there are.</summary>
    private int Split(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        var batches = Math.Min(count, Math.Max(1, _scheduler.WorkerCount + 1) * 4);
        _batchSize = (count + batches - 1) / batches;
        return (count + _batchSize - 1) / _batchSize;
    }

    public void Execute(int workIndex)
    {
        // The job's own name is only known at run time, so each distinct name gets its own
        // interned source location under this call site - otherwise every job would aggregate
        // into one row in the profiler's statistics. A chunk job opens one zone per batch, which
        // is what makes the split across workers visible.
        using var zone = Profiler.ZoneNamed(Name, ExecuteZone);

        JobSafety.Enter(Access, Name);
        try
        {
            if (RangeBody is not null)
            {
                var context = Context;
                var start = workIndex * _batchSize;
                RangeBody(in context, start, Math.Min(start + _batchSize, _itemCount));
            }
            else if (Query is null)
            {
                Body!(Context);
            }
            else
            {
                var chunks = _chunks!;
                var context = Context;
                var start = workIndex * _batchSize;
                var end = Math.Min(start + _batchSize, chunks.Count);
                for (var i = start; i < end; i++)
                {
                    ChunkBody!(in context, new ArchetypeChunk(chunks[i]));
                }
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            JobSafety.Exit();
        }

        if (Interlocked.Decrement(ref _pendingWork) == 0)
        {
            Complete();
        }
    }

    private void Fail(Exception ex)
    {
        lock (_lock)
        {
            Error = Error is null ? ex : new AggregateException(Error, ex);
        }

        _scheduler.ReportError(ex);
    }

    private void Complete()
    {
        List<TaskNode>? successors;
        lock (_lock)
        {
            _completed = true;
            successors = _successors;
            _successors = null;
            _chunks = null;
        }

        _scheduler.OnCompleted(this, _hasWaiter);

        if (successors is not null)
        {
            foreach (var successor in successors)
            {
                successor.Release();
            }
        }
    }

    public override string ToString()
    {
        return Name;
    }
}