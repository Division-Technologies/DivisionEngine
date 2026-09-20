namespace DivisionEngine.Tests.Entities;

[TestFixture]
public sealed class QueryTests
{
    private static Entity Make(World world, params ReadOnlySpan<ComponentTypeId> types)
    {
        return world.CreateEntity(types);
    }

    [Test]
    public void With_MatchesAcrossArchetypes()
    {
        using var world = new World();
        Make(world, ComponentType<Position>.Id);
        Make(world, ComponentType<Position>.Id, ComponentType<Velocity>.Id);
        Make(world, ComponentType<Velocity>.Id);
        Make(world);

        var query = world.Query().With<Position>().Build();
        Assert.Multiple(() =>
        {
            Assert.That(query.CalculateEntityCount(), Is.EqualTo(2));
            Assert.That(query.CalculateChunkCount(), Is.EqualTo(2));
            Assert.That(world.Query().With<Position>().With<Velocity>().Build().CalculateEntityCount(), Is.EqualTo(1));
            Assert.That(world.Query().Build().CalculateEntityCount(), Is.EqualTo(4), "empty query matches everything");
        });
    }

    [Test]
    public void Without_And_WithAny()
    {
        using var world = new World();
        Make(world, ComponentType<Position>.Id);
        Make(world, ComponentType<Position>.Id, ComponentType<Velocity>.Id);
        Make(world, ComponentType<Position>.Id, ComponentType<Frozen>.Id);
        Make(world, ComponentType<Health>.Id);

        Assert.Multiple(() =>
        {
            Assert.That(world.Query().With<Position>().Without<Velocity>().Build().CalculateEntityCount(), Is.EqualTo(2));
            Assert.That(world.Query().WithAny<Velocity>().WithAny<Frozen>().Build().CalculateEntityCount(), Is.EqualTo(2));
            Assert.That(world.Query().With<Position>().WithAny<Health>().Build().CalculateEntityCount(), Is.Zero);
        });
    }

    [Test]
    public void SameDescription_ReturnsCachedQuery()
    {
        using var world = new World();
        var a = world.Query().With<Position>().Without<Velocity>().Build();
        var b = world.Query().Without<Velocity>().With<Position>().With<Position>().Build();
        Assert.That(a, Is.SameAs(b));
    }

    [Test]
    public void Query_SeesArchetypesCreatedLater()
    {
        using var world = new World();
        var query = world.Query().With<Position>().Build();
        Assert.That(query.CalculateEntityCount(), Is.Zero);

        Make(world, ComponentType<Position>.Id, ComponentType<Health>.Id);
        Assert.That(query.CalculateEntityCount(), Is.EqualTo(1));
    }

    [Test]
    public void Enumeration_VisitsEveryEntityOnce_AndAllowsWrites()
    {
        using var world = new World();
        var entities = new Entity[1_000];
        for (var i = 0; i < entities.Length; i++)
        {
            entities[i] = Make(world, ComponentType<Position>.Id, ComponentType<Velocity>.Id);
            world.SetComponent(entities[i], new Velocity(1, 2, 3));
        }

        var visited = new HashSet<Entity>();
        foreach (var chunk in world.Query().With<Position>().With<Velocity>().Build())
        {
            var positions = chunk.GetSpan<Position>();
            var velocities = chunk.GetReadOnlySpan<Velocity>();
            for (var i = 0; i < chunk.Count; i++)
            {
                positions[i].X += velocities[i].X;
                positions[i].Y += velocities[i].Y;
                Assert.That(visited.Add(chunk.Entities[i]), Is.True, "visited twice");
            }
        }

        Assert.That(visited, Has.Count.EqualTo(entities.Length));
        Assert.That(world.GetComponent<Position>(entities[500]).Y, Is.EqualTo(2));
    }

    [Test]
    public void StructuralChange_DuringEnumeration_Throws()
    {
        using var world = new World();
        Make(world, ComponentType<Position>.Id);
        Make(world, ComponentType<Position>.Id);

        Assert.That(() =>
        {
            foreach (var chunk in world.Query().With<Position>().Build())
            {
                world.DestroyEntity(chunk.Entities[0]);
            }
        }, Throws.InvalidOperationException);
    }

    [Test]
    public void Chunk_RejectsWrongAccessKind()
    {
        using var world = new World();
        Make(world, ComponentType<Position>.Id, ComponentType<Name>.Id, ComponentType<Frozen>.Id);

        foreach (var chunk in world.Query().With<Position>().Build())
        {
            Assert.Multiple(() =>
            {
                Assert.That(chunk.Has<Frozen>(), Is.True);
                Assert.That(() => chunk.GetSpan<Frozen>(), Throws.ArgumentException, "tag");
                Assert.That(() => chunk.GetSpan<Velocity>(), Throws.InvalidOperationException, "absent");
                Assert.That(() => chunk.GetManagedSpan<Name>(), Throws.Nothing);
            });
        }
    }
}
