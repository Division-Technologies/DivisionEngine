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
        Scheduler = scheduler ?? JobScheduler.CreateDefault();
        World = new World();
        Graph = new JobGraph(Scheduler, World);
        RenderWorld = new RenderWorld();
        Loop = loop ?? new FrameLoop();
        Loop.Extract.Add(new ExtractFrameSystem());
    }

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

    /// <summary>Replays a recorded run: <see cref="RunFrame()" /> takes clock and external completions from the log.</summary>
    public void Replay(FrameLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
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

            RunFrame(log.Frames[_replayIndex].Realtime);
            return;
        }

        RunFrame(Realtime.Current);
    }

    /// <summary>
    ///     Runs one frame at an explicit clock sample (headless runs, tests): drains main-thread
    ///     continuations, admits external completions, executes the loop's phases, closes the frame.
    /// </summary>
    public void RunFrame(Realtime realtime)
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

        Recording?.Add(new FrameRecord(realtime, externals));

        if (!_frameClock.DoUpdate(realtime, out _time))
        {
            _time = new Time(_time.Current, 0); // same clock sample as the previous frame
        }

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
        }
    }
}
