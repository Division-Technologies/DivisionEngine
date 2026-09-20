namespace DivisionEngine.Tests.Core;

[TestFixture]
public sealed class FixedUpdateTimeProviderTests
{
    // Use an integer frequency so seconds are exact for the values used here.
    private static Realtime At(double seconds)
    {
        return Realtime.FromTicks((long)Math.Round(seconds * 1000), 1000);
    }

    private static List<Time> Drain(ref FixedUpdateTimeProvider provider, Realtime realtime)
    {
        var steps = new List<Time>();
        while (provider.DoUpdate(realtime, out var time))
        {
            steps.Add(time);
        }

        return steps;
    }

    [Test]
    public void FirstFrame_YieldsNoSteps()
    {
        var provider = new FixedUpdateTimeProvider(0.02);
        Assert.That(Drain(ref provider, At(0)), Is.Empty);
    }

    [Test]
    public void YieldsOwedSteps_WithContinuousClock()
    {
        var provider = new FixedUpdateTimeProvider(0.02);
        Drain(ref provider, At(0));

        var steps = Drain(ref provider, At(0.05));
        Assert.That(steps, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(steps[0].Current, Is.EqualTo(0.00).Within(1e-9));
            Assert.That(steps[0].Delta, Is.EqualTo(0.02).Within(1e-9));
            Assert.That(steps[1].Current, Is.EqualTo(0.02).Within(1e-9));
        });

        // Same realtime again within the frame: nothing more.
        Assert.That(Drain(ref provider, At(0.05)), Is.Empty);

        // 0.06s elapsed → 3 steps owed in total, 2 done.
        steps = Drain(ref provider, At(0.06));
        Assert.That(steps, Has.Count.EqualTo(1));
        Assert.That(steps[0].Current, Is.EqualTo(0.04).Within(1e-9));
    }

    [Test]
    public void CatchUp_IsCapped_AndSimulationLagsInsteadOfJumping()
    {
        var provider = new FixedUpdateTimeProvider(0.02, 4);
        Drain(ref provider, At(0));

        // 1.0s stall → 50 steps owed, only 4 run.
        var steps = Drain(ref provider, At(1.0));
        Assert.That(steps, Has.Count.EqualTo(4));
        Assert.That(steps[3].Current, Is.EqualTo(0.06).Within(1e-9), "clock must stay continuous");

        // Origin moved forward: the next 0.02s of real time owes exactly one more step, continuing the clock.
        steps = Drain(ref provider, At(1.02));
        Assert.That(steps, Has.Count.EqualTo(1));
        Assert.That(steps[0].Current, Is.EqualTo(0.08).Within(1e-9));
    }

    [Test]
    public void RejectsInvalidArguments()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => new FixedUpdateTimeProvider(0), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new FixedUpdateTimeProvider(0.02, 0), Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }
}

[TestFixture]
public sealed class UpdateTimeProviderTests
{
    private static Realtime At(double seconds)
    {
        return Realtime.FromTicks((long)Math.Round(seconds * 1000), 1000);
    }

    [Test]
    public void OneUpdatePerDistinctRealtime_CountingFromFirstUpdate()
    {
        var provider = new UpdateTimeProvider();

        // Arbitrary (Stopwatch-like) origin must not leak into Current or the first Delta.
        Assert.That(provider.DoUpdate(At(1000.0), out var t0), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(t0.Current, Is.EqualTo(0).Within(1e-9));
            Assert.That(t0.Delta, Is.EqualTo(0).Within(1e-9));
        });

        Assert.That(provider.DoUpdate(At(1000.0), out _), Is.False, "same realtime → no second update");

        Assert.That(provider.DoUpdate(At(1000.016), out var t1), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(t1.Current, Is.EqualTo(0.016).Within(1e-9));
            Assert.That(t1.Delta, Is.EqualTo(0.016).Within(1e-9));
        });
    }
}
