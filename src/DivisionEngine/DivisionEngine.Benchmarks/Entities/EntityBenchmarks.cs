using BenchmarkDotNet.Attributes;

namespace DivisionEngine.Benchmarks.Entities;

public struct Position
{
    public float X, Y, Z;
}

public struct Velocity
{
    public float X, Y, Z;
}

public struct Health
{
    public int Value;
}

/// <summary>
///     Entity storage baseline (plan M1): creation, query iteration and add/remove churn at 100k
///     entities. Iteration is the number that matters for the scheduler (M2) later.
/// </summary>
[MemoryDiagnoser]
public class EntityBenchmarks
{
    [Params(100_000)] public int N;

    private Entity[] _entities = [];
    private EntityQuery _query = null!;
    private World _world = null!;

    [GlobalSetup]
    public void Setup()
    {
        _world = new World();
        _entities = new Entity[N];
        for (var i = 0; i < N; i++)
        {
            var e = _world.CreateEntity(ComponentType<Position>.Id, ComponentType<Velocity>.Id);
            _world.SetComponent(e, new Velocity { X = 1, Y = 2, Z = 3 });
            _entities[i] = e;
        }

        _query = _world.Query().With<Position>().With<Velocity>().Build();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _world.Dispose();
    }

    [Benchmark]
    public void Create_Destroy_N()
    {
        using var world = new World();
        for (var i = 0; i < N; i++)
        {
            world.CreateEntity(ComponentType<Position>.Id, ComponentType<Velocity>.Id);
        }
    }

    [Benchmark(Baseline = true)]
    public float Iterate_Integrate()
    {
        const float dt = 1f / 60f;
        var checksum = 0f;
        foreach (var chunk in _query)
        {
            var positions = chunk.GetSpan<Position>();
            var velocities = chunk.GetReadOnlySpan<Velocity>();
            for (var i = 0; i < positions.Length; i++)
            {
                positions[i].X += velocities[i].X * dt;
                positions[i].Y += velocities[i].Y * dt;
                positions[i].Z += velocities[i].Z * dt;
                checksum += positions[i].X;
            }
        }

        return checksum;
    }

    [Benchmark]
    public void AddRemove_Churn_10k()
    {
        for (var i = 0; i < 10_000; i++)
        {
            _world.AddComponent(_entities[i], new Health { Value = i });
        }

        for (var i = 0; i < 10_000; i++)
        {
            _world.RemoveComponent<Health>(_entities[i]);
        }
    }

    [Benchmark]
    public int RandomAccess_GetComponent_100k()
    {
        var sum = 0;
        foreach (var e in _entities)
        {
            sum += (int)_world.GetComponent<Velocity>(e).X;
        }

        return sum;
    }
}
