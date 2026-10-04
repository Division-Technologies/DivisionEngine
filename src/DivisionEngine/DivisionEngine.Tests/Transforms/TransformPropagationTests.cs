using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;

namespace DivisionEngine.Tests.Transforms;

[TestFixture]
public sealed class TransformPropagationTests
{
    private const float Tolerance = 1e-4f;

    private static Realtime At(double seconds)
    {
        return Realtime.FromTicks((long)Math.Round(seconds * 1000), 1000);
    }

    private static Vector3 WorldPosition(World world, Entity entity)
    {
        return world.GetComponentReadOnly<WorldTransform>(entity).Position;
    }

    private static void AssertPosition(Vector3 actual, Vector3 expected, string message)
    {
        Assert.Multiple(() =>
        {
            Assert.That(actual.X, Is.EqualTo(expected.X).Within(Tolerance), $"{message} (x)");
            Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(Tolerance), $"{message} (y)");
            Assert.That(actual.Z, Is.EqualTo(expected.Z).Within(Tolerance), $"{message} (z)");
        });
    }

    /// <summary>Runs one frame of the standard loop, which includes the transform propagation phase.</summary>
    private static void Propagate(Engine engine, double seconds = 0)
    {
        engine.RunFrame(At(seconds));
    }

    [Test]
    public void Roots_TakeTheirLocalTransformAsTheirWorldTransform()
    {
        using var engine = new Engine(NullLogger.Instance, new JobScheduler(2));
        var root = engine.World.CreateTransform(LocalTransform.FromPosition(new Vector3(1, 2, 3)));

        Propagate(engine);

        AssertPosition(WorldPosition(engine.World, root), new Vector3(1, 2, 3), "root");
    }

    [Test]
    public void Children_AreComposedWithTheirParent()
    {
        using var engine = new Engine(NullLogger.Instance, new JobScheduler(2));
        var world = engine.World;
        var root = world.CreateTransform(LocalTransform.FromPosition(new Vector3(10, 0, 0)));
        var child = world.CreateTransform(LocalTransform.FromPosition(new Vector3(0, 5, 0)));
        var grandchild = world.CreateTransform(LocalTransform.FromPosition(new Vector3(0, 0, 2)));
        world.SetParent(child, root);
        world.SetParent(grandchild, child);

        Propagate(engine);

        Assert.Multiple(() =>
        {
            AssertPosition(WorldPosition(world, root), new Vector3(10, 0, 0), "root");
            AssertPosition(WorldPosition(world, child), new Vector3(10, 5, 0), "child");
            AssertPosition(WorldPosition(world, grandchild), new Vector3(10, 5, 2), "grandchild");
        });
    }

    [Test]
    public void ParentRotation_AppliesToChildOffsets()
    {
        using var engine = new Engine(NullLogger.Instance, new JobScheduler(2));
        var world = engine.World;

        // A quarter turn about Y sends the child's local +X offset to -Z.
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var root = world.CreateTransform(LocalTransform.FromRotation(rotation));
        var child = world.CreateTransform(LocalTransform.FromPosition(new Vector3(1, 0, 0)));
        world.SetParent(child, root);

        Propagate(engine);

        AssertPosition(WorldPosition(world, child), new Vector3(0, 0, -1), "rotated child");
    }

    [Test]
    public void ParentScale_ScalesChildOffsets()
    {
        using var engine = new Engine(NullLogger.Instance, new JobScheduler(2));
        var world = engine.World;
        var root = world.CreateTransform(LocalTransform.Identity with { Scale = new Vector3(2, 2, 2) });
        var child = world.CreateTransform(LocalTransform.FromPosition(new Vector3(3, 0, 0)));
        world.SetParent(child, root);

        Propagate(engine);

        AssertPosition(WorldPosition(world, child), new Vector3(6, 0, 0), "scaled child");
    }

    [Test]
    public void Reparenting_TakesEffectOnTheNextFrame()
    {
        using var engine = new Engine(NullLogger.Instance, new JobScheduler(2));
        var world = engine.World;
        var first = world.CreateTransform(LocalTransform.FromPosition(new Vector3(10, 0, 0)));
        var second = world.CreateTransform(LocalTransform.FromPosition(new Vector3(-10, 0, 0)));
        var child = world.CreateTransform(LocalTransform.FromPosition(new Vector3(1, 0, 0)));
        world.SetParent(child, first);
        Propagate(engine);
        AssertPosition(WorldPosition(world, child), new Vector3(11, 0, 0), "under the first parent");

        world.SetParent(child, second);
        Propagate(engine, 0.01);

        AssertPosition(WorldPosition(world, child), new Vector3(-9, 0, 0), "under the second parent");
    }

    [Test]
    public void DetachedChild_BecomesARootAndKeepsItsLocalTransform()
    {
        using var engine = new Engine(NullLogger.Instance, new JobScheduler(2));
        var world = engine.World;
        var root = world.CreateTransform(LocalTransform.FromPosition(new Vector3(10, 0, 0)));
        var child = world.CreateTransform(LocalTransform.FromPosition(new Vector3(1, 0, 0)));
        world.SetParent(child, root);
        Propagate(engine);

        world.ClearParent(child);
        Propagate(engine, 0.01);

        AssertPosition(WorldPosition(world, child), new Vector3(1, 0, 0),
            "the local transform is preserved, not the world one");
    }

    [Test]
    public void EntitiesWithoutALocalTransform_PassTheirParentsTransformThrough()
    {
        using var engine = new Engine(NullLogger.Instance, new JobScheduler(2));
        var world = engine.World;
        var root = world.CreateTransform(LocalTransform.FromPosition(new Vector3(10, 0, 0)));
        var group = world.CreateEntity(); // a grouping node that is not part of the transform hierarchy
        var child = world.CreateTransform(LocalTransform.FromPosition(new Vector3(1, 0, 0)));
        world.SetParent(group, root);
        world.SetParent(child, group);

        Propagate(engine);

        AssertPosition(WorldPosition(world, child), new Vector3(11, 0, 0),
            "placed relative to the nearest transform ancestor");
    }

    [Test]
    public void ARootWithoutTransformComponents_PlacesItsChildrenRelativeToTheWorld()
    {
        using var engine = new Engine(NullLogger.Instance, new JobScheduler(2));
        var world = engine.World;
        var group = world.CreateEntity();
        var child = world.CreateTransform(LocalTransform.FromPosition(new Vector3(1, 0, 0)));
        world.SetParent(child, group);
        Propagate(engine);

        world.GetComponent<LocalTransform>(child).Position = new Vector3(4, 0, 0);
        Propagate(engine, 0.01);

        AssertPosition(WorldPosition(world, child), new Vector3(4, 0, 0), "follows its local transform");
    }

    [Test]
    public void ARootWithOnlyALocalTransform_StillPropagatesToItsChildren()
    {
        using var engine = new Engine(NullLogger.Instance, new JobScheduler(2));
        var world = engine.World;
        var root = world.CreateEntity(ComponentType<LocalTransform>.Id);
        world.GetComponent<LocalTransform>(root) = LocalTransform.FromPosition(new Vector3(10, 0, 0));
        var child = world.CreateTransform(LocalTransform.FromPosition(new Vector3(1, 0, 0)));
        world.SetParent(child, root);

        Propagate(engine);

        AssertPosition(WorldPosition(world, child), new Vector3(11, 0, 0), "composed with the root's local");
    }

    [Test]
    public void DeepAndWideHierarchies_PropagateAcrossWorkers()
    {
        using var engine = new Engine(NullLogger.Instance, new JobScheduler(4));
        var world = engine.World;

        // 200 independent chains of 20, each step offsetting by one on X, spread over many chunks.
        const int chains = 200;
        const int depth = 20;
        var leaves = new Entity[chains];
        for (var chain = 0; chain < chains; chain++)
        {
            var current = world.CreateTransform(LocalTransform.FromPosition(new Vector3(1, 0, 0)));
            for (var step = 1; step < depth; step++)
            {
                var next = world.CreateTransform(LocalTransform.FromPosition(new Vector3(1, 0, 0)));
                world.SetParent(next, current);
                current = next;
            }

            leaves[chain] = current;
        }

        Propagate(engine);

        Assert.Multiple(() =>
        {
            foreach (var leaf in leaves)
            {
                AssertPosition(WorldPosition(world, leaf), new Vector3(depth, 0, 0), "leaf of a chain");
            }
        });
    }

    [Test]
    public void LocalTransform_IsFrozenFromTransformPropagationOnwards()
    {
        using var engine = new Engine(NullLogger.Instance, new JobScheduler(2));
        var query = engine.World.Query().With<LocalTransform>().Build();
        engine.AddSystem(PhaseId.LateUpdate, new LocalTransformWriter(query));

        Assert.That(() => Propagate(engine), Throws.InvalidOperationException);
    }

    [Test]
    public void WorldTransform_IsFrozenAfterPropagation_ButWritableDuringIt()
    {
        using var engine = new Engine(NullLogger.Instance, new JobScheduler(2));

        Assert.Multiple(() =>
        {
            Assert.That(engine.Loop.TransformPropagation.FrozenTypes,
                Does.Not.Contain(ComponentType<WorldTransform>.Id));
            Assert.That(engine.Loop.LateUpdate.FrozenTypes, Contains.Item(ComponentType<WorldTransform>.Id));
            Assert.That(engine.Loop.Extract.FrozenTypes, Contains.Item(ComponentType<WorldTransform>.Id));
            Assert.That(engine.Loop.Render.FrozenTypes, Contains.Item(ComponentType<WorldTransform>.Id));
        });
    }

    private sealed class LocalTransformWriter(EntityQuery query) : IJobSystem
    {
        public void Schedule(in JobSchedulingContext context)
        {
            context.Graph.ScheduleChunks("write LocalTransform", query, Access.Write<LocalTransform>(),
                static (in _, chunk) =>
                {
                    foreach (ref var local in chunk.GetSpan<LocalTransform>())
                    {
                        local.Position.X += 1;
                    }
                });
        }
    }
}