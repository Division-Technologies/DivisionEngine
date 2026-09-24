namespace DivisionEngine;

/// <summary>
///     A system group with its own time axis: each frame the provider decides how many steps are
///     owed and the children (typically <see cref="PhaseGroup" />s) run once per step under that
///     step's time.
/// </summary>
public sealed class TimeSteppedGroup<T> : SystemGroup where T : struct, ITimeProvider
{
    private T _provider;

    public TimeSteppedGroup(T provider)
    {
        _provider = provider;
    }

    public ref T Provider => ref _provider;

    /// <summary>Steps executed during the last frame.</summary>
    public int LastStepCount { get; private set; }

    public override void Execute(ref FrameContext ctx)
    {
        var steps = 0;
        while (_provider.DoUpdate(ctx.Realtime, out var time))
        {
            steps++;
            var stepTime = time;
            var stepContext = new FrameContext(ref stepTime, ctx.Realtime, ctx.Engine);
            base.Execute(ref stepContext);
        }

        LastStepCount = steps;
    }
}