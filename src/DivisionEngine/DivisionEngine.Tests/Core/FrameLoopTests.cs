using DivisionEngine.Tests.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace DivisionEngine.Tests.Core;

[TestFixture]
public sealed class FrameLoopTests
{
    private Engine _engine = null!;
    private List<(PhaseId phase, double time)> _log = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new Engine(NullLogger.Instance, new JobScheduler(2));
        _log = new List<(PhaseId, double)>();
        foreach (var group in _engine.Loop.Phases)
        {
            group.Add(new Probe(group.Phase, _log));
        }
    }

    [TearDown]
    public void TearDown()
    {
        _engine.Dispose();
    }

    private static Realtime At(double seconds)
    {
        return Realtime.FromTicks((long)Math.Round(seconds * 1000), 1000);
    }

    private static readonly PhaseId[] VariablePhases =
    [
        PhaseId.Update, PhaseId.PostUpdate, PhaseId.TransformPropagation, PhaseId.LateUpdate,
        PhaseId.Extract, PhaseId.Render, PhaseId.FrameEnd
    ];

    private static readonly PhaseId[] FixedPhases = [PhaseId.FixedPre, PhaseId.FixedUpdate, PhaseId.Physics, PhaseId.FixedPost];

    private sealed class Probe(PhaseId phase, List<(PhaseId, double)> log) : ISystem
    {
        public void Execute(ref FrameContext ctx)
        {
            log.Add((phase, ctx.ActiveTime.Current));
        }
    }

    private sealed class WritingSystem(PhaseId phase, EntityQuery query) : IJobSystem
    {
        public void Schedule(in JobSchedulingContext context)
        {
            context.Graph.ScheduleChunks($"write in {phase}", query, Access.Write<Position>(), (in _, chunk) =>
            {
                foreach (ref var p in chunk.GetSpan<Position>())
                {
                    p.X += 1;
                }
            });
        }
    }

    private sealed class Script(Func<BehaviourContext, BehaviourTask> body) : Behaviour
    {
        protected override BehaviourTask Run(BehaviourContext context)
        {
            return body(context);
        }
    }

    [Test]
    public void Phases_RunInOrder_OncePerFrame_WithoutFixedStepsBeforeTheFirstOneIsOwed()
    {
        _engine.RunFrame(At(0));
        _engine.RunFrame(At(0.01));

        var expected = new List<PhaseId>();
        for (var frame = 0; frame < 2; frame++)
        {
            expected.Add(PhaseId.FrameBegin);
            expected.AddRange(VariablePhases);
        }

        Assert.Multiple(() =>
        {
            Assert.That(_log.Select(e => e.phase), Is.EqualTo(expected));
            Assert.That(_log.Where(e => e.phase == PhaseId.Update).Select(e => e.time), Is.EqualTo(new[] { 0.0, 0.01 }).Within(1e-9));
            Assert.That(_engine.FrameIndex, Is.EqualTo(2));
        });
    }

    [Test]
    public void FixedPhases_RunOncePerOwedStep_UnderTheStepTime()
    {
        _engine.RunFrame(At(0));
        _log.Clear();
        _engine.RunFrame(At(0.05)); // 2 fixed steps (0.02) owed

        var expected = new List<PhaseId> { PhaseId.FrameBegin };
        expected.AddRange(FixedPhases);
        expected.AddRange(FixedPhases);
        expected.AddRange(VariablePhases);

        Assert.Multiple(() =>
        {
            Assert.That(_log.Select(e => e.phase), Is.EqualTo(expected));
            Assert.That(_log.Where(e => e.phase == PhaseId.FixedUpdate).Select(e => e.time), Is.EqualTo(new[] { 0.0, 0.02 }).Within(1e-9));
            Assert.That(_log.Single(e => e.phase == PhaseId.LateUpdate).time, Is.EqualTo(0.05).Within(1e-9), "variable phases use the frame time");
            Assert.That(_engine.Loop.FixedLoop.LastStepCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void Behaviour_AwaitingFixedUpdate_ResumesOncePerStep()
    {
        var count = 0;
        _engine.Graph.Start(_engine.World.CreateEntity(), new Script(async ctx =>
        {
            while (true)
            {
                await ctx.Phase(PhaseId.FixedUpdate);
                count++;
            }
            // ReSharper disable once FunctionNeverReturns
        }));

        _engine.RunFrame(At(0)); // started at the first dispatching phase (Update), parks on FixedUpdate
        Assert.That(count, Is.Zero);
        _engine.RunFrame(At(0.05)); // two steps
        Assert.That(count, Is.EqualTo(2));
        _engine.RunFrame(At(0.06)); // one step
        Assert.That(count, Is.EqualTo(3));
        _engine.RunFrame(At(0.065)); // none
        Assert.That(count, Is.EqualTo(3));
    }

    [Test]
    public void AddSystem_DefaultsToFrameBegin_AndPhasesAreAddressable()
    {
        var ran = new List<PhaseId>();
        _engine.AddSystem(new Probe(PhaseId.FrameBegin, _log));
        _engine.AddSystem(PhaseId.LateUpdate, new Probe(PhaseId.LateUpdate, _log));
        _engine.RunFrame(At(0));

        Assert.Multiple(() =>
        {
            Assert.That(_log.Count(e => e.phase == PhaseId.FrameBegin), Is.EqualTo(2));
            Assert.That(_log.Count(e => e.phase == PhaseId.LateUpdate), Is.EqualTo(2));
            Assert.That(_engine.Loop[PhaseId.Physics].DispatchesBehaviours, Is.False);
            Assert.That(_engine.Loop[PhaseId.Update].DispatchesBehaviours, Is.True);
            Assert.That(() => _engine.Loop[PhaseId.Register("no-such-phase")], Throws.TypeOf<KeyNotFoundException>());
        });
    }

    [Test]
    public void FrozenType_SystemWrite_IsRejectedWhenScheduled()
    {
        _engine.Loop.Freeze<Position>(PhaseId.LateUpdate, PhaseId.Extract);
        var query = _engine.World.Query().With<Position>().Build();
        _engine.World.CreateEntity(ComponentType<Position>.Id);
        _engine.AddSystem(PhaseId.Update, new WritingSystem(PhaseId.Update, query));
        Assert.That(() => _engine.RunFrame(At(0)), Throws.Nothing, "writing in an allowed phase");

        _engine.AddSystem(PhaseId.LateUpdate, new WritingSystem(PhaseId.LateUpdate, query));
        Assert.That(() => _engine.RunFrame(At(0.01)), Throws.InvalidOperationException.With.Message.Contains("frozen"));
    }

    [Test]
    public void FrozenType_BehaviourWrite_IsCarriedToTheNextAllowingPhase()
    {
        _engine.Loop.Freeze<Position>(PhaseId.LateUpdate, PhaseId.Extract, PhaseId.Render);
        var target = _engine.World.CreateEntity(ComponentType<Position>.Id);
        var seenInRender = new List<float>();
        _engine.AddSystem(PhaseId.Render, new Observer(target, seenInRender));

        var context = _engine.Graph.Start(_engine.World.CreateEntity(), new Script(async ctx =>
        {
            await ctx.Phase(PhaseId.LateUpdate);
            var p = await ctx.Write<Position>(target);
            p.Value.X = 9;
        }));

        // Frame 1: started in Update, parks on LateUpdate before it begins, so LateUpdate resumes it in the same
        // frame; the write is frozen there → carried past Extract and Render → committed at FrameEnd.
        _engine.RunFrame(At(0));
        Assert.Multiple(() =>
        {
            Assert.That(context.Task.IsCompleted, Is.True);
            Assert.That(seenInRender, Is.EqualTo(new[] { 0f }), "not visible while the type is frozen");
            Assert.That(_engine.World.GetComponent<Position>(target).X, Is.EqualTo(9), "committed by the first allowing phase");
        });

        _engine.RunFrame(At(0.01));
        Assert.That(seenInRender, Is.EqualTo(new[] { 0f, 9f }));
    }

    private sealed class Observer(Entity target, List<float> seen) : ISystem
    {
        public void Execute(ref FrameContext ctx)
        {
            seen.Add(ctx.World.GetComponent<Position>(target).X);
        }
    }

    [Test]
    public void NonDispatchingPhases_DoNotIssueIntake()
    {
        var startedIn = PhaseId.FrameBegin;
        var started = false;
        _engine.Graph.Start(_engine.World.CreateEntity(), new Script(ctx =>
        {
            startedIn = ctx.Graph.CurrentPhase!.Value;
            started = true;
            return default;
        }));

        _engine.RunFrame(At(0));
        Assert.Multiple(() =>
        {
            Assert.That(started, Is.True);
            Assert.That(startedIn, Is.EqualTo(PhaseId.Update), "FrameBegin does not dispatch; no fixed step was owed");
        });
    }

    [Test]
    public void RenderWorld_IsStampedDuringExtract()
    {
        Assert.That(_engine.RenderWorld.FrameIndex, Is.EqualTo(-1));
        _engine.RunFrame(At(0));
        Assert.That(_engine.RenderWorld.FrameIndex, Is.EqualTo(0));
        _engine.RunFrame(At(0.01));
        Assert.That(_engine.RenderWorld.FrameIndex, Is.EqualTo(1));
    }

    [Test]
    public void SameClockSampleTwice_GivesZeroDelta_AndNoFixedSteps()
    {
        _engine.RunFrame(At(0.05));
        _log.Clear();
        _engine.RunFrame(At(0.05));
        Assert.Multiple(() =>
        {
            Assert.That(_log.Any(e => FixedPhases.Contains(e.phase)), Is.False);
            Assert.That(_log.Single(e => e.phase == PhaseId.Update).time, Is.EqualTo(0).Within(1e-9), "frame clock counts from the first frame");
        });
    }
}
