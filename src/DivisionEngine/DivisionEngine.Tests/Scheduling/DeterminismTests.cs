using System.Numerics;
using DivisionEngine.Tests.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace DivisionEngine.Tests.Scheduling;

/// <summary>
///     The sequential oracle: the same scenario must produce the same world whether it runs on
///     the main thread alone (issue order = execution order) or on a pool with random timing.
///     This harness is reused by later milestones (Notes/Core/EntityManagementPlan.md).
/// </summary>
[TestFixture]
public sealed class DeterminismTests
{
    private const int Frames = 40;
    private const int InitialEntities = 3_000;
    private const float Dt = 1f / 60f;

    [Test]
    public void Scenario_IsDeterministic_AcrossWorkerCountsAndTiming()
    {
        var reference = RunScenario(0, false);
        var runs = new List<(int workers, bool jitter, ulong hash)>();
        foreach (var workers in new[] { 0, 1, 3, 7 })
        {
            foreach (var jitter in new[] { false, true })
            {
                runs.Add((workers, jitter, RunScenario(workers, jitter)));
                runs.Add((workers, jitter, RunScenario(workers, jitter)));
            }
        }

        foreach (var (workers, jitter, hash) in runs)
        {
            Assert.That(hash, Is.EqualTo(reference), $"workers={workers} jitter={jitter}");
        }
    }

    [Test]
    public void Scenario_ActuallyChangesTheWorld()
    {
        // Guard against a vacuous oracle: different seeds must give different hashes.
        Assert.That(RunScenario(0, false, 1), Is.Not.EqualTo(RunScenario(0, false, 2)));
    }

    [Test]
    public void BehaviourScenario_IsDeterministic_AcrossWorkerCountsAndTiming()
    {
        var reference = RunBehaviourScenario(0, false);
        foreach (var workers in new[] { 0, 1, 3, 7 })
        {
            foreach (var jitter in new[] { false, true })
            {
                Assert.That(RunBehaviourScenario(workers, jitter), Is.EqualTo(reference), $"workers={workers} jitter={jitter}");
                Assert.That(RunBehaviourScenario(workers, jitter), Is.EqualTo(reference), $"workers={workers} jitter={jitter} (repeat)");
            }
        }
    }

    [Test]
    public void BehaviourScenario_ActuallyChangesTheWorld()
    {
        Assert.That(RunBehaviourScenario(0, false, 1), Is.Not.EqualTo(RunBehaviourScenario(0, false, 2)));
    }

    [Test]
    public void HierarchyScenario_IsDeterministic_AcrossWorkerCountsAndTiming()
    {
        var reference = RunHierarchyScenario(0, false);
        foreach (var workers in new[] { 0, 1, 3, 7 })
        {
            foreach (var jitter in new[] { false, true })
            {
                Assert.That(RunHierarchyScenario(workers, jitter), Is.EqualTo(reference), $"workers={workers} jitter={jitter}");
                Assert.That(RunHierarchyScenario(workers, jitter), Is.EqualTo(reference), $"workers={workers} jitter={jitter} (repeat)");
            }
        }
    }

    [Test]
    public void HierarchyScenario_ActuallyChangesTheWorld()
    {
        Assert.That(RunHierarchyScenario(0, false, 1), Is.Not.EqualTo(RunHierarchyScenario(0, false, 2)));
    }

    /// <summary>
    ///     Transform hierarchies through the real frame loop: a chunk-parallel job spins every local
    ///     transform, <see cref="TransformPropagationSystem" /> descends the subtrees, and the tree is
    ///     reshaped between frames. The world transforms must not depend on how the roots were split
    ///     across workers.
    /// </summary>
    private static ulong RunHierarchyScenario(int workers, bool jitter, int seed = 7)
    {
        using var engine = new Engine(NullLogger.Instance, new JobScheduler(workers));
        var world = engine.World;
        var rng = new Random(seed);

        var nodes = new List<Entity>();
        var roots = new List<Entity>();
        for (var r = 0; r < 40; r++)
        {
            var root = world.CreateTransform(LocalTransform.FromPosition(new Vector3(r, 0, 0)));
            roots.Add(root);
            nodes.Add(root);
            for (var c = 0; c < 3; c++)
            {
                var child = world.CreateTransform(LocalTransform.FromPosition(new Vector3(0, c + 1, 0)));
                world.SetParent(child, root);
                nodes.Add(child);
                for (var g = 0; g < 2; g++)
                {
                    var grandchild = world.CreateTransform(LocalTransform.FromPosition(new Vector3(0, 0, g + 1)));
                    world.SetParent(grandchild, child);
                    nodes.Add(grandchild);
                }
            }
        }

        engine.AddSystem(PhaseId.Update, new Spin(world.Query().With<LocalTransform>().Build(), jitter));

        var checksums = new List<double>();
        for (var frame = 0; frame < 20; frame++)
        {
            engine.RunFrame(Realtime.FromTicks(frame * 16, 1000));

            // Structural churn between frames, on the main thread, so the shape of the tree is fixed
            // by the seed and only the propagation of it is left to the pool.
            if (frame % 4 == 3)
            {
                var victim = nodes[rng.Next(nodes.Count)];
                if (world.IsAlive(victim))
                {
                    world.DestroyEntity(victim);
                }
            }

            if (frame % 3 == 2)
            {
                var child = nodes[rng.Next(nodes.Count)];
                var parent = roots[rng.Next(roots.Count)];
                // Only genuine roots are used as the new parent, which rules out closing a cycle.
                if (child != parent && world.IsAlive(child) && world.IsAlive(parent) && world.GetParent(parent).IsNull)
                {
                    world.SetParent(child, parent);
                }
            }

            double sum = 0;
            foreach (var chunk in world.Query().With<WorldTransform>().Build())
            {
                foreach (var transform in chunk.GetReadOnlySpan<WorldTransform>())
                {
                    var position = transform.Position;
                    sum += position.X * 0.5 + position.Y * 0.25 + position.Z;
                }
            }

            checksums.Add(sum);
        }

        return HashTransforms(world, checksums);
    }

    private sealed class Spin(EntityQuery query, bool jitter) : IJobSystem
    {
        public void Schedule(in JobSchedulingContext context)
        {
            var delta = (float)context.Time.Delta;
            context.Graph.ScheduleChunks("Spin", query, Access.Write<LocalTransform>(),
                (in JobContext _, ArchetypeChunk chunk) =>
                {
                    Jitter(jitter);
                    var step = Quaternion.CreateFromAxisAngle(Vector3.UnitY, delta);
                    foreach (ref var local in chunk.GetSpan<LocalTransform>())
                    {
                        local.Rotation = Quaternion.Normalize(local.Rotation * step);
                    }
                });
        }
    }

    /// <summary>Hashes both the world transforms and the shape of the tree that produced them.</summary>
    private static ulong HashTransforms(World world, List<double> checksums)
    {
        var rows = new List<(int index, int version, Vector3 position, int parent)>();
        foreach (var chunk in world.Query().With<WorldTransform>().Build())
        {
            var entities = chunk.Entities;
            var transforms = chunk.GetReadOnlySpan<WorldTransform>();
            for (var i = 0; i < entities.Length; i++)
            {
                var parent = world.GetParent(entities[i]);
                rows.Add((entities[i].Index, entities[i].Version, transforms[i].Position, parent.IsNull ? -1 : parent.Index));
            }
        }

        rows.Sort((a, b) => a.index.CompareTo(b.index));

        var hash = 14695981039346656037UL;
        void Mix(long value)
        {
            hash = (hash ^ (ulong)value) * 1099511628211UL;
        }

        Mix(world.EntityCount);
        foreach (var row in rows)
        {
            Mix(row.index);
            Mix(row.version);
            Mix(row.parent);
            Mix(BitConverter.SingleToInt32Bits(row.position.X));
            Mix(BitConverter.SingleToInt32Bits(row.position.Y));
            Mix(BitConverter.SingleToInt32Bits(row.position.Z));
        }

        foreach (var checksum in checksums)
        {
            Mix(BitConverter.DoubleToInt64Bits(checksum));
        }

        return hash;
    }

    private sealed class Attacker(Entity target, bool jitter) : Behaviour
    {
        protected override async BehaviourTask Run(BehaviourContext context)
        {
            while (true)
            {
                await context.Phase(PhaseId.Update);
                var self = await context.Read<Position>(context.Entity);
                Jitter(jitter);
                var damage = 1 + (int)Math.Abs(self.X) % 3;
                context.Modify<Health>(target, h => new Health { Value = h.Value - damage }, Round.Label("damage"));
            }
            // ReSharper disable once FunctionNeverReturns
        }
    }

    private sealed class Healer(Entity target, Entity shared, bool jitter) : Behaviour
    {
        protected override async BehaviourTask Run(BehaviourContext context)
        {
            while (true)
            {
                await context.Phase(PhaseId.Update);
                var damaged = await context.Read<Health>(target, Round.Label("damage"));
                Jitter(jitter);
                if (damaged.Value < 50)
                {
                    context.Modify<Health>(target, h => new Health { Value = h.Value + 7 });
                }

                var mark = await context.Write<Position>(shared);
                mark.Value.X = damaged.Value;
                mark.Value.Y = context.TurnId;
            }
            // ReSharper disable once FunctionNeverReturns
        }
    }

    private static ulong RunBehaviourScenario(int workers, bool jitter, int seed = 99)
    {
        using var scheduler = new JobScheduler(workers);
        using var world = new World();
        var graph = new JobGraph(scheduler, world);
        graph.RegisterRounds(PhaseId.Update, "damage");

        var rng = new Random(seed);
        var targets = new Entity[60];
        for (var i = 0; i < targets.Length; i++)
        {
            targets[i] = world.CreateEntity(ComponentType<Health>.Id, ComponentType<Position>.Id);
            world.SetComponent(targets[i], new Health { Value = rng.Next(40, 120) });
        }

        var shared = world.CreateEntity(ComponentType<Position>.Id);
        var actors = new List<Entity>();
        for (var i = 0; i < 150; i++)
        {
            var actor = world.CreateEntity(ComponentType<Position>.Id, ComponentType<Velocity>.Id);
            world.SetComponent(actor, new Position(rng.Next(-20, 20), 0, 0));
            world.SetComponent(actor, new Velocity(rng.NextSingle() * 4 - 2, 0, 0));
            actors.Add(actor);
            var target = targets[rng.Next(targets.Length)];
            graph.Start(actor, i % 3 == 0 ? new Healer(target, shared, jitter) : new Attacker(target, jitter));
        }

        var moving = world.Query().With<Position>().With<Velocity>().Build();
        var checksums = new List<double>();
        for (var frame = 0; frame < 25; frame++)
        {
            graph.Time = new Time(frame * Dt, Dt);
            graph.BeginPhase(PhaseId.Update);
            graph.ScheduleChunks("Integrate", moving, Access.Read<Velocity>().Write<Position>(), (in ctx, chunk) =>
            {
                Jitter(jitter);
                var positions = chunk.GetSpan<Position>();
                var velocities = chunk.GetReadOnlySpan<Velocity>();
                for (var i = 0; i < positions.Length; i++)
                {
                    positions[i].X += velocities[i].X * (float)ctx.Time.Delta;
                }
            });
            graph.ResumePhase(PhaseId.Update);
            graph.EndPhase();

            double sum = 0;
            foreach (var target in targets)
            {
                sum += world.GetComponent<Health>(target).Value;
            }

            checksums.Add(sum + world.GetComponent<Position>(shared).X * 0.25 + world.GetComponent<Position>(shared).Y);
            graph.EndFrame();
        }

        return Hash(world, checksums);
    }

    private static ulong RunScenario(int workers, bool jitter, int seed = 1234)
    {
        using var scheduler = new JobScheduler(workers);
        using var world = new World();
        var graph = new JobGraph(scheduler, world);

        var rng = new Random(seed);
        for (var i = 0; i < InitialEntities; i++)
        {
            var e = world.CreateEntity(ComponentType<Position>.Id, ComponentType<Velocity>.Id, ComponentType<Health>.Id);
            world.SetComponent(e, new Position(rng.NextSingle() * 100, rng.NextSingle() * 100, 0));
            world.SetComponent(e, new Velocity(rng.NextSingle() * 10 - 5, rng.NextSingle() * 10 - 5, 0));
            world.SetComponent(e, new Health { Value = rng.Next(1, 60) });
        }

        var moving = world.Query().With<Position>().With<Velocity>().Build();
        var mortal = world.Query().With<Health>().Build();
        var ecb = new EntityCommandBuffer();
        var ecbResource = ecb.Resource;
        var checksums = new List<double>();

        for (var frame = 0; frame < Frames; frame++)
        {
            graph.Time = new Time(frame * Dt, Dt);
            var frameIndex = frame;

            graph.ScheduleChunks("Integrate", moving, Access.Read<Velocity>().Write<Position>(), (in ctx, chunk) =>
            {
                Jitter(jitter);
                var positions = chunk.GetSpan<Position>();
                var velocities = chunk.GetReadOnlySpan<Velocity>();
                var dt = (float)ctx.Time.Delta;
                for (var i = 0; i < positions.Length; i++)
                {
                    positions[i].X += velocities[i].X * dt;
                    positions[i].Y += velocities[i].Y * dt;
                }
            });

            graph.ScheduleChunks("Damage", mortal, Access.Write<Health>(), (in _, chunk) =>
            {
                Jitter(jitter);
                foreach (ref var health in chunk.GetSpan<Health>())
                {
                    health.Value -= 1;
                }
            });

            graph.Schedule("CollectDead", Access.Read<Health>().Write(ecbResource), _ =>
            {
                Jitter(jitter);
                foreach (var chunk in mortal)
                {
                    var healths = chunk.GetReadOnlySpan<Health>();
                    var entities = chunk.Entities;
                    for (var i = 0; i < healths.Length; i++)
                    {
                        if (healths[i].Value <= 0)
                        {
                            ecb.DestroyEntity(entities[i]);
                        }
                    }
                }
            });

            graph.Schedule("Spawn", new AccessSetBuilder().Write(ecbResource), _ =>
            {
                Jitter(jitter);
                for (var k = 0; k < 25; k++)
                {
                    var e = ecb.CreateEntity();
                    ecb.AddComponent(e, new Position(frameIndex, k, 0));
                    ecb.AddComponent(e, new Velocity(1, 0, 0));
                    ecb.AddComponent(e, new Health { Value = frameIndex % 7 + 1 });
                }
            });

            graph.SchedulePlayback(ecb);

            graph.Schedule("Sum", Access.Read<Position>().Read<Health>(), _ =>
            {
                Jitter(jitter);
                double sum = 0;
                foreach (var chunk in mortal)
                {
                    foreach (var h in chunk.GetReadOnlySpan<Health>())
                    {
                        sum += h.Value;
                    }
                }

                foreach (var chunk in moving)
                {
                    foreach (var p in chunk.GetReadOnlySpan<Position>())
                    {
                        sum += p.X * 0.5 + p.Y;
                    }
                }

                checksums.Add(sum);
            });

            graph.EndFrame();
        }

        return Hash(world, checksums);
    }

    private static void Jitter(bool enabled)
    {
        if (enabled)
        {
            Thread.SpinWait(Random.Shared.Next(0, 4_000));
        }
    }

    private static ulong Hash(World world, List<double> checksums)
    {
        var rows = new List<(int index, int version, float x, float y, int health)>();
        foreach (var chunk in world.Query().Build())
        {
            var entities = chunk.Entities;
            var positions = chunk.Has<Position>() ? chunk.GetReadOnlySpan<Position>() : default;
            var healths = chunk.Has<Health>() ? chunk.GetReadOnlySpan<Health>() : default;
            for (var i = 0; i < entities.Length; i++)
            {
                rows.Add((entities[i].Index, entities[i].Version,
                    positions.IsEmpty ? 0 : positions[i].X, positions.IsEmpty ? 0 : positions[i].Y,
                    healths.IsEmpty ? 0 : healths[i].Value));
            }
        }

        rows.Sort((a, b) => a.index.CompareTo(b.index));

        var hash = 14695981039346656037UL;
        void Mix(long value)
        {
            hash = (hash ^ (ulong)value) * 1099511628211UL;
        }

        Mix(world.EntityCount);
        foreach (var row in rows)
        {
            Mix(row.index);
            Mix(row.version);
            Mix(BitConverter.SingleToInt32Bits(row.x));
            Mix(BitConverter.SingleToInt32Bits(row.y));
            Mix(row.health);
        }

        foreach (var checksum in checksums)
        {
            Mix(BitConverter.DoubleToInt64Bits(checksum));
        }

        return hash;
    }
}
