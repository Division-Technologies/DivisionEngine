using DivisionEngine.Tests.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace DivisionEngine.Tests.Scheduling;

/// <summary>Moves every entity that has both components. The signature is the only place the types appear.</summary>
[EntityJob]
public partial struct Integrate
{
    public float Delta;

    private void Execute(ref Position position, in Velocity velocity)
    {
        position.X += velocity.X * Delta;
    }
}

/// <summary>Stamps each entity's own index, to check the Entity parameter.</summary>
[EntityJob]
public partial struct StampIndex
{
    private void Execute(Entity entity, ref Health health)
    {
        health.Value = entity.Index;
    }
}

/// <summary>Uses the frame's time from the job context rather than a captured field.</summary>
[EntityJob]
public partial struct AdvanceByFrameTime
{
    private void Execute(in JobContext job, ref Position position)
    {
        position.Y += (float)job.Time.Delta;
    }
}

/// <summary>A tag that only filters, so it has no place in the signature.</summary>
[Component]
[TypeId("d0c9b8a7-1234-4f56-9abc-0f1e2d3c4b50")]
public struct Disabled;

[EntityJob]
[WithNone(typeof(Disabled))]
public partial struct IntegrateActive
{
    public float Delta;

    private void Execute(ref Position position, in Velocity velocity)
    {
        position.X += velocity.X * Delta;
    }
}

/// <summary>
///     An entity job states its components once, in its <c>Execute</c> signature; the query, the
///     access declaration and the chunk loop are generated from it, so they cannot disagree.
/// </summary>
[TestFixture]
public sealed class EntityJobTests
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

    private Entity Moving(float x, float vx)
    {
        var entity = _engine.World.CreateEntity(ComponentType<Position>.Id, ComponentType<Velocity>.Id);
        _engine.World.SetComponent(entity, new Position(x, 0, 0));
        _engine.World.SetComponent(entity, new Velocity(vx, 0, 0));
        return entity;
    }

    private sealed class Runner(Action<JobSchedulingContext> schedule) : IJobSystem
    {
        public void Schedule(in JobSchedulingContext context)
        {
            schedule(context);
        }
    }

    private void RunOnce(Action<JobSchedulingContext> schedule, double seconds = 0)
    {
        _engine.AddSystem(PhaseId.Update, new Runner(schedule));
        _engine.RunFrame(Realtime.FromSeconds(seconds));
    }

    [Test]
    public void TheGeneratedJob_RunsOverEveryMatchingEntity()
    {
        var a = Moving(0, 2);
        var b = Moving(10, -1);
        var untouched = _engine.World.CreateEntity(ComponentType<Position>.Id);
        _engine.World.SetComponent(untouched, new Position(5, 0, 0));

        RunOnce(ctx => new Integrate { Delta = 0.5f }.Schedule(ctx));

        Assert.Multiple(() =>
        {
            Assert.That(_engine.World.GetComponentReadOnly<Position>(a).X, Is.EqualTo(1f));
            Assert.That(_engine.World.GetComponentReadOnly<Position>(b).X, Is.EqualTo(9.5f));
            Assert.That(_engine.World.GetComponentReadOnly<Position>(untouched).X, Is.EqualTo(5f),
                "an entity without Velocity is not matched");
        });
    }

    [Test]
    public void TheGeneratedAccessSet_MatchesWhatTheBodyTouches()
    {
        // JobSafety is on in Debug: a mismatch between the declaration and the spans the generated
        // loop takes would throw here rather than pass.
        Assert.That(JobSafety.Enabled, Is.True, "this test is only meaningful with the safety checks on");
        Moving(0, 1);

        Assert.That(() => RunOnce(ctx => new Integrate { Delta = 1 }.Schedule(ctx)), Throws.Nothing);
    }

    [Test]
    public void AnEntityParameter_ReceivesTheEntity()
    {
        var entity = _engine.World.CreateEntity(ComponentType<Health>.Id);

        RunOnce(ctx => new StampIndex().Schedule(ctx));

        Assert.That(_engine.World.GetComponentReadOnly<Health>(entity).Value, Is.EqualTo(entity.Index));
    }

    [Test]
    public void AJobContextParameter_ReceivesTheFrame()
    {
        var entity = Moving(0, 0);

        _engine.RunFrame(Realtime.FromSeconds(0));
        RunOnce(ctx => new AdvanceByFrameTime().Schedule(ctx), 0.25);

        Assert.That(_engine.World.GetComponentReadOnly<Position>(entity).Y, Is.EqualTo(0.25f).Within(1e-5f));
    }

    [Test]
    public void WithNone_ExcludesMatchingEntities()
    {
        var active = Moving(0, 1);
        var disabled = Moving(0, 1);
        _engine.World.AddComponent<Disabled>(disabled);

        RunOnce(ctx => new IntegrateActive { Delta = 1 }.Schedule(ctx));

        Assert.Multiple(() =>
        {
            Assert.That(_engine.World.GetComponentReadOnly<Position>(active).X, Is.EqualTo(1f));
            Assert.That(_engine.World.GetComponentReadOnly<Position>(disabled).X, Is.EqualTo(0f));
        });
    }

    [Test]
    public void FieldValues_AreCapturedWhenScheduled()
    {
        var entity = Moving(0, 1);

        RunOnce(ctx =>
        {
            var job = new Integrate { Delta = 2 };
            job.Schedule(ctx);
            job.Delta = 100; // after scheduling: must not affect the run
        });

        Assert.That(_engine.World.GetComponentReadOnly<Position>(entity).X, Is.EqualTo(2f));
    }
}
