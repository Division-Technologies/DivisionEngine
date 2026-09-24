using BenchmarkDotNet.Attributes;
using DivisionEngine.Benchmarks.Entities;

namespace DivisionEngine.Benchmarks.Behaviors;

/// <summary>
///     Behavior lane baseline: the per-segment cost of dynamic issue (entity-level
///     dependency inference + dispatch + async resume) and the cost of resuming a phase.
/// </summary>
[MemoryDiagnoser]
public class BehaviorBenchmarks
{
    private const int Behaviors = 1_000;
    private const int SegmentsPerFrame = 4;

    [ParamsSource(nameof(WorkerCounts))] public int Workers;

    private JobGraph _graph = null!;
    private JobScheduler _scheduler = null!;
    private World _world = null!;

    public static IEnumerable<int> WorkerCounts => [0, Math.Max(1, Environment.ProcessorCount - 1)];

    [GlobalSetup]
    public void Setup()
    {
        JobSafety.Enabled = false;
        _scheduler = new JobScheduler(Workers);
        _world = new World();
        _graph = new JobGraph(_scheduler, _world);
        var targets = new Entity[64];
        for (var i = 0; i < targets.Length; i++)
        {
            targets[i] = _world.CreateEntity(ComponentType<Position>.Id);
        }

        for (var i = 0; i < Behaviors; i++)
        {
            _graph.Start(_world.CreateEntity(), new Reader(targets[i % targets.Length]));
        }

        _graph.EndFrame(); // everyone is parked on Update
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _scheduler.Dispose();
        _world.Dispose();
    }

    /// <summary>One frame: resume 1000 behaviors, each running 1 + 4 segments (4 entity-level reads).</summary>
    [Benchmark]
    public void Frame_1000Behaviors_5SegmentsEach()
    {
        _graph.BeginPhase(PhaseId.Update);
        _graph.ResumePhase(PhaseId.Update);
        _graph.EndFrame(); // ends the phase (commit) and clears the tracker, as the engine loop does
    }

    private sealed class Reader(Entity target) : Behavior
    {
        public float Sum;

        protected override async BehaviorTask Run(BehaviorContext context)
        {
            while (true)
            {
                await context.Phase(PhaseId.Update);
                for (var i = 0; i < SegmentsPerFrame; i++)
                {
                    var position = await context.Read<Position>(target);
                    Sum += position.X;
                }
            }
            // ReSharper disable once FunctionNeverReturns
        }
    }
}