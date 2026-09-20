using DivisionEngine.Tests.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace DivisionEngine.Tests.Scheduling;

[TestFixture]
public sealed class SystemGroupTests
{
    private sealed class JobSystem(string name, List<string> log, int sleepMs) : IJobSystem
    {
        public double LastDelta { get; private set; }

        public void Schedule(in JobSchedulingContext context)
        {
            LastDelta = context.Time.Delta;
            context.Graph.Schedule(name, Access.Write<Health>(), ctx =>
            {
                Thread.Sleep(sleepMs);
                log.Add(name);
            });
        }
    }

    private sealed class BarrierSystem(string name, List<string> log) : ISystem
    {
        public int ThreadId { get; private set; }

        public void Execute(ref FrameContext ctx)
        {
            ThreadId = Environment.CurrentManagedThreadId;
            log.Add(name);
        }
    }

    [Test]
    public void PlainSystems_AreBarriers_BetweenJobSystems()
    {
        using var engine = new Engine(NullLogger.Instance, new JobScheduler(2));
        var log = new List<string>();
        var a = new JobSystem("A", log, 30);
        var b = new BarrierSystem("B", log);
        var c = new JobSystem("C", log, 0);
        engine.AddSystem(a);
        engine.AddSystem(b);
        engine.AddSystem(c);

        engine.RunFrame();
        engine.RunFrame();

        Assert.Multiple(() =>
        {
            Assert.That(log, Is.EqualTo(new[] { "A", "B", "C", "A", "B", "C" }));
            Assert.That(b.ThreadId, Is.EqualTo(Environment.CurrentManagedThreadId));
            Assert.That(engine.Scheduler.LiveNodeCount, Is.Zero, "frame end is a barrier");
        });
    }

    [Test]
    public void JobSystems_ReceiveTheGroupTime()
    {
        using var engine = new Engine(NullLogger.Instance, new JobScheduler(1));
        var log = new List<string>();
        var system = new JobSystem("timed", log, 0);
        engine.AddSystem(system);

        engine.RunFrame();
        Thread.Sleep(5);
        engine.RunFrame();

        Assert.That(system.LastDelta, Is.GreaterThan(0), "root group runs systems under the variable-step time");
    }
}
