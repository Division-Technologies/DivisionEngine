using DivisionEngine.Tests.Entities;

namespace DivisionEngine.Tests.Behaviors;

[TestFixture]
public sealed class BehaviorTests
{
    [SetUp]
    public void SetUp()
    {
        JobSafety.Enabled = true;
        _scheduler = new JobScheduler(4);
        _world = new World();
        _graph = new JobGraph(_scheduler, _world);
    }

    [TearDown]
    public void TearDown()
    {
        _scheduler.Dispose();
        _world.Dispose();
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private JobGraph _graph = null!;
    private JobScheduler _scheduler = null!;
    private World _world = null!;

    /// <summary>One frame as the loop would run it: intake, the Update phase, close.</summary>
    private void Frame()
    {
        _graph.AdmitExternal();
        _graph.BeginPhase(PhaseId.Update);
        _graph.ResumePhase(PhaseId.Update);
        _graph.EndPhase();
    }

    private int RunUntil(Func<bool> done, int maxFrames = 100)
    {
        for (var frame = 1; frame <= maxFrames; frame++)
        {
            Frame();
            if (done())
            {
                return frame;
            }
        }

        Assert.Fail($"not done after {maxFrames} frames");
        return -1;
    }

    private Entity Make(params ReadOnlySpan<ComponentTypeId> types)
    {
        return _world.CreateEntity(types);
    }

    // ------------------------------------------------------------------ behaviors

    private sealed class Reader(Entity target) : Behavior
    {
        public bool InSegment;
        public Position Value;

        protected override async BehaviorTask Run(BehaviorContext context)
        {
            Value = await context.Read<Position>(target);
            InSegment = BehaviorContext.Current == context && JobSafety.Current is not null;
        }
    }

    private sealed class Counter(int iterations, bool jitter) : Behavior
    {
        public int Count;

        protected override async BehaviorTask Run(BehaviorContext context)
        {
            for (var i = 0; i < iterations; i++)
            {
                await context.Read<Position>(context.Entity);
                if (jitter)
                {
                    Thread.SpinWait(Random.Shared.Next(0, 3_000));
                }

                Count++; // not atomic: segments of one behavior must never overlap
            }
        }
    }

    private sealed class Incrementer(Entity shared, int iterations) : Behavior
    {
        protected override async BehaviorTask Run(BehaviorContext context)
        {
            for (var i = 0; i < iterations; i++)
            {
                await context.Read<Health>(shared);
                Thread.SpinWait(Random.Shared.Next(0, 2_000));
                context.Modify<Health>(shared,
                    h => new Health { Value = h.Value + 1 }); // applied in key order at commit
            }
        }
    }

    private sealed class PhaseWaiter(Action<BehaviorContext> onResume, int times = 1) : Behavior
    {
        public int Resumed;

        protected override async BehaviorTask Run(BehaviorContext context)
        {
            for (var i = 0; i < times; i++)
            {
                await context.Phase(PhaseId.Update);
                Resumed++;
                onResume(context);
            }
        }
    }

    private sealed class Script(Func<BehaviorContext, BehaviorTask> body) : Behavior
    {
        protected override BehaviorTask Run(BehaviorContext context)
        {
            return body(context);
        }
    }

    // ------------------------------------------------------------------ tests

    [Test]
    public void Behavior_ReadsThroughASegment_AndCompletes()
    {
        var target = Make(ComponentType<Position>.Id);
        _world.SetComponent(target, new Position(1, 2, 3));
        var self = Make();
        var reader = new Reader(target);

        var context = _graph.Start(self, reader);
        RunUntil(() => context.Task.IsCompleted);

        Assert.Multiple(() =>
        {
            Assert.That(reader.Value.Z, Is.EqualTo(3));
            Assert.That(reader.InSegment, Is.True, "continuations run inside a segment with a job context");
            Assert.That(context.Task.IsFaulted, Is.False);
            Assert.That(reader.Context, Is.SameAs(context));
        });
    }

    [Test]
    public void SameTurn_SegmentsNeverOverlap_DifferentTurnsInterleave()
    {
        const int behaviors = 40;
        const int iterations = 30;
        var counters = new List<(BehaviorContext context, Counter counter)>();
        for (var i = 0; i < behaviors; i++)
        {
            var counter = new Counter(iterations, true);
            counters.Add((_graph.Start(Make(ComponentType<Position>.Id), counter), counter));
        }

        RunUntil(() => counters.All(c => c.context.Task.IsCompleted));

        Assert.That(counters.Select(c => c.counter.Count), Is.All.EqualTo(iterations));
    }

    [Test]
    public void DifferentBehaviors_RunConcurrently()
    {
        using var barrier = new Barrier(3);
        var passed = 0;
        for (var i = 0; i < 3; i++)
        {
            _graph.Start(Make(), new PhaseWaiter(_ =>
            {
                if (barrier.SignalAndWait(Timeout))
                {
                    Interlocked.Increment(ref passed);
                }
            }));
        }

        _graph.WaitAll(); // first segments park on Update
        Frame(); // first segments run and park on Update
        Frame(); // resumed together
        Assert.That(passed, Is.EqualTo(3), "resumed segments ran at the same time on the pool");
    }

    [Test]
    public void Modifications_Accumulate_AcrossTurns()
    {
        var shared = Make(ComponentType<Health>.Id);
        const int behaviors = 20;
        const int iterations = 25;
        var contexts = new List<BehaviorContext>();
        for (var i = 0; i < behaviors; i++)
        {
            contexts.Add(_graph.Start(Make(), new Incrementer(shared, iterations)));
        }

        RunUntil(() => contexts.All(c => c.Task.IsCompleted));
        Assert.That(_world.GetComponent<Health>(shared).Value, Is.EqualTo(behaviors * iterations));
    }

    [Test]
    public void EntityReads_AreConsistentWith_SystemWrites()
    {
        var targets = new Entity[50];
        for (var i = 0; i < targets.Length; i++)
        {
            targets[i] = Make(ComponentType<Position>.Id);
        }

        var query = _world.Query().With<Position>().Build();
        var torn = 0;
        var reads = 0;
        var contexts = new List<BehaviorContext>();
        for (var i = 0; i < 30; i++)
        {
            var target = targets[i % targets.Length];
            contexts.Add(_graph.Start(Make(), new Script(async ctx =>
            {
                for (var frame = 0; frame < 20; frame++)
                {
                    await ctx.Phase(PhaseId.Update);
                    for (var k = 0; k < 3; k++)
                    {
                        var p = await ctx.Read<Position>(target);
                        Interlocked.Increment(ref reads);
                        if (p.X != p.Y)
                        {
                            Interlocked.Increment(ref torn);
                        }
                    }
                }
            })));
        }

        _graph.WaitAll();
        var frames = 0;
        while (!contexts.All(c => c.Task.IsCompleted))
        {
            var value = ++frames;
            Assert.That(frames, Is.LessThan(500), "behaviors did not finish");
            _graph.BeginPhase(PhaseId.Update);
            _graph.ScheduleChunks("write positions", query, Access.Write<Position>(), (in _, chunk) =>
            {
                var positions = chunk.GetSpan<Position>();
                for (var i = 0; i < positions.Length; i++)
                {
                    positions[i].X = value;
                    Thread.SpinWait(200);
                    positions[i].Y = value;
                }
            });
            _graph.ResumePhase(PhaseId.Update);
            _graph.EndPhase();
        }

        Assert.Multiple(() =>
        {
            Assert.That(reads, Is.EqualTo(30 * 20 * 3));
            Assert.That(torn, Is.Zero, "entity-level reads are ordered against type-level writes");
        });
    }

    [Test]
    public void Phase_ParksUntilResumed_AndResumesInTurnOrder()
    {
        // Sequential scheduler so the observed order equals the issue order.
        _scheduler.Dispose();
        _scheduler = new JobScheduler(0);
        _graph = new JobGraph(_scheduler, _world);

        var order = new List<int>();
        var contexts = new List<BehaviorContext>();
        for (var i = 0; i < 20; i++)
        {
            contexts.Add(_graph.Start(Make(), new PhaseWaiter(ctx => order.Add(ctx.TurnId))));
        }

        Frame(); // first segments run and park on Update
        Assert.That(order, Is.Empty, "nothing resumes until the phase is resumed");
        Assert.That(_scheduler.LiveNodeCount, Is.Zero, "parked behaviors do not keep the frame alive");

        Frame();
        Assert.That(order, Is.EqualTo(contexts.Select(c => c.TurnId).OrderBy(id => id)));
        Assert.That(contexts.All(c => c.Task.IsCompleted), Is.True);
    }

    [Test]
    public void ExternalAwait_ResumesThroughTheIntake_NotOnTheCompletingThread()
    {
        var resumedInSegment = false;
        var resumed = false;
        var context = _graph.Start(Make(), new Script(async ctx =>
        {
            await Task.Delay(20);
            resumedInSegment = BehaviorContext.Current == ctx;
            resumed = true;
        }));

        Frame(); // first segment runs and starts the delay
        Assert.That(resumed, Is.False);

        var deadline = DateTime.UtcNow + Timeout;
        while (_graph.PendingExternalCount == 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(5);
        }

        Assert.Multiple(() =>
        {
            Assert.That(_graph.PendingExternalCount, Is.EqualTo(1));
            Assert.That(resumed, Is.False, "completion is held until it is admitted at a frame begin");
        });

        Frame();
        Assert.Multiple(() =>
        {
            Assert.That(resumed, Is.True);
            Assert.That(resumedInSegment, Is.True);
            Assert.That(context.Task.IsCompleted, Is.True);
        });
    }

    [Test]
    public void RunBackground_DeliversTheResult_NextFrame()
    {
        var result = 0;
        var context = _graph.Start(Make(), new Script(async ctx =>
        {
            result = await ctx.RunBackground(() =>
            {
                Thread.Sleep(10);
                return 42;
            });
        }));

        var frames = RunUntil(() => context.Task.IsCompleted);
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(42));
            Assert.That(frames, Is.LessThanOrEqualTo(3), "start phase, background job, result at the next phase");
        });
    }

    [Test]
    public void DestroyedSelf_CancelsPendingContinuations()
    {
        var self = Make();
        var waiter = new PhaseWaiter(_ => { });
        var context = _graph.Start(self, waiter);
        _graph.WaitAll();

        _world.DestroyEntity(self);
        Frame();

        Assert.Multiple(() =>
        {
            Assert.That(waiter.Resumed, Is.Zero);
            Assert.That(context.IsCancelled, Is.True);
            Assert.That(context.Task.IsCompleted, Is.False, "the async method is abandoned, not completed");
        });
    }

    [Test]
    public void DestroyedTarget_ReadThrows_TryReadReturnsNull()
    {
        var target = Make(ComponentType<Position>.Id);
        _world.DestroyEntity(target);

        var caught = false;
        Position? tried = new Position();
        var context = _graph.Start(Make(), new Script(async ctx =>
        {
            try
            {
                await ctx.Read<Position>(target);
            }
            catch (EntityNotAliveException)
            {
                caught = true;
            }

            tried = await ctx.TryRead<Position>(target);
        }));

        RunUntil(() => context.Task.IsCompleted);
        Assert.Multiple(() =>
        {
            Assert.That(caught, Is.True);
            Assert.That(tried, Is.Null);
            Assert.That(context.Task.IsFaulted, Is.False);
        });
    }

    [Test]
    public void UndeclaredAccess_InASegment_FailsTheBehavior()
    {
        var a = Make(ComponentType<Position>.Id, ComponentType<Velocity>.Id);
        var context = _graph.Start(Make(), new Script(async ctx =>
        {
            var access = await ctx.Access().Read<Position>(a);
            access.Get<Velocity>(a);
        }));

        Assert.That(() => RunUntil(() => context.Task.IsCompleted),
            Throws.TypeOf<JobFailedException>()
                .With.InnerException.TypeOf<BehaviorFailedException>()
                .With.InnerException.InnerException.TypeOf<JobAccessViolationException>());
        Assert.That(context.Task.IsFaulted, Is.True);
    }

    [Test]
    public void AccessHandle_IsInvalid_AfterItsSegmentEnds()
    {
        var a = Make(ComponentType<Position>.Id);
        var context = _graph.Start(Make(), new Script(async ctx =>
        {
            var access = await ctx.Access().Read<Position>(a);
            await ctx.Phase(PhaseId.Update);
            access.Get<Position>(a); // stale handle
        }));

        Assert.That(() => RunUntil(() => context.Task.IsCompleted),
            Throws.TypeOf<JobFailedException>().With.InnerException.InnerException.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void ChainsWithoutPhaseAwaits_AreBoundedPerPhase_AndNotLost()
    {
        _graph.MaxSegmentsPerPhase = 4;
        var counter = new Counter(10, false);
        var context = _graph.Start(Make(ComponentType<Position>.Id), counter);
        var frames = RunUntil(() => context.Task.IsCompleted, 20);
        Assert.Multiple(() =>
        {
            Assert.That(counter.Count, Is.EqualTo(10));
            Assert.That(frames, Is.InRange(2, 4),
                "4 segments per phase: the chain spans several phases but is never lost");
        });
    }

    [Test]
    public void MultiEntityAccess_ReadsAndWrites()
    {
        var source = Make(ComponentType<Position>.Id);
        var sink = Make(ComponentType<Position>.Id);
        _world.SetComponent(source, new Position(5, 6, 7));

        var context = _graph.Start(Make(), new Script(async ctx =>
        {
            var access = await ctx.Access().Read<Position>(source).Write<Position>(sink);
            access.Ref<Position>(sink) = access.Get<Position>(source);
        }));

        RunUntil(() => context.Task.IsCompleted);
        Assert.That(_world.GetComponent<Position>(sink).Y, Is.EqualTo(6));
    }

    [Test]
    public void EngineLoop_ResumesUpdatePhaseOncePerFrame()
    {
        _scheduler.Dispose();
        using var engine = new Engine(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            new JobScheduler(2));
        _scheduler = new JobScheduler(0); // placeholder so TearDown can dispose something

        var entity = engine.World.CreateEntity();
        var waiter = new PhaseWaiter(_ => { }, 100);
        engine.Graph.Start(entity, waiter);

        for (var i = 0; i < 4; i++)
        {
            engine.RunFrame(Realtime.FromTicks(i, 1000)); // 1 ms apart: no fixed step is owed
        }

        Assert.That(waiter.Resumed, Is.EqualTo(3),
            "the first frame starts the behavior; each later frame resumes it once");
    }
}