using System.Numerics;
using BenchmarkDotNet.Attributes;

namespace DivisionEngine.Benchmarks.Transforms;

/// <summary>
///     Transform propagation over 100k entities, comparing a flat world (all roots, so pure
///     chunk iteration) with a hierarchy of the same size (roots walked chunk-parallel, subtrees
///     descended by chasing the sibling links). The gap between the two is what the hierarchy costs.
///     <see cref="Roots" /> is varied because the work is split over roots: a forest of many small
///     trees parallelises, a scene hanging off a single root does not.
/// </summary>
[MemoryDiagnoser]
public class TransformBenchmarks
{
    private const int N = 100_000;

    /// <summary>Children per node; 4 puts a tree of 100k entities about eight levels deep.</summary>
    [Params(4)] public int Branching;

    /// <summary>How many top-level trees the 100k entities are spread over.</summary>
    [Params(1, 1_000)] public int Roots;

    [ParamsSource(nameof(WorkerCounts))] public int Workers;

    private JobGraph _flatGraph = null!;
    private JobScheduler _flatScheduler = null!;

    private TransformPropagationSystem _flatSystem = null!;
    private World _flatWorld = null!;

    private JobGraph _treeGraph = null!;
    private JobScheduler _treeScheduler = null!;
    private TransformPropagationSystem _treeSystem = null!;
    private World _treeWorld = null!;

    public static IEnumerable<int> WorkerCounts => [0, Math.Max(1, Environment.ProcessorCount - 1)];

    [GlobalSetup]
    public void Setup()
    {
        JobSafety.Enabled = false;

        _flatScheduler = new JobScheduler(Workers);
        _flatWorld = new World();
        _flatGraph = new JobGraph(_flatScheduler, _flatWorld);
        _flatSystem = new TransformPropagationSystem();
        for (var i = 0; i < N; i++)
        {
            _flatWorld.CreateTransform(LocalTransform.FromPosition(new Vector3(i, 0, 0)));
        }

        _treeScheduler = new JobScheduler(Workers);
        _treeWorld = new World();
        _treeGraph = new JobGraph(_treeScheduler, _treeWorld);
        _treeSystem = new TransformPropagationSystem();
        BuildForest(_treeWorld, N, Roots, Branching);
    }

    /// <summary>
    ///     Builds <paramref name="roots" /> trees totalling <paramref name="count" /> entities, each one
    ///     breadth-first so that a level lands in its own run of chunks the way an authored scene would.
    /// </summary>
    private static void BuildForest(World world, int count, int roots, int branching)
    {
        var local = LocalTransform.FromPosition(new Vector3(0.001f, 0.002f, 0.003f));
        var frontier = new Queue<Entity>();
        var attached = 0;

        for (var created = 0; created < count; created++)
        {
            var entity = world.CreateTransform(local);

            // The first `roots` entities stay roots; everything after fills the trees breadth-first.
            if (created >= roots)
            {
                world.SetParent(entity, frontier.Peek());
                if (++attached == branching)
                {
                    frontier.Dequeue();
                    attached = 0;
                }
            }

            frontier.Enqueue(entity);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _flatScheduler.Dispose();
        _flatWorld.Dispose();
        _treeScheduler.Dispose();
        _treeWorld.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void Propagate_Flat_100k()
    {
        Propagate(_flatSystem, _flatGraph, _flatWorld);
    }

    [Benchmark]
    public void Propagate_Hierarchy_100k()
    {
        Propagate(_treeSystem, _treeGraph, _treeWorld);
    }

    private static void Propagate(TransformPropagationSystem system, JobGraph graph, World world)
    {
        graph.BeginPhase(PhaseId.TransformPropagation, false);
        system.Schedule(graph, world);
        graph.EndPhase();
        graph.EndFrame();
    }
}