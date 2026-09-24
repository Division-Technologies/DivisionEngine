namespace DivisionEngine.Tests.Entities;

[TestFixture]
public sealed class EntityCommandBufferTests
{
    [Test]
    public void DeferredEntity_IsResolvedAtPlayback()
    {
        using var world = new World();
        var ecb = new EntityCommandBuffer();

        var deferred = ecb.CreateEntity();
        ecb.AddComponent(deferred, new Position(1, 2, 3));
        ecb.AddComponent<Frozen>(deferred);
        ecb.AddManagedComponent(deferred, new Name { Value = "x" });

        Assert.Multiple(() =>
        {
            Assert.That(deferred.IsDeferred, Is.True);
            Assert.That(world.IsAlive(deferred), Is.False);
            Assert.That(ecb.Count, Is.EqualTo(4));
        });

        ecb.Playback(world);

        Assert.That(world.EntityCount, Is.EqualTo(1));
        foreach (var chunk in world.Query().With<Position>().Build())
        {
            var e = chunk.Entities[0];
            Assert.Multiple(() =>
            {
                Assert.That(chunk.GetSpan<Position>()[0].Z, Is.EqualTo(3));
                Assert.That(world.HasComponent<Frozen>(e), Is.True);
                Assert.That(world.GetManagedComponent<Name>(e).Value, Is.EqualTo("x"));
            });
        }

        Assert.That(ecb.IsEmpty, Is.True, "playback clears the buffer");
    }

    [Test]
    public void CommandsApply_InRecordOrder()
    {
        using var world = new World();
        var e = world.CreateEntity();
        var ecb = new EntityCommandBuffer();

        ecb.AddComponent(e, new Health { Value = 1 });
        ecb.SetComponent(e, new Health { Value = 2 });
        ecb.SetComponent(e, new Health { Value = 3 });
        ecb.AddComponent<Velocity>(e);
        ecb.RemoveComponent<Velocity>(e);
        ecb.Playback(world);

        Assert.Multiple(() =>
        {
            Assert.That(world.GetComponent<Health>(e).Value, Is.EqualTo(3), "last write wins");
            Assert.That(world.HasComponent<Velocity>(e), Is.False, "add then remove");
        });
    }

    [Test]
    public void Destroy_AndManagedSet()
    {
        using var world = new World();
        var a = world.CreateEntity();
        var b = world.CreateEntity();
        world.AddManagedComponent(b, new Name { Value = "old" });

        var ecb = new EntityCommandBuffer();
        ecb.DestroyEntity(a);
        ecb.SetManagedComponent(b, new Name { Value = "new" });
        ecb.Playback(world);

        Assert.Multiple(() =>
        {
            Assert.That(world.IsAlive(a), Is.False);
            Assert.That(world.GetManagedComponent<Name>(b).Value, Is.EqualTo("new"));
        });
    }

    [Test]
    public void ManyDeferredEntities_WithPayloadGrowth()
    {
        using var world = new World();
        var ecb = new EntityCommandBuffer();
        for (var i = 0; i < 2_000; i++)
        {
            var e = ecb.CreateEntity();
            ecb.AddComponent(e, new Position(i, i, i));
        }

        ecb.Playback(world);

        var sum = 0f;
        foreach (var chunk in world.Query().With<Position>().Build())
        {
            foreach (var p in chunk.GetReadOnlySpan<Position>())
            {
                sum += p.X;
            }
        }

        Assert.That(world.EntityCount, Is.EqualTo(2_000));
        Assert.That(sum, Is.EqualTo(2_000 * 1_999 / 2f));
    }

    [Test]
    public void Clear_DiscardsCommands()
    {
        using var world = new World();
        var ecb = new EntityCommandBuffer();
        ecb.CreateEntity();
        ecb.Clear();
        ecb.Playback(world);
        Assert.That(world.EntityCount, Is.Zero);
    }
}