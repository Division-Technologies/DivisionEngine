using DivisionEngine.Tests.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace DivisionEngine.Tests.Core;

/// <summary>
///     Recording the clock samples and the external completions admitted per frame, then feeding
///     them back, must reproduce a run whose behaviors await real timers.
/// </summary>
[TestFixture]
public sealed class ReplayTests
{
    private const int Frames = 40;
    private const int Behaviors = 24;

    private sealed class TimerDriven(Entity shared, Entity own) : Behavior
    {
        protected override async BehaviorTask Run(BehaviorContext context)
        {
            while (true)
            {
                // Real, jittery timing: which frame this completes in differs from run to run.
                await Task.Delay(Random.Shared.Next(1, 6));
                await context.Phase(PhaseId.Update);
                var frameTime = (float)context.Graph.Time.Current;
                context.Modify<Health>(shared, h => new Health { Value = h.Value * 3 % 1000 + context.TurnId });
                var p = await context.Write<Position>(own);
                p.Value.X += 1;
                p.Value.Y = frameTime;
            }
            // ReSharper disable once FunctionNeverReturns
        }
    }

    private static (Engine engine, Entity shared, Entity[] owns) Build()
    {
        var engine = new Engine(NullLogger.Instance, new JobScheduler(3));
        var shared = engine.World.CreateEntity(ComponentType<Health>.Id);
        engine.World.SetComponent(shared, new Health { Value = 1 });
        var owns = new Entity[Behaviors];
        for (var i = 0; i < Behaviors; i++)
        {
            owns[i] = engine.World.CreateEntity(ComponentType<Position>.Id);
            engine.Graph.Start(owns[i], new TimerDriven(shared, owns[i]));
        }

        return (engine, shared, owns);
    }

    private static ulong Hash(Engine engine, Entity shared, Entity[] owns)
    {
        var hash = 14695981039346656037UL;

        void Mix(long value)
        {
            hash = (hash ^ (ulong)value) * 1099511628211UL;
        }

        Mix(engine.World.GetComponent<Health>(shared).Value);
        foreach (var own in owns)
        {
            var p = engine.World.GetComponent<Position>(own);
            Mix(BitConverter.SingleToInt32Bits(p.X));
            Mix(BitConverter.SingleToInt32Bits(p.Y));
        }

        return hash;
    }

    [Test]
    public void Replay_ReproducesARun_WithRealTimers()
    {
        ulong recorded;
        int sharedValue;
        FrameLog log;
        var (engine, shared, owns) = Build();
        using (engine)
        {
            log = engine.StartRecording();
            for (var frame = 0; frame < Frames; frame++)
            {
                Thread.Sleep(2);
                engine.RunFrame();
            }

            engine.StopRecording();
            recorded = Hash(engine, shared, owns);
            sharedValue = engine.World.GetComponent<Health>(shared).Value;
        }

        Assert.Multiple(() =>
        {
            Assert.That(log.Count, Is.EqualTo(Frames));
            Assert.That(log.Frames.Sum(f => f.Externals.Length), Is.GreaterThan(0), "timers completed during the run");
            Assert.That(sharedValue, Is.Not.EqualTo(1), "behaviors acted");
        });

        // A different run without the log almost certainly differs (timers) — not asserted, timing-dependent.
        // Replaying the log must match exactly, even though the timers fire at different real times.
        var (replay, replayShared, replayOwns) = Build();
        using (replay)
        {
            replay.Replay(log);
            while (!replay.IsReplayComplete)
            {
                replay.RunFrame();
            }

            Assert.Multiple(() =>
            {
                Assert.That(replay.FrameIndex, Is.EqualTo(Frames));
                Assert.That(Hash(replay, replayShared, replayOwns), Is.EqualTo(recorded));
                Assert.That(() => replay.RunFrame(), Throws.InvalidOperationException, "log exhausted");
            });
        }
    }

    [Test]
    public void Replay_UsesTheRecordedClock()
    {
        var log = new FrameLog();
        log.Add(new FrameRecord(Realtime.FromTicks(1000, 1000), []));
        log.Add(new FrameRecord(Realtime.FromTicks(1250, 1000), []));

        using var engine = new Engine(NullLogger.Instance, new JobScheduler(1));
        var times = new List<double>();
        engine.AddSystem(PhaseId.Update, new TimeProbe(times));
        engine.Replay(log);
        engine.RunFrame();
        engine.RunFrame();

        Assert.That(times, Is.EqualTo(new[] { 0.0, 0.25 }).Within(1e-9));
    }

    [Test]
    public void Replay_RequiresAFreshEngine_AndTheLogsClock()
    {
        var log = new FrameLog();
        log.Add(new FrameRecord(Realtime.FromTicks(1000, 1000), []));

        using (var used = new Engine(NullLogger.Instance, new JobScheduler(1)))
        {
            used.RunFrame(Realtime.FromTicks(0, 1000));
            Assert.That(() => used.Replay(log), Throws.InvalidOperationException,
                "the clocks are not rewound, so a used engine cannot reproduce the run");
        }

        using var engine = new Engine(NullLogger.Instance, new JobScheduler(1));
        engine.Replay(log);
        Assert.That(() => engine.RunFrame(Realtime.FromTicks(5000, 1000)), Throws.InvalidOperationException,
            "while replaying, the clock comes from the log");
    }

    private sealed class TimeProbe(List<double> times) : ISystem
    {
        public void Execute(ref FrameContext ctx)
        {
            times.Add(ctx.ActiveTime.Current);
        }
    }
}