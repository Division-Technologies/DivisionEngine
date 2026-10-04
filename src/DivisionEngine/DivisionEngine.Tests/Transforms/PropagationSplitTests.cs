using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;

namespace DivisionEngine.Tests.Transforms;

/// <summary>
///     Propagation hands work it cannot finish within its budget to a later pass, so that one heavy
///     subtree cannot hold the frame. These shapes all exceed that budget, in the three ways a
///     hierarchy can be large: wide, deep, and bushy. Each is checked on one thread and on a pool,
///     because the split is exactly what differs between them.
/// </summary>
[TestFixture]
public sealed class PropagationSplitTests
{
    /// <summary>Comfortably past TransformPropagationSystem.WorkBudget, so the deferral path is taken.</summary>
    private const int Wide = 5_000;

    private static readonly int[] WorkerCounts = [0, 1, 7];

    private static Engine NewEngine(int workers)
    {
        return new Engine(NullLogger.Instance, new JobScheduler(workers));
    }

    /// <summary>One root with many leaf children: the shape that used to stay serial.</summary>
    [Test]
    public void WideSubtree_IsPropagatedWhole([ValueSource(nameof(WorkerCounts))] int workers)
    {
        using var engine = NewEngine(workers);
        var world = engine.World;

        var root = world.CreateTransform(LocalTransform.FromPosition(new Vector3(100, 0, 0)));
        var children = new Entity[Wide];
        for (var i = 0; i < Wide; i++)
        {
            children[i] = world.CreateTransform(LocalTransform.FromPosition(new Vector3(i, 0, 0)));
            world.SetParent(children[i], root);
        }

        engine.RunFrame(Realtime.FromSeconds(0));

        for (var i = 0; i < Wide; i++)
        {
            Assert.That(world.GetComponentReadOnly<WorldTransform>(children[i]).Position.X,
                Is.EqualTo(100f + i).Within(1e-3f), $"child {i}");
        }
    }

    /// <summary>A chain longer than the budget: the deferral has to keep its place mid-descent.</summary>
    [Test]
    public void DeepChain_IsPropagatedWhole([ValueSource(nameof(WorkerCounts))] int workers)
    {
        const int depth = 3_000;
        using var engine = NewEngine(workers);
        var world = engine.World;

        var chain = new Entity[depth];
        chain[0] = world.CreateTransform(LocalTransform.FromPosition(new Vector3(1, 0, 0)));
        for (var i = 1; i < depth; i++)
        {
            chain[i] = world.CreateTransform(LocalTransform.FromPosition(new Vector3(1, 0, 0)));
            world.SetParent(chain[i], chain[i - 1]);
        }

        engine.RunFrame(Realtime.FromSeconds(0));

        Assert.Multiple(() =>
        {
            Assert.That(world.GetComponentReadOnly<WorldTransform>(chain[0]).Position.X, Is.EqualTo(1f).Within(1e-2f));
            Assert.That(world.GetComponentReadOnly<WorldTransform>(chain[depth - 1]).Position.X,
                Is.EqualTo((float)depth).Within(1e-2f));
        });
    }

    /// <summary>
    ///     The measured bad case: several roots where one holds nearly all of the entities. The
    ///     result must not depend on which pass a given subtree ended up in.
    /// </summary>
    [Test]
    public void OneDominantRoot_MatchesTheSerialResult()
    {
        var expected = Run(0);
        var parallel = Run(7);

        Assert.That(parallel, Is.EqualTo(expected).Within(1e-3f).AsCollection);

        static float[] Run(int workers)
        {
            using var engine = NewEngine(workers);
            var world = engine.World;
            var leaves = new List<Entity>();

            // A heavy root, then a tail of light ones.
            var heavy = world.CreateTransform(LocalTransform.FromPosition(new Vector3(1, 0, 0)));
            var branch = heavy;
            for (var i = 0; i < 4_000; i++)
            {
                var leaf = world.CreateTransform(LocalTransform.FromPosition(new Vector3(1, 0, 0)));
                world.SetParent(leaf, branch);
                leaves.Add(leaf);

                // Every so often, descend instead of widening, so the shape is not a flat fan.
                if (i % 500 == 499)
                {
                    branch = leaf;
                }
            }

            for (var i = 0; i < 50; i++)
            {
                var light = world.CreateTransform(LocalTransform.FromPosition(new Vector3(i, 0, 0)));
                var child = world.CreateTransform(LocalTransform.FromPosition(new Vector3(1, 0, 0)));
                world.SetParent(child, light);
                leaves.Add(child);
            }

            engine.RunFrame(Realtime.FromSeconds(0));
            return leaves.Select(e => world.GetComponentReadOnly<WorldTransform>(e).Position.X).ToArray();
        }
    }

    /// <summary>A static subtree stays skipped even when the work around it has to be split.</summary>
    [Test]
    public void DeferredWork_StillRespectsStatic()
    {
        using var engine = NewEngine(7);
        var world = engine.World;

        var root = world.CreateTransform(LocalTransform.FromPosition(new Vector3(1, 0, 0)));
        var children = new Entity[Wide];
        for (var i = 0; i < Wide; i++)
        {
            children[i] = world.CreateTransform(LocalTransform.FromPosition(new Vector3(i, 0, 0)));
            world.SetParent(children[i], root);
        }

        engine.RunFrame(Realtime.FromSeconds(0));

        // Freeze the whole tree, move it, and confirm nothing was recomputed.
        world.MakeStatic(root);
        world.GetComponent<LocalTransform>(root).Position = new Vector3(-1000, 0, 0);
        engine.RunFrame(Realtime.FromSeconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(world.GetComponentReadOnly<WorldTransform>(children[0]).Position.X,
                Is.EqualTo(1f).Within(1e-3f));
            Assert.That(world.GetComponentReadOnly<WorldTransform>(children[Wide - 1]).Position.X,
                Is.EqualTo(1f + Wide - 1).Within(1e-3f));
        });
    }
}