using DivisionEngine.Tests.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace DivisionEngine.Tests.Scheduling;

/// <summary>
///     Structural changes are recorded during a phase and applied at its boundary, where nothing is
///     reading chunks. What these pin down is when the change becomes visible and in what order
///     several recorders are applied.
/// </summary>
[TestFixture]
public sealed class StructuralChangeTests
{
    private Engine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new Engine(NullLogger.Instance, new JobScheduler(2));
    }

    [TearDown]
    public void TearDown()
    {
        _engine.Dispose();
    }

    private static Realtime At(double seconds)
    {
        return Realtime.FromSeconds(seconds);
    }

    private int CountOf<T>()
    {
        return _engine.World.Query().With<T>().Build().CalculateEntityCount();
    }

    private sealed class SpawningSystem(int count) : IJobSystem
    {
        public bool Done;

        public void Schedule(in JobSchedulingContext context)
        {
            if (Done)
            {
                return;
            }

            Done = true;
            var commands = context.Graph.Commands;
            context.Graph.Schedule("spawn", Access.Write(commands.Resource), _ =>
            {
                for (var i = 0; i < count; i++)
                {
                    var e = commands.CreateEntity();
                    commands.AddComponent(e, new Health { Value = i });
                }
            });
        }
    }

    [Test]
    public void ASystemsRecordedChanges_AreAppliedAtThePhaseBoundary()
    {
        _engine.AddSystem(PhaseId.Update, new SpawningSystem(3));

        Assert.That(CountOf<Health>(), Is.EqualTo(0));
        _engine.RunFrame(At(0));

        Assert.That(CountOf<Health>(), Is.EqualTo(3), "applied by the end of the frame");
    }

    [Test]
    public void TheGraphsBuffer_IsReusableAcrossFrames()
    {
        var system = new SpawningSystem(2);
        _engine.AddSystem(PhaseId.Update, system);

        _engine.RunFrame(At(0));
        system.Done = false;
        _engine.RunFrame(At(0.01));

        Assert.Multiple(() =>
        {
            Assert.That(CountOf<Health>(), Is.EqualTo(4), "the second frame's spawns were applied too");
            Assert.That(_engine.Graph.Commands.IsEmpty, Is.True, "playback clears the buffer");
        });
    }

    private sealed class Spawner(int count) : Behavior
    {
        protected override async BehaviorTask Run(BehaviorContext context)
        {
            await context.Phase(PhaseId.Update);
            for (var i = 0; i < count; i++)
            {
                var e = context.Commands.CreateEntity();
                context.Commands.AddComponent(e, new Health { Value = context.TurnId });
            }

            while (true)
            {
                await context.Phase(PhaseId.Update);
            }
            // ReSharper disable once FunctionNeverReturns
        }
    }

    [Test]
    public void ABehaviorCanRecordStructuralChanges()
    {
        var host = _engine.World.CreateEntity();
        _engine.Graph.Start(host, new Spawner(2));

        _engine.RunFrame(At(0));
        _engine.RunFrame(At(0.01));

        Assert.That(CountOf<Health>(), Is.EqualTo(2));
    }

    [Test]
    public void SeveralBehaviorsApply_InTurnOrder_NotInSegmentOrder()
    {
        // Three turns each spawn one entity stamped with their turn id. Turn ids ascend with start
        // order, so the health values must come out ascending however the segments were scheduled.
        var first = _engine.Graph.Start(_engine.World.CreateEntity(), new Spawner(1));
        var second = _engine.Graph.Start(_engine.World.CreateEntity(), new Spawner(1));
        var third = _engine.Graph.Start(_engine.World.CreateEntity(), new Spawner(1));

        _engine.RunFrame(At(0));
        _engine.RunFrame(At(0.01));

        var stamps = new List<int>();
        foreach (var chunk in _engine.World.Query().With<Health>().Build())
        {
            foreach (var health in chunk.GetReadOnlySpan<Health>())
            {
                stamps.Add(health.Value);
            }
        }

        Assert.That(stamps, Is.EqualTo(new[] { first.TurnId, second.TurnId, third.TurnId }));
    }

    private sealed class SpawnThenLook(Entity report) : Behavior
    {
        protected override async BehaviorTask Run(BehaviorContext context)
        {
            await context.Phase(PhaseId.Update);
            context.Commands.CreateEntity();

            // Still inside the phase that recorded it: the world must not show it yet.
            var duringWrite = await context.Write<Health>(report);
            duringWrite.Value.Value = context.World.EntityCount;

            await context.Phase(PhaseId.PostUpdate);
            var afterWrite = await context.Write<Velocity>(report);
            afterWrite.Value.X = context.World.EntityCount;

            while (true)
            {
                await context.Phase(PhaseId.Update);
            }
            // ReSharper disable once FunctionNeverReturns
        }
    }

    [Test]
    public void ARecordedEntity_IsVisibleFromTheNextPhase()
    {
        var report = _engine.World.CreateEntity(ComponentType<Health>.Id, ComponentType<Velocity>.Id);
        _engine.Graph.Start(_engine.World.CreateEntity(), new SpawnThenLook(report));

        _engine.RunFrame(At(0));
        _engine.RunFrame(At(0.01));

        var before = _engine.World.GetComponentReadOnly<Health>(report).Value;
        var after = (int)_engine.World.GetComponentReadOnly<Velocity>(report).X;

        Assert.Multiple(() =>
        {
            Assert.That(before, Is.EqualTo(2), "the report entity and the behavior's own host, and nothing else yet");
            Assert.That(after, Is.EqualTo(3), "the recorded entity has arrived by the next phase");
        });
    }
}
