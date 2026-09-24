using BenchmarkDotNet.Attributes;

namespace DivisionEngine.Benchmarks.Core;

/// <summary>
///     Per-frame cost of the time providers. Mostly a smoke benchmark that keeps the project wired
///     up; the entity storage and scheduler benchmarks are the ones that matter.
/// </summary>
[MemoryDiagnoser]
public class TimeProviderBenchmarks
{
    private const int Frames = 1000;
    private Realtime[] _frames = [];

    [GlobalSetup]
    public void Setup()
    {
        _frames = new Realtime[Frames];
        for (var i = 0; i < Frames; i++)
        {
            _frames[i] = Realtime.FromTicks(i * 16, 1000); // ~60 fps
        }
    }

    [Benchmark]
    public int FixedUpdate_1000Frames()
    {
        var provider = new FixedUpdateTimeProvider(0.02);
        var steps = 0;
        foreach (var frame in _frames)
        {
            while (provider.DoUpdate(frame, out _))
            {
                steps++;
            }
        }

        return steps;
    }

    [Benchmark]
    public int Update_1000Frames()
    {
        var provider = new UpdateTimeProvider();
        var updates = 0;
        foreach (var frame in _frames)
        {
            while (provider.DoUpdate(frame, out _))
            {
                updates++;
            }
        }

        return updates;
    }
}