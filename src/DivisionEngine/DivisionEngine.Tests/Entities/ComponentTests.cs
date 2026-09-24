namespace DivisionEngine.Tests.Entities;

[TestFixture]
public sealed class ComponentTests
{
    [Test]
    public void Registry_ClassifiesTypes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ComponentType<Position>.Info.Size, Is.EqualTo(12));
            Assert.That(ComponentType<Position>.Info.Alignment, Is.EqualTo(16));
            Assert.That(ComponentType<Position>.Info.HasChunkData, Is.True);
            Assert.That(ComponentType<Frozen>.Info.IsTag, Is.True);
            Assert.That(ComponentType<Frozen>.Info.Size, Is.Zero);
            Assert.That(ComponentType<Name>.Info.IsManaged, Is.True);
            Assert.That(ComponentType<Name>.Info.Size, Is.Zero);
            Assert.That(ComponentType<Position>.Id, Is.Not.EqualTo(ComponentType<Velocity>.Id));
            Assert.That(ComponentType<Position>.Id, Is.EqualTo(ComponentType<Position>.Id), "stable per type");
        });
    }

    [Test]
    public void AddGetSetRemove_Unmanaged()
    {
        using var world = new World();
        var e = world.CreateEntity();

        world.AddComponent(e, new Position(1, 2, 3));
        Assert.That(world.HasComponent<Position>(e), Is.True);
        Assert.That(world.GetComponent<Position>(e).Y, Is.EqualTo(2));

        world.GetComponent<Position>(e).Y = 20;
        Assert.That(world.GetComponent<Position>(e).Y, Is.EqualTo(20), "GetComponent returns a live reference");

        world.SetComponent(e, new Position(7, 8, 9));
        Assert.That(world.GetComponent<Position>(e).Z, Is.EqualTo(9));

        world.RemoveComponent<Position>(e);
        Assert.That(world.HasComponent<Position>(e), Is.False);
        Assert.That(world.GetArchetype(e).Types, Is.Empty);
    }

    [Test]
    public void Misuse_Throws()
    {
        using var world = new World();
        var e = world.CreateEntity();
        world.AddComponent<Position>(e);

        Assert.Multiple(() =>
        {
            Assert.That(() => world.AddComponent<Position>(e), Throws.InvalidOperationException, "duplicate add");
            Assert.That(() => world.GetComponent<Velocity>(e), Throws.InvalidOperationException, "missing get");
            Assert.That(() => world.RemoveComponent<Velocity>(e), Throws.InvalidOperationException, "missing remove");
            Assert.That(() => world.SetComponent(e, new Velocity()), Throws.InvalidOperationException, "missing set");
            Assert.That(() => world.AddManagedComponent(e, new Name()), Throws.Nothing);
            Assert.That(() => world.GetComponent<Frozen>(e), Throws.InvalidOperationException, "tag has no data");
            Assert.That(() => world.AddComponent(e, ComponentType<Position>.Id, new byte[3]), Throws.ArgumentException,
                "wrong size");
        });
    }

    [Test]
    public void RemovingOneComponent_PreservesOthers()
    {
        using var world = new World();
        var e = world.CreateEntity();
        world.AddComponent(e, new Position(1, 2, 3));
        world.AddComponent(e, new Velocity(4, 5, 6));
        world.AddManagedComponent(e, new Name { Value = "n" });

        world.RemoveComponent<Position>(e);

        Assert.Multiple(() =>
        {
            Assert.That(world.HasComponent<Position>(e), Is.False);
            Assert.That(world.GetComponent<Velocity>(e).X, Is.EqualTo(4));
            Assert.That(world.GetManagedComponent<Name>(e).Value, Is.EqualTo("n"));
        });
    }

    [Test]
    public void Managed_AddGetSetRemove()
    {
        using var world = new World();
        var e = world.CreateEntity();
        var name = new Name { Value = "a" };

        world.AddManagedComponent(e, name);
        Assert.That(world.GetManagedComponent<Name>(e), Is.SameAs(name), "stored by reference");

        var other = new Name { Value = "b" };
        world.SetManagedComponent(e, other);
        Assert.That(world.GetManagedComponent<Name>(e), Is.SameAs(other));

        world.RemoveComponent<Name>(e);
        Assert.That(world.HasComponent<Name>(e), Is.False);

        Assert.Multiple(() =>
        {
            Assert.That(() => world.AddManagedComponent(e, (Name)null!), Throws.ArgumentNullException);
            Assert.That(() => world.AddManagedComponent(e, ComponentType<Name>.Id, "not a Name"),
                Throws.ArgumentException);
            Assert.That(() => world.AddManagedComponent(e, ComponentType<Position>.Id, new Name()),
                Throws.ArgumentException);
            Assert.That(() => world.SetManagedComponent(e, new Name()), Throws.InvalidOperationException, "missing");
        });
    }

    [Test]
    public void Tag_AddHasRemove()
    {
        using var world = new World();
        var e = world.CreateEntity();
        world.AddComponent<Position>(e);
        world.AddComponent<Frozen>(e);

        Assert.That(world.HasComponent<Frozen>(e), Is.True);
        Assert.That(world.GetArchetype(e).Types, Has.Length.EqualTo(2));

        world.RemoveComponent<Frozen>(e);
        Assert.That(world.HasComponent<Frozen>(e), Is.False);
    }

    [Test]
    public void Values_SurviveArchetypeMoves_AcrossManyEntities()
    {
        using var world = new World();
        const int count = 5_000;
        var entities = new Entity[count];
        for (var i = 0; i < count; i++)
        {
            entities[i] = world.CreateEntity();
            world.AddComponent(entities[i], new Position(i, 0, 0));
        }

        for (var i = 0; i < count; i += 2)
        {
            world.AddComponent(entities[i], new Velocity(0, i, 0));
        }

        for (var i = 0; i < count; i += 3)
        {
            world.AddManagedComponent(entities[i], new Name { Value = i.ToString() });
        }

        for (var i = 0; i < count; i += 5)
        {
            world.DestroyEntity(entities[i]);
        }

        for (var i = 0; i < count; i++)
        {
            if (i % 5 == 0)
            {
                Assert.That(world.IsAlive(entities[i]), Is.False);
                continue;
            }

            Assert.That(world.GetComponent<Position>(entities[i]).X, Is.EqualTo(i));
            Assert.That(world.HasComponent<Velocity>(entities[i]), Is.EqualTo(i % 2 == 0));
            if (i % 2 == 0)
            {
                Assert.That(world.GetComponent<Velocity>(entities[i]).Y, Is.EqualTo(i));
            }

            if (i % 3 == 0)
            {
                Assert.That(world.GetManagedComponent<Name>(entities[i]).Value, Is.EqualTo(i.ToString()));
            }
        }
    }

    [Test]
    public void NewEntity_DoesNotSeeStaleDataFromPreviousOccupant()
    {
        using var world = new World();
        var a = world.CreateEntity(ComponentType<Position>.Id);
        world.SetComponent(a, new Position(9, 9, 9));
        world.DestroyEntity(a);

        var b = world.CreateEntity(ComponentType<Position>.Id);
        Assert.That(world.GetComponent<Position>(b).X, Is.Zero);
    }

    [Test]
    public void ComponentSetLargerThanChunk_Throws()
    {
        using var world = new World();
        var e = world.CreateEntity();
        Assert.That(() => world.AddComponent<Huge>(e), Throws.InvalidOperationException);
    }
}