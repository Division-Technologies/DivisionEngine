using BenchmarkDotNet.Attributes;
using DivisionEngine.Benchmarks.Entities;

namespace DivisionEngine.Benchmarks.Behaviours;

/// <summary>
///     Behaviour lane baseline (plan M3): the per-segment cost of dynamic issue (entity-level
///     dependency inference + dispatch + async resume) and the cost of resuming a phase.
/// </summary>
[MemoryDiagnoser]
public class BehaviourBenchmarks
{
    private const int Behaviours = 1_000;
    private const int SegmentsPerFrame = 4;

    private JobGraph _graph = null!;
    private JobScheduler _scheduler = null!;
    private World _world = null!;

    [ParamsSource(nameof(WorkerCounts))] public int Workers;

    public static IEnumerable<int> WorkerCounts => [0, Math.Max(1, Environment.ProcessorCount - 1)];

    private sealed class Reader(Entity target) : Behaviour
    {
        public float Sum;

        protected override async BehaviourTask Run(BehaviourContext context)
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

        for (var i = 0; i < Behaviours; i++)
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

    /// <summary>One frame: resume 1000 behaviours, each running 1 + 4 segments (4 entity-level reads).</summary>
    [Benchmark]
    public void Frame_1000Behaviours_5SegmentsEach()
    {
        _graph.BeginPhase(PhaseId.Update);
        _graph.ResumePhase(PhaseId.Update);
        _graph.EndFrame(); // ends the phase (commit) and clears the tracker, as the engine loop does
    }
}
