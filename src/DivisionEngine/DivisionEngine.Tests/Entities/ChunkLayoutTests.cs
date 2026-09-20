namespace DivisionEngine.Tests.Entities;

[TestFixture]
public sealed class ChunkLayoutTests
{
    private const int ChunkSize = 16 * 1024;

    [Test]
    public void Capacity_FillsTheChunk()
    {
        using var world = new World();
        var e = world.CreateEntity(ComponentType<Position>.Id, ComponentType<Velocity>.Id);
        var archetype = world.GetArchetype(e);

        // 8 bytes entity + 12 + 12 per entity; alignment padding is at most 2 * 16 bytes.
        const int perEntity = 8 + 12 + 12;
        Assert.Multiple(() =>
        {
            Assert.That(archetype.Capacity * perEntity, Is.LessThanOrEqualTo(ChunkSize));
            Assert.That((archetype.Capacity + 1) * perEntity + 32, Is.GreaterThan(ChunkSize));
        });
    }

    [Test]
    public void FillingBeyondCapacity_CreatesChunks_AndOnlyTheLastIsPartial()
    {
        using var world = new World();
        var first = world.CreateEntity(ComponentType<Position>.Id);
        var archetype = world.GetArchetype(first);
        var capacity = archetype.Capacity;

        var entities = new List<Entity> { first };
        for (var i = 1; i < capacity * 2 + 5; i++)
        {
            var e = world.CreateEntity(ComponentType<Position>.Id);
            world.SetComponent(e, new Position(i, 0, 0));
            entities.Add(e);
        }

        Assert.That(archetype.ChunkCount, Is.EqualTo(3));

        // Punch holes in every chunk.
        for (var i = 0; i < entities.Count; i += 3)
        {
            world.DestroyEntity(entities[i]);
        }

        AssertDense(world, archetype);

        // Survivors keep their values.
        for (var i = 0; i < entities.Count; i++)
        {
            if (i % 3 != 0)
            {
                Assert.That(world.GetComponent<Position>(entities[i]).X, Is.EqualTo(i));
            }
        }

        // Destroy everything: chunks are released.
        foreach (var e in entities)
        {
            if (world.IsAlive(e))
            {
                world.DestroyEntity(e);
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(archetype.ChunkCount, Is.Zero);
            Assert.That(archetype.EntityCount, Is.Zero);
        });
    }

    [Test]
    public void Spans_CoverExactlyTheLiveEntities()
    {
        using var world = new World();
        for (var i = 0; i < 100; i++)
        {
            var e = world.CreateEntity(ComponentType<Position>.Id, ComponentType<Name>.Id);
            world.SetComponent(e, new Position(i, 0, 0));
            world.SetManagedComponent(e, new Name { Value = i.ToString() });
        }

        var query = world.Query().With<Position>().Build();
        var seen = 0;
        foreach (var chunk in query)
        {
            var positions = chunk.GetSpan<Position>();
            var names = chunk.GetManagedSpan<Name>();
            var entities = chunk.Entities;
            var (positionCount, nameCount, entityCount) = (positions.Length, names.Length, entities.Length);
            Assert.Multiple(() =>
            {
                Assert.That(positionCount, Is.EqualTo(chunk.Count));
                Assert.That(nameCount, Is.EqualTo(chunk.Count));
                Assert.That(entityCount, Is.EqualTo(chunk.Count));
            });

            for (var i = 0; i < chunk.Count; i++)
            {
                Assert.That(names[i].Value, Is.EqualTo(((int)positions[i].X).ToString()));
                Assert.That(world.GetComponent<Position>(entities[i]).X, Is.EqualTo(positions[i].X));
                seen++;
            }
        }

        Assert.That(seen, Is.EqualTo(100));
    }

    private static void AssertDense(World world, Archetype archetype)
    {
        var query = world.Query().With<Position>().Build();
        var chunkIndex = 0;
        var total = 0;
        foreach (var chunk in query)
        {
            if (chunk.Archetype != archetype)
            {
                continue;
            }

            var isLast = chunkIndex == archetype.ChunkCount - 1;
            if (!isLast)
            {
                Assert.That(chunk.Count, Is.EqualTo(archetype.Capacity), $"chunk {chunkIndex} must be full");
            }

            Assert.That(chunk.Count, Is.GreaterThan(0));
            total += chunk.Count;
            chunkIndex++;
        }

        Assert.That(total, Is.EqualTo(archetype.EntityCount));
    }
}
