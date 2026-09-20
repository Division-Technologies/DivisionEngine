using Microsoft.Extensions.Logging.Abstractions;

namespace DivisionEngine.Tests.Scenes;

/// <summary>A behaviour whose only state is a component, as the convention requires.</summary>
[Component]
[TypeId("4f27c1d8-9a30-4b6e-8c11-5d02e7f3a100")]
public sealed class Counting : Behaviour
{
    public static int Starts;

    protected override async BehaviourTask Run(BehaviourContext context)
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

/// <summary>A behaviour that does carry a field, so [AutoSerialization] takes over from the empty default.</summary>
[Component]
[AutoSerialization]
[TypeId("4f27c1d8-9a30-4b6e-8c11-5d02e7f3a101")]
public sealed partial class Labelled : Behaviour
{
    [Serialize] public string Label = "";

    protected override async BehaviourTask Run(BehaviourContext context)
    {
        while (true)
        {
            await context.Phase(PhaseId.Update);
        }
        // ReSharper disable once FunctionNeverReturns
    }
}

[Component]
[AutoSerialization]
[TypeId("4f27c1d8-9a30-4b6e-8c11-5d02e7f3a102")]
public partial struct Ticks
{
    [Serialize] public int Count;
}

/// <summary>
///     A behaviour is a component, so what persists is "this entity has this behaviour" rather than
///     the turn itself. Everything that rebuilds a world — a scene load, a script reload — therefore
///     gets its behaviours running again for free.
/// </summary>
[TestFixture]
public sealed class BehaviourComponentTests
{
    [SetUp]
    public void SetUp()
    {
        Counting.Starts = 0;
    }

    private static Engine NewEngine()
    {
        return new Engine(NullLogger.Instance, new JobScheduler(2));
    }

    private static void RunFrames(Engine engine, int count, double start = 0)
    {
        for (var i = 0; i < count; i++)
        {
            engine.RunFrame(Realtime.FromSeconds(start + i * 0.01));
        }
    }

    [Test]
    public void AddingABehaviourComponent_StartsIt()
    {
        using var engine = NewEngine();
        var entity = engine.World.CreateEntity(ComponentType<Ticks>.Id);
        engine.World.AddManagedComponent(entity, new Counting());

        RunFrames(engine, 3);

        Assert.Multiple(() =>
        {
            Assert.That(Counting.Starts, Is.EqualTo(1), "started once, not once per frame");
            Assert.That(engine.World.GetComponentReadOnly<Ticks>(entity).Count, Is.GreaterThan(0));
        });
    }

    [Test]
    public void ARunningBehaviour_IsNotStartedAgain()
    {
        using var engine = NewEngine();
        var entity = engine.World.CreateEntity(ComponentType<Ticks>.Id);
        engine.World.AddManagedComponent(entity, new Counting());

        RunFrames(engine, 10);

        Assert.That(Counting.Starts, Is.EqualTo(1));
    }

    [Test]
    public void ACancelledBehaviour_IsStartedAgainOnTheNextFrame()
    {
        using var engine = NewEngine();
        var entity = engine.World.CreateEntity(ComponentType<Ticks>.Id);
        var behaviour = new Counting();
        engine.World.AddManagedComponent(entity, behaviour);
        RunFrames(engine, 2);
        Assert.That(Counting.Starts, Is.EqualTo(1));

        behaviour.Context!.Cancel();
        RunFrames(engine, 2, 0.1);

        Assert.That(Counting.Starts, Is.EqualTo(2), "the component is still there, so it runs again");
    }

    [Test]
    public void ABehaviourSurvivesASceneRoundTrip_AndRunsInTheNewWorld()
    {
        using var source = NewEngine();
        var entity = source.World.CreateEntity(ComponentType<Ticks>.Id);
        source.World.AddManagedComponent(entity, new Counting());
        RunFrames(source, 3);
        var ticksBefore = source.World.GetComponentReadOnly<Ticks>(entity).Count;
        Assert.That(ticksBefore, Is.GreaterThan(0));

        var scene = EntityScene.FromBytes(EntityScene.CaptureFrom(source.World).ToBytes());

        Counting.Starts = 0;
        using var target = NewEngine();
        var created = scene.ApplyTo(target.World);
        RunFrames(target, 3);

        Assert.Multiple(() =>
        {
            Assert.That(Counting.Starts, Is.EqualTo(1), "the loaded behaviour started in the new world");
            Assert.That(target.World.GetComponentReadOnly<Ticks>(created[0]).Count,
                Is.GreaterThan(ticksBefore), "it carried its tick count and kept going");
        });
    }

    [Test]
    public void ABehavioursOwnFieldsAreSaved_WhenItOptsIn()
    {
        using var source = NewEngine();
        var entity = source.World.CreateEntity();
        source.World.AddManagedComponent(entity, new Labelled { Label = "patrol" });

        var scene = EntityScene.FromBytes(EntityScene.CaptureFrom(source.World).ToBytes());

        using var target = NewEngine();
        var created = scene.ApplyTo(target.World);

        Assert.That(target.World.GetManagedComponent<Labelled>(created[0]).Label, Is.EqualTo("patrol"));
    }

    [Test]
    public void ApplyingASceneTwice_GivesEachEntityItsOwnBehaviourInstance()
    {
        using var source = NewEngine();
        var entity = source.World.CreateEntity();
        source.World.AddManagedComponent(entity, new Labelled { Label = "a" });
        var scene = EntityScene.CaptureFrom(source.World);

        using var target = NewEngine();
        var first = scene.ApplyTo(target.World);
        var second = scene.ApplyTo(target.World);

        var one = target.World.GetManagedComponent<Labelled>(first[0]);
        var two = target.World.GetManagedComponent<Labelled>(second[0]);

        Assert.Multiple(() =>
        {
            Assert.That(one, Is.Not.SameAs(two), "two entities must not share one behaviour object");
            Assert.That(two.Label, Is.EqualTo("a"));
        });
    }

    [Test]
    public void AWorldReloadRoundTrip_RestartsBehaviours()
    {
        using var engine = NewEngine();
        var entity = engine.World.CreateEntity(ComponentType<Ticks>.Id);
        engine.World.AddManagedComponent(entity, new Counting());
        RunFrames(engine, 3);
        Assert.That(Counting.Starts, Is.EqualTo(1));

        // The engine-side half of a script reload, without an actual assembly swap.
        engine.Graph.CancelAllTurns();
        var snapshot = WorldReload.BeforeSwap(engine.World);
        WorldReload.AfterSwap(engine.World, snapshot);

        RunFrames(engine, 3, 0.1);

        Assert.That(Counting.Starts, Is.EqualTo(2), "the rebuilt world starts the behaviour again");
    }
}
