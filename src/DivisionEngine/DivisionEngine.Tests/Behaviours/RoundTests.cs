using DivisionEngine.Tests.Entities;

namespace DivisionEngine.Tests.Behaviours;

[TestFixture]
public sealed class RoundTests
{
    private JobGraph _graph = null!;
    private JobScheduler _scheduler = null!;
    private World _world = null!;

    [SetUp]
    public void SetUp()
    {
        JobSafety.Enabled = true;
        _scheduler = new JobScheduler(4);
        _world = new World();
        _graph = new JobGraph(_scheduler, _world);
        _graph.RegisterRounds(PhaseId.Update, "damage");
    }

    [TearDown]
    public void TearDown()
    {
        _scheduler.Dispose();
        _world.Dispose();
    }

    private void Frame()
    {
        _graph.AdmitExternal();
        _graph.BeginPhase(PhaseId.Update);
        _graph.ResumePhase(PhaseId.Update);
        _graph.EndPhase();
    }

    private void RunUntil(Func<bool> done, int maxFrames = 50)
    {
        for (var frame = 1; frame <= maxFrames; frame++)
        {
            Frame();
            if (done())
            {
                return;
            }
        }

        Assert.Fail($"not done after {maxFrames} frames");
    }

    private sealed class Script(Func<BehaviourContext, BehaviourTask> body) : Behaviour
    {
        protected override BehaviourTask Run(BehaviourContext context)
        {
            return body(context);
        }
    }

    private BehaviourContext Start(Func<BehaviourContext, BehaviourTask> body)
    {
        return _graph.Start(_world.CreateEntity(), new Script(body));
    }

    [Test]
    public void DefaultWrite_IsBuffered_UntilThePhaseEnds()
    {
        var target = _world.CreateEntity(ComponentType<Position>.Id);
        var context = Start(async ctx =>
        {
            await ctx.Phase(PhaseId.Update);
            var p = await ctx.Write<Position>(target);
            p.Value.X = 5;
        });
        Frame(); // start: the first segment runs and parks on Update

        _graph.BeginPhase(PhaseId.Update);
        _graph.ResumePhase(PhaseId.Update);
        _graph.WaitAll();
        Assert.That(_world.GetComponent<Position>(target).X, Is.Zero, "not visible before the commit");

        _graph.EndPhase();
        Assert.Multiple(() =>
        {
            Assert.That(_world.GetComponent<Position>(target).X, Is.EqualTo(5));
            Assert.That(context.Task.IsCompleted, Is.True);
        });
    }

    [Test]
    public void SameRoundWrites_LastWriterByTurnOrderWins_RegardlessOfTiming()
    {
        var target = _world.CreateEntity(ComponentType<Position>.Id);
        var contexts = new List<BehaviourContext>();
        for (var i = 0; i < 10; i++)
        {
            contexts.Add(Start(async ctx =>
            {
                await ctx.Phase(PhaseId.Update);
                Thread.SpinWait(Random.Shared.Next(0, 20_000));
                var p = await ctx.Write<Position>(target);
                p.Value.X = ctx.TurnId;
            }));
        }

        RunUntil(() => contexts.All(c => c.Task.IsCompleted));
        Assert.That(_world.GetComponent<Position>(target).X, Is.EqualTo(contexts.Max(c => c.TurnId)));
    }

    [Test]
    public void Modify_IsAppliedInTurnOrder_SoNonCommutativeUpdatesAreDeterministic()
    {
        var target = _world.CreateEntity(ComponentType<Health>.Id);
        _world.SetComponent(target, new Health { Value = 1 });
        var contexts = new List<BehaviourContext>();
        for (var i = 0; i < 8; i++)
        {
            contexts.Add(Start(async ctx =>
            {
                await ctx.Phase(PhaseId.Update);
                await ctx.Read<Health>(target);
                Thread.SpinWait(Random.Shared.Next(0, 20_000));
                ctx.Modify<Health>(target, h => new Health { Value = h.Value * 2 + ctx.TurnId });
            }));
        }

        RunUntil(() => contexts.All(c => c.Task.IsCompleted));

        var expected = 1;
        foreach (var turn in contexts.Select(c => c.TurnId).OrderBy(id => id))
        {
            expected = expected * 2 + turn;
        }

        Assert.That(_world.GetComponent<Health>(target).Value, Is.EqualTo(expected));
    }

    [Test]
    public void LabelledRead_WaitsForTheRound_AndSeesItsWrites()
    {
        var target = _world.CreateEntity(ComponentType<Health>.Id);
        _world.SetComponent(target, new Health { Value = 100 });

        var seenByHealer = 0;
        var seenInitial = 0;
        var attacker = Start(async ctx =>
        {
            await ctx.Phase(PhaseId.Update);
            await ctx.Read<Health>(target);
            Thread.SpinWait(50_000); // the healer must wait for this
            ctx.Modify<Health>(target, h => new Health { Value = h.Value - 10 }, Round.Label("damage"));
        });
        var healer = Start(async ctx =>
        {
            await ctx.Phase(PhaseId.Update);
            seenByHealer = (await ctx.Read<Health>(target, Round.Label("damage"))).Value;
            ctx.Modify<Health>(target, h => new Health { Value = h.Value + 5 });
        });
        var observer = Start(async ctx =>
        {
            await ctx.Phase(PhaseId.Update);
            seenInitial = (await ctx.Read<Health>(target)).Value;
        });

        RunUntil(() => attacker.Task.IsCompleted && healer.Task.IsCompleted && observer.Task.IsCompleted);
        Assert.Multiple(() =>
        {
            Assert.That(seenByHealer, Is.EqualTo(90), "reads the damage round after it closed");
            Assert.That(seenInitial, Is.EqualTo(100), "initial never sees behaviour writes");
            Assert.That(_world.GetComponent<Health>(target).Value, Is.EqualTo(95), "damage then main, committed in order");
        });
    }

    [Test]
    public void WritingToAnEarlierRound_AfterAdvancing_FailsTheBehaviour()
    {
        var target = _world.CreateEntity(ComponentType<Health>.Id);
        var context = Start(async ctx =>
        {
            await ctx.Phase(PhaseId.Update);
            await ctx.Read<Health>(target, Round.Label("damage")); // advances past damage
            ctx.Modify<Health>(target, h => h, Round.Label("damage"));
        });

        Assert.That(() => RunUntil(() => context.Task.IsCompleted),
            Throws.TypeOf<JobFailedException>().With.InnerException.InnerException.TypeOf<InvalidOperationException>());
        Assert.That(context.Task.IsFaulted, Is.True);
    }

    [Test]
    public void UnregisteredLabel_FailsTheBehaviour()
    {
        var target = _world.CreateEntity(ComponentType<Health>.Id);
        var context = Start(async ctx =>
        {
            await ctx.Phase(PhaseId.Update);
            ctx.Modify<Health>(target, h => h, Round.Label("no-such-round"));
        });

        Assert.That(() => RunUntil(() => context.Task.IsCompleted), Throws.TypeOf<JobFailedException>());
        Assert.That(context.Task.IsFaulted, Is.True);
    }

    [Test]
    public void CompletedRead_ResumesAtTheNextPhase_WithTheCommittedValue()
    {
        var target = _world.CreateEntity(ComponentType<Position>.Id);
        var writer = Start(async ctx =>
        {
            await ctx.Phase(PhaseId.Update);
            var p = await ctx.Write<Position>(target);
            p.Value.X = 7;
        });
        float? seen = null;
        var reader = Start(async ctx =>
        {
            await ctx.Phase(PhaseId.Update);
            seen = (await ctx.Read<Position>(target, Round.Completed)).X;
        });
        Frame(); // start: first segments run and park on Update

        Frame(); // writer writes, reader parks on completed
        Assert.Multiple(() =>
        {
            Assert.That(writer.Task.IsCompleted, Is.True);
            Assert.That(seen, Is.Null, "completed readers resume at the next phase");
            Assert.That(_graph.PendingIntakeCount, Is.EqualTo(1));
        });

        _graph.BeginPhase(PhaseId.Update);
        _graph.WaitAll();
        Assert.That(seen, Is.EqualTo(7));
        _graph.EndPhase();
        Assert.That(reader.Task.IsCompleted, Is.True);
    }

    [Test]
    public void ReadYourOwnWrites_WithinATurn_NotAcrossTurns()
    {
        var target = _world.CreateEntity(ComponentType<Position>.Id);
        float ownView = -1, otherView = -1;
        var writer = Start(async ctx =>
        {
            await ctx.Phase(PhaseId.Update);
            var p = await ctx.Write<Position>(target);
            p.Value.X = 3;
            ownView = (await ctx.Read<Position>(target)).X;
        });
        var other = Start(async ctx =>
        {
            await ctx.Phase(PhaseId.Update);
            Thread.SpinWait(20_000);
            otherView = (await ctx.Read<Position>(target)).X;
        });

        RunUntil(() => writer.Task.IsCompleted && other.Task.IsCompleted);
        Assert.Multiple(() =>
        {
            Assert.That(ownView, Is.EqualTo(3));
            Assert.That(otherView, Is.Zero);
        });
    }

    [Test]
    public void StartAfterEntryClosed_RunsAtTheNextPhase()
    {
        _graph.BeginPhase(PhaseId.Update);
        _graph.ResumePhase(PhaseId.Update); // entry closed
        var started = false;
        var context = Start(async ctx =>
        {
            started = true;
            await ctx.Phase(PhaseId.Update);
        });
        _graph.EndPhase();
        Assert.That(started, Is.False);

        _graph.BeginPhase(PhaseId.Update);
        _graph.WaitAll();
        Assert.That(started, Is.True);
        _graph.EndPhase();
        Assert.That(context.IsCancelled, Is.False);
    }

    [Test]
    public void WritesToDestroyedEntities_AreDroppedAtCommit()
    {
        var target = _world.CreateEntity(ComponentType<Position>.Id);
        var context = Start(async ctx =>
        {
            await ctx.Phase(PhaseId.Update);
            var p = await ctx.Write<Position>(target);
            p.Value.X = 1;
        });
        Frame(); // start: the first segment runs and parks on Update

        _graph.BeginPhase(PhaseId.Update);
        _graph.ResumePhase(PhaseId.Update);
        _graph.WaitAll();
        _world.DestroyEntity(target);
        Assert.That(() => _graph.EndPhase(), Throws.Nothing);
        Assert.That(context.Task.IsCompleted, Is.True);
    }

    [Test]
    public void WriteOutsideAPhase_Fails()
    {
        var target = _world.CreateEntity(ComponentType<Position>.Id);
        var context = Start(async ctx =>
        {
            var p = await ctx.Write<Position>(target);
            p.Value.X = 1;
        });

        _graph.DrainIntake(); // run the first segment outside any phase
        Assert.That(() => _graph.WaitAll(), Throws.TypeOf<JobFailedException>());
        Assert.That(context.Task.IsFaulted, Is.True);
    }

    [Test]
    public void Start_IsIssuedAtTheNextPhase_InTurnOrder()
    {
        var order = new List<int>();
        var contexts = new List<BehaviourContext>();
        for (var i = 0; i < 5; i++)
        {
            contexts.Add(Start(ctx =>
            {
                lock (order)
                {
                    order.Add(ctx.TurnId);
                }

                return default;
            }));
        }

        _graph.WaitAll();
        Assert.That(order, Is.Empty, "nothing runs before a phase begins");

        _graph.BeginPhase(PhaseId.Update);
        _graph.EndPhase();
        Assert.That(order.OrderBy(id => id), Is.EqualTo(contexts.Select(c => c.TurnId)));
    }
}
