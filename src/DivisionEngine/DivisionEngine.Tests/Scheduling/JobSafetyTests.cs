using DivisionEngine.Tests.Entities;

namespace DivisionEngine.Tests.Scheduling;

[TestFixture]
public sealed class JobSafetyTests
{
    private bool _enabledBefore;
    private JobScheduler _scheduler = null!;
    private World _world = null!;
    private JobGraph _graph = null!;

    [SetUp]
    public void SetUp()
    {
        _enabledBefore = JobSafety.Enabled;
        JobSafety.Enabled = true;
        _scheduler = new JobScheduler(2);
        _world = new World();
        _graph = new JobGraph(_scheduler, _world);
        for (var i = 0; i < 10; i++)
        {
            _world.CreateEntity(ComponentType<Position>.Id, ComponentType<Health>.Id, ComponentType<Name>.Id);
        }
    }

    [TearDown]
    public void TearDown()
    {
        _scheduler.Dispose();
        _world.Dispose();
        JobSafety.Enabled = _enabledBefore;
    }

    private static void AssertViolation(TestDelegate action)
    {
        Assert.That(action, Throws.TypeOf<JobFailedException>().With.InnerException.TypeOf<JobAccessViolationException>());
    }

    [Test]
    public void UndeclaredWrite_ThroughChunk_IsRejected()
    {
        var query = _world.Query().With<Position>().Build();
        var handle = _graph.ScheduleChunks("read-only", query, Access.Read<Position>(), (in _, chunk) => chunk.GetSpan<Position>());
        AssertViolation(() => _graph.Wait(handle));
    }

    [Test]
    public void DeclaredRead_AllowsReadOnlyAccess()
    {
        var query = _world.Query().With<Position>().Build();
        var sum = 0f;
        var handle = _graph.ScheduleChunks("read-only", query, Access.Read<Position>().Read<Name>(), (in _, chunk) =>
        {
            foreach (var p in chunk.GetReadOnlySpan<Position>())
            {
                sum += p.X;
            }

            var names = chunk.GetManagedReadOnlySpan<Name>();
            sum += names.Length;
        });
        Assert.That(() => _graph.Wait(handle), Throws.Nothing);
    }

    [Test]
    public void UndeclaredComponent_ThroughWorld_IsRejected()
    {
        Entity target = default;
        foreach (var chunk in _world.Query().With<Health>().Build())
        {
            target = chunk.Entities[0];
        }

        var write = _graph.Schedule("write health", Access.Read<Position>(), ctx => ctx.World.GetComponent<Health>(target).Value = 1);
        AssertViolation(() => _graph.Wait(write));

        var readOnly = _graph.Schedule("read health", Access.Read<Health>(), ctx => _ = ctx.World.GetComponentReadOnly<Health>(target).Value);
        Assert.That(() => _graph.Wait(readOnly), Throws.Nothing);

        var none = _graph.Schedule("none", AccessSet.None, ctx => ctx.World.IsAlive(target));
        Assert.That(() => _graph.Wait(none), Throws.Nothing, "IsAlive is a pure query and never asserts");

        var has = _graph.Schedule("has", AccessSet.None, ctx => ctx.World.HasComponent<Health>(target));
        AssertViolation(() => _graph.Wait(has));
    }

    [Test]
    public void StructuralChange_RequiresStructureWrite()
    {
        var create = _graph.Schedule("create", Access.Write<Position>(), ctx => ctx.World.CreateEntity());
        AssertViolation(() => _graph.Wait(create));

        var exclusive = _graph.Schedule("exclusive", AccessSet.Exclusive, ctx =>
        {
            var e = ctx.World.CreateEntity();
            ctx.World.AddComponent(e, new Health { Value = 3 });
            ctx.World.DestroyEntity(e);
        });
        Assert.That(() => _graph.Wait(exclusive), Throws.Nothing);

        var structureOnly = _graph.Schedule("structure only", Access.WriteStructure(), ctx => ctx.World.CreateEntity());
        Assert.That(() => _graph.Wait(structureOnly), Throws.Nothing);
    }

    [Test]
    public void OutsideJobs_NothingIsChecked()
    {
        Assert.That(JobSafety.Current, Is.Null);
        foreach (var chunk in _world.Query().With<Position>().Build())
        {
            Assert.That(() => chunk.GetSpan<Position>(), Throws.Nothing);
        }
    }

    [Test]
    public void Disabled_SkipsChecks()
    {
        JobSafety.Enabled = false;
        var query = _world.Query().With<Position>().Build();
        var handle = _graph.ScheduleChunks("unchecked", query, AccessSet.None, (in _, chunk) => chunk.GetSpan<Position>());
        Assert.That(() => _graph.Wait(handle), Throws.Nothing);
    }
}
