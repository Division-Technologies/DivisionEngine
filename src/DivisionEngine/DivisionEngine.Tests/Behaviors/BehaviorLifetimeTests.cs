using DivisionEngine.Tests.Entities;
using DivisionEngine.Tests.Scenes;
using Microsoft.Extensions.Logging.Abstractions;

namespace DivisionEngine.Tests.Behaviors;

/// <summary>Counts its own starts and ticks once per Update, like <see cref="Counting" /> but with its own counters.</summary>
[Component]
[TypeId("7d0b3e52-61a4-4f0e-9b8e-2c5f7a1d4e00")]
public sealed class Ticking : Behavior
{
    public static int Starts;

    protected override async BehaviorTask Run(BehaviorContext context)
    {
        Interlocked.Increment(ref Starts);
        while (true)
        {
            await context.Phase(PhaseId.Update);
            var tick = await context.Write<Ticks>(context.Entity);
            tick.Value.Count++;
        }
        // ReSharper disable once FunctionNeverReturns
    }
}

/// <summary>A behavior whose Run is not async and throws straight out of the call.</summary>
[Component]
[TypeId("7d0b3e52-61a4-4f0e-9b8e-2c5f7a1d4e01")]
public sealed class ThrowsAtOnce : Behavior
{
    public static int Starts;

    protected override BehaviorTask Run(BehaviorContext context)
    {
        Interlocked.Increment(ref Starts);
        throw new InvalidOperationException("sync failure");
    }
}

/// <summary>Awaits a phase that never resumes behaviors, and records whether that was refused.</summary>
[Component]
[TypeId("7d0b3e52-61a4-4f0e-9b8e-2c5f7a1d4e02")]
public sealed class WaitsForPhysics : Behavior
{
    public static bool Refused;

    protected override async BehaviorTask Run(BehaviorContext context)
    {
        try
        {
            await context.Phase(PhaseId.Physics);
        }
        catch (InvalidOperationException)
        {
            Refused = true;
        }
    }
}

/// <summary>
///     How long a turn lives and what happens to it when something fails: removing the behavior
///     component, a Run that throws, a phase that throws, a behavior that fails next to others.
/// </summary>
[TestFixture]
public sealed class BehaviorLifetimeTests
{
    [SetUp]
    public void SetUp()
    {
        JobSafety.Enabled = true;
        Ticking.Starts = 0;
        ThrowsAtOnce.Starts = 0;
        WaitsForPhysics.Refused = false;
    }

    private static Engine NewEngine()
    {
        return new Engine(NullLogger.Instance, new JobScheduler(2));
    }

    private static double _clock;

    private static void RunFrames(Engine engine, int count)
    {
        for (var i = 0; i < count; i++)
        {
            _clock += 0.01;
            engine.RunFrame(Realtime.FromSeconds(_clock));
        }
    }

    private sealed class Script(Func<BehaviorContext, BehaviorTask> body) : Behavior
    {
        protected override BehaviorTask Run(BehaviorContext context)
        {
            return body(context);
        }
    }

    private sealed class ThrowOnce : ISystem
    {
        public bool Armed;

        public void Execute(ref FrameContext ctx)
        {
            if (Armed)
            {
                Armed = false;
                throw new InvalidOperationException("system failure");
            }
        }
    }

    [Test]
    public void RemovingTheBehaviorComponent_StopsItsTurn()
    {
        using var engine = NewEngine();
        var entity = engine.World.CreateEntity(ComponentType<Ticks>.Id);
        var behavior = new Ticking();
        engine.World.AddManagedComponent(entity, behavior);
        RunFrames(engine, 3);
        var context = behavior.Context!;

        engine.World.RemoveComponent<Ticking>(entity);
        RunFrames(engine, 1);
        var ticks = engine.World.GetComponentReadOnly<Ticks>(entity).Count;
        RunFrames(engine, 3);

        Assert.Multiple(() =>
        {
            Assert.That(context.IsCancelled, Is.True);
            Assert.That(engine.World.GetComponentReadOnly<Ticks>(entity).Count, Is.EqualTo(ticks),
                "a removed behavior does not keep writing");
            Assert.That(engine.Graph.LiveTurnCount, Is.Zero);
        });
    }

    [Test]
    public void ReplacingTheBehaviorComponent_LeavesOneTurnRunning()
    {
        using var engine = NewEngine();
        var entity = engine.World.CreateEntity(ComponentType<Ticks>.Id);
        engine.World.AddManagedComponent(entity, new Ticking());
        RunFrames(engine, 3);

        engine.World.RemoveComponent<Ticking>(entity);
        engine.World.AddManagedComponent(entity, new Ticking());
        RunFrames(engine, 2); // the replacement starts, then parks on Update
        var before = engine.World.GetComponentReadOnly<Ticks>(entity).Count;
        RunFrames(engine, 5);

        Assert.Multiple(() =>
        {
            Assert.That(Ticking.Starts, Is.EqualTo(2));
            Assert.That(engine.World.GetComponentReadOnly<Ticks>(entity).Count - before, Is.EqualTo(5),
                "one tick per frame: the old turn must not run alongside the new one");
            Assert.That(engine.Graph.LiveTurnCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void AwaitingAPhaseThatDoesNotDispatchBehaviors_FailsAtTheAwait()
    {
        using var engine = NewEngine();
        var entity = engine.World.CreateEntity();
        engine.World.AddManagedComponent(entity, new WaitsForPhysics());
        RunFrames(engine, 2);

        Assert.That(WaitsForPhysics.Refused, Is.True);
    }

    [Test]
    public void ARunThatThrowsSynchronously_IsReported_AndStartedAgain()
    {
        using var engine = NewEngine();
        var entity = engine.World.CreateEntity();
        var behavior = new ThrowsAtOnce();
        engine.World.AddManagedComponent(entity, behavior);

        for (var frame = 0; frame < 2; frame++)
        {
            Assert.That(() => RunFrames(engine, 1), Throws.InstanceOf<Exception>()
                .With.InnerException.TypeOf<BehaviorFailedException>());
            Assert.That(behavior.IsRunning, Is.False);
        }

        Assert.That(ThrowsAtOnce.Starts, Is.EqualTo(2), "a failed behavior is started again, like after a reload");
    }

    [Test]
    public void AFailingBehavior_IsNoLongerRunning()
    {
        using var engine = NewEngine();
        var entity = engine.World.CreateEntity();
        var behavior = new Script(async ctx =>
        {
            await ctx.Phase(PhaseId.Update);
            throw new InvalidOperationException("async failure");
        });
        var context = engine.Graph.Start(entity, behavior);

        RunFrames(engine, 1);
        Assert.That(() => RunFrames(engine, 1), Throws.InstanceOf<Exception>());
        Assert.Multiple(() =>
        {
            Assert.That(behavior.IsRunning, Is.False);
            Assert.That(context.Task.IsFaulted, Is.True);
            Assert.That(context.Task.Exception, Is.TypeOf<InvalidOperationException>());
        });
    }

    [Test]
    public void ASystemThatThrows_DoesNotLoseTheBehaviorsParkedOnItsPhase()
    {
        using var engine = NewEngine();
        var failing = new ThrowOnce();
        engine.AddSystem(PhaseId.Update, failing);
        var entity = engine.World.CreateEntity(ComponentType<Ticks>.Id);
        engine.World.AddManagedComponent(entity, new Ticking());
        RunFrames(engine, 2);
        var before = engine.World.GetComponentReadOnly<Ticks>(entity).Count;

        failing.Armed = true;
        Assert.That(() => RunFrames(engine, 1), Throws.InvalidOperationException);
        RunFrames(engine, 2);

        Assert.That(engine.World.GetComponentReadOnly<Ticks>(entity).Count, Is.GreaterThan(before),
            "the turn parked on Update resumes after the failed frame");
    }

    [Test]
    public void AFailingBehavior_DoesNotDropTheOtherTurnsWrites()
    {
        using var scheduler = new JobScheduler(2);
        using var world = new World();
        var graph = new JobGraph(scheduler, world);
        var target = world.CreateEntity(ComponentType<Position>.Id);
        graph.Start(world.CreateEntity(), new Script(async ctx =>
        {
            await ctx.Phase(PhaseId.Update);
            throw new InvalidOperationException("failure");
        }));
        graph.Start(world.CreateEntity(), new Script(async ctx =>
        {
            await ctx.Phase(PhaseId.Update);
            var p = await ctx.Write<Position>(target);
            p.Value.X = 5;
        }));

        Frame(graph);
        Assert.That(() => Frame(graph), Throws.TypeOf<JobFailedException>());

        Assert.That(world.GetComponentReadOnly<Position>(target).X, Is.EqualTo(5));
    }

    [Test]
    public void TwoTurnsDestroyingTheSameEntity_BothApply_AndLaterTurnsAreNotDropped()
    {
        using var scheduler = new JobScheduler(2);
        using var world = new World();
        var graph = new JobGraph(scheduler, world);
        var victim = world.CreateEntity();
        for (var i = 0; i < 3; i++)
        {
            graph.Start(world.CreateEntity(), new Script(ctx =>
            {
                ctx.Commands.DestroyEntity(victim);
                ctx.Commands.CreateEntity();
                return default;
            }));
        }

        Assert.That(() => Frame(graph), Throws.Nothing);
        Assert.That(world.EntityCount, Is.EqualTo(4 - 1 + 3));
    }

    /// <summary>
    ///     "Initial" is the state after the phase's systems. A behavior's first segment used to be
    ///     issued before the systems were scheduled, so with workers it could read the value from
    ///     before them.
    /// </summary>
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(4)]
    public void AStartingBehavior_ReadsTheStateAfterThePhasesSystems(int workers)
    {
        using var scheduler = new JobScheduler(workers);
        using var world = new World();
        var graph = new JobGraph(scheduler, world);
        var target = world.CreateEntity(ComponentType<Position>.Id);
        var query = world.Query().With<Position>().Build();
        float seen = -1;
        graph.Start(world.CreateEntity(), new Script(async ctx => { seen = (await ctx.Read<Position>(target)).X; }));

        graph.BeginPhase(PhaseId.Update);
        graph.ScheduleChunks("system", query, Access.Write<Position>(), (in _, chunk) =>
        {
            Thread.Sleep(5);
            foreach (ref var p in chunk.GetSpan<Position>())
            {
                p.X = 1;
            }
        });
        graph.ResumePhase(PhaseId.Update);
        graph.EndPhase();

        Assert.That(seen, Is.EqualTo(1));
    }

    private static void Frame(JobGraph graph)
    {
        graph.AdmitExternal();
        graph.BeginPhase(PhaseId.Update);
        graph.ResumePhase(PhaseId.Update);
        graph.EndPhase();
    }
}