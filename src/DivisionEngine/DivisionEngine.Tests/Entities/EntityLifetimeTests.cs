namespace DivisionEngine.Tests.Entities;

[TestFixture]
public sealed class EntityLifetimeTests
{
    [Test]
    public void CreateEntity_IsAlive_AndCounted()
    {
        using var world = new World();
        var a = world.CreateEntity();
        var b = world.CreateEntity();

        Assert.Multiple(() =>
        {
            Assert.That(world.IsAlive(a), Is.True);
            Assert.That(world.IsAlive(b), Is.True);
            Assert.That(a, Is.Not.EqualTo(b));
            Assert.That(world.EntityCount, Is.EqualTo(2));
            Assert.That(world.GetArchetype(a).Types, Is.Empty);
        });
    }

    [Test]
    public void DestroyEntity_InvalidatesHandle_AndReusesIndexWithNewVersion()
    {
        using var world = new World();
        var a = world.CreateEntity();
        world.DestroyEntity(a);

        Assert.That(world.IsAlive(a), Is.False);
        Assert.That(world.EntityCount, Is.EqualTo(0));

        var b = world.CreateEntity();
        Assert.Multiple(() =>
        {
            Assert.That(b.Index, Is.EqualTo(a.Index), "slot is reused");
            Assert.That(b.Version, Is.Not.EqualTo(a.Version), "version distinguishes the new occupant");
            Assert.That(world.IsAlive(a), Is.False, "stale handle stays dead");
            Assert.That(world.IsAlive(b), Is.True);
        });
    }

    [Test]
    public void NullAndStaleHandles_AreRejected()
    {
        using var world = new World();
        var a = world.CreateEntity();
        world.DestroyEntity(a);

        Assert.Multiple(() =>
        {
            Assert.That(world.IsAlive(Entity.Null), Is.False);
            Assert.That(() => world.DestroyEntity(a), Throws.TypeOf<EntityNotAliveException>());
            Assert.That(() => world.DestroyEntity(Entity.Null), Throws.TypeOf<EntityNotAliveException>());
            Assert.That(() => world.AddComponent<Position>(a), Throws.TypeOf<EntityNotAliveException>());
        });
    }

    [Test]
    public void CreateEntity_WithTypes_UsesThatArchetype_RegardlessOfOrder()
    {
        using var world = new World();
        var a = world.CreateEntity(ComponentType<Velocity>.Id, ComponentType<Position>.Id);
        var b = world.CreateEntity(ComponentType<Position>.Id, ComponentType<Velocity>.Id, ComponentType<Position>.Id);

        Assert.Multiple(() =>
        {
            Assert.That(world.HasComponent<Position>(a), Is.True);
            Assert.That(world.HasComponent<Velocity>(a), Is.True);
            Assert.That(world.GetArchetype(a), Is.SameAs(world.GetArchetype(b)), "sorted and deduplicated");
            Assert.That(world.GetComponent<Position>(a).X, Is.Zero, "zero-initialized");
        });
    }

    [Test]
    public void ManyEntities_GrowStorage()
    {
        using var world = new World();
        var entities = new Entity[10_000];
        for (var i = 0; i < entities.Length; i++)
        {
            entities[i] = world.CreateEntity();
        }

        for (var i = 0; i < entities.Length; i += 2)
        {
            world.DestroyEntity(entities[i]);
        }

        Assert.That(world.EntityCount, Is.EqualTo(5_000));
        for (var i = 0; i < entities.Length; i++)
        {
            Assert.That(world.IsAlive(entities[i]), Is.EqualTo(i % 2 == 1));
        }
    }

    [Test]
    public void DisposedWorld_RejectsUse()
    {
        var world = new World();
        var a = world.CreateEntity();
        world.Dispose();
        Assert.That(() => world.CreateEntity(), Throws.TypeOf<ObjectDisposedException>());
        Assert.That(() => world.IsAlive(a), Throws.Nothing);
    }
}