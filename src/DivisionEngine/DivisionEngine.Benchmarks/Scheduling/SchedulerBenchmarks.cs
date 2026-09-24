using BenchmarkDotNet.Attributes;
using DivisionEngine.Benchmarks.Entities;

namespace DivisionEngine.Benchmarks.Scheduling;

/// <summary>
///     Job system baseline: per-issue dependency inference and dispatch overhead, and the
///     chunk-parallel speedup of the integrate loop from the entity benchmarks.
/// </summary>
[MemoryDiagnoser]
public class SchedulerBenchmarks
{
    private const int N = 100_000;

    [ParamsSource(nameof(WorkerCounts))] public int Workers;

    private AccessSet[] _accessSets = [];
    private JobGraph _graph = null!;
    private EntityQuery _query = null!;
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
        for (var i = 0; i < N; i++)
        {
            var e = _world.CreateEntity(ComponentType<Position>.Id, ComponentType<Velocity>.Id);
            _world.SetComponent(e, new Velocity { X = 1, Y = 2, Z = 3 });
        }

        _query = _world.Query().With<Position>().With<Velocity>().Build();
        _accessSets =
        [
            Access.Read<Position>(),
            Access.Write<Position>(),
            Access.Read<Velocity>().Read<Position>(),
            Access.Write<Velocity>(),
            Access.Read<Health>(),
            Access.Write<Health>().Read<Position>()
        ];
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _scheduler.Dispose();
        _world.Dispose();
    }

    /// <summary>1000 trivial jobs with a mix of read/write declarations: measures issue + inference + dispatch per job.</summary>
    [Benchmark]
    public void Issue_1000_TrivialJobs()
    {
        for (var i = 0; i < 1_000; i++)
        {
            _graph.Schedule("noop", _accessSets[i % _accessSets.Length], static _ => { });
        }

        _graph.EndFrame();
    }

    /// <summary>Chunk-parallel integrate over 100k entities, same body as EntityBenchmarks.Iterate_Integrate.</summary>
    [Benchmark]
    public void ChunkIntegrate_100k()
    {
        _graph.ScheduleChunks("integrate", _query, Access.Read<Velocity>().Write<Position>(), static (in ctx, chunk) =>
        {
            const float dt = 1f / 60f;
            var positions = chunk.GetSpan<Position>();
            var velocities = chunk.GetReadOnlySpan<Velocity>();
            for (var i = 0; i < positions.Length; i++)
            {
                positions[i].X += velocities[i].X * dt;
                positions[i].Y += velocities[i].Y * dt;
                positions[i].Z += velocities[i].Z * dt;
            }
        });
        _graph.EndFrame();
    }

    /// <summary>Four dependent chunk passes per frame (write → read → write → read), the typical shape of a frame.</summary>
    [Benchmark]
    public void ChunkPipeline_4Passes_100k()
    {
        for (var pass = 0; pass < 4; pass++)
        {
            var access = pass % 2 == 0
                ? Access.Read<Velocity>().Write<Position>()
                : Access.Read<Position>().Write<Velocity>();
            _graph.ScheduleChunks("pass", _query, access, static (in ctx, chunk) =>
            {
                var positions = chunk.GetSpan<Position>();
                for (var i = 0; i < positions.Length; i++)
                {
                    positions[i].X += 1f;
                }
            });
        }

        _graph.EndFrame();
    }
}