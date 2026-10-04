using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace DivisionEngine;

public sealed class Engine : IDisposable
{
    private readonly ILogger _logger;
    private readonly DivisionSynchronizationContext _synchronizationContext = new();
    private UpdateTimeProvider _frameClock;
    private FrameLog? _replay;
    private int _replayIndex;
    private Time _time = new(0, 0);

    /// <param name="scheduler">Worker pool to use; defaults to one worker per core besides the main thread.</param>
    /// <param name="loop">Frame loop to run; defaults to the standard phases with a 20 ms fixed step.</param>
    public Engine(ILogger logger, JobScheduler? scheduler = null, FrameLoop? loop = null)
    {
        _logger = logger;

        // No-op unless the build defines DIVISION_PROFILING. Idempotent, so several engines in one
        // process (tests) share one capture. Deliberately not stopped in Dispose: shutting the
        // profiler down closes the capture, which is the hosting application's call, not an
        // engine instance's - see Profiler.Shutdown.
        if (Profiler.IsCompiledIn && !Profiler.Startup())
        {
            _logger.LogWarning("Profiling is compiled in but inactive: {Reason}", Profiler.UnavailableReason);
        }

        Scheduler = scheduler ?? JobScheduler.CreateDefault();
        World = new World();
        World.AddStructuralHook(HierarchyIntegrity.Instance);
        Graph = new JobGraph(Scheduler, World);
        RenderWorld = new RenderWorld();
        Loop = loop ?? new FrameLoop();
        Loop.Extract.Add(new ExtractFrameSystem());
        foreach (var phase in Loop.Phases)
        {
            Graph.DeclarePhase(phase.Phase, phase.DispatchesBehaviors);
        }
    }

    public ILogger Logger => _logger;
    public World World { get; }
    public JobScheduler Scheduler { get; }
    public JobGraph Graph { get; }
    public RenderWorld RenderWorld { get; }
    public FrameLoop Loop { get; }

    /// <summary>Frames run so far.</summary>
    public long FrameIndex { get; private set; }

    /// <summary>The log being recorded, if <see cref="StartRecording" /> was called.</summary>
    public FrameLog? Recording { get; private set; }

    public bool IsReplaying => _replay is not null;

    public bool IsReplayComplete => _replay is { } log && _replayIndex >= log.Count;

    public void Dispose()
    {
        Graph.WaitAll();
        Scheduler.Dispose();
        World.Dispose();
    }

    /// <summary>Adds a system to the FrameBegin phase (authoring refresh, input). Use <see cref="Loop" /> for other phases.</summary>
    public void AddSystem(ISystem system)
    {
        Loop.FrameBegin.Add(system);
    }

    public void AddSystem(PhaseId phase, ISystem system)
    {
        Loop[phase].Add(system);
    }

    /// <summary>Starts recording clock samples and external completions per frame (see <see cref="FrameLog" />).</summary>
    public FrameLog StartRecording()
    {
        return Recording = new FrameLog();
    }

    public void StopRecording()
    {
        Recording = null;
    }

    /// <summary>
    ///     Replays a recorded run: <see cref="RunFrame()" /> takes clock and external completions from
    ///     the log. The clocks are not rewound, so a replay has to start on an engine that has not run
    ///     a frame yet, just as the recording did.
    /// </summary>
    public void Replay(FrameLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (FrameIndex != 0)
        {
            throw new InvalidOperationException("A replay has to start on an engine that has not run a frame.");
        }

        _replay = log;
        _replayIndex = 0;
    }

    public void Main(CancellationToken ct = default)
    {
        SynchronizationContext.SetSynchronizationContext(_synchronizationContext);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            RunFrame();
        }
    }

    /// <summary>Runs one frame with the current clock (or the recorded clock when replaying).</summary>
    public void RunFrame()
    {
        if (_replay is { } log)
        {
            if (_replayIndex >= log.Count)
            {
                throw new InvalidOperationException("The replay log has no more frames.");
            }

            RunFrameCore(log.Frames[_replayIndex].Realtime);
            return;
        }

        RunFrameCore(Realtime.Current);
    }

    /// <summary>
    ///     Runs one frame at an explicit clock sample (headless runs, tests): drains main-thread
    ///     continuations, admits external completions, executes the loop's phases, closes the frame.
    /// </summary>
    public void RunFrame(Realtime realtime)
    {
        if (_replay is not null)
        {
            throw new InvalidOperationException("While replaying, the clock comes from the log; call RunFrame().");
        }

        RunFrameCore(realtime);
    }

    private void RunFrameCore(Realtime realtime)
    {
        _synchronizationContext.Update();

        ExternalKey[] externals;
        if (_replay is { } log)
        {
            if (_replayIndex >= log.Count)
            {
                throw new InvalidOperationException("The replay log has no more frames.");
            }

            externals = Graph.AdmitExternal(log.Frames[_replayIndex].Externals);
            _replayIndex++;
        }
        else
        {
            externals = Graph.AdmitExternal();
        }

        Recording?.Add(new FrameRecord(realtime, ImmutableCollectionsMarshal.AsImmutableArray(externals)));

        _time = _frameClock.DoUpdate(realtime, out var time)
            ? time
            : new Time(_time.Current, 0); // same clock sample as the previous frame

        Graph.Time = _time;
        Graph.Realtime = realtime;
        var frameContext = new FrameContext(ref _time, realtime, this);
        try
        {
            Loop.Execute(ref frameContext);
        }
        finally
        {
            Graph.EndFrame();
            FrameIndex++;
            MemoryPlots.Sample();
            Profiler.FrameMark();
        }
    }
}