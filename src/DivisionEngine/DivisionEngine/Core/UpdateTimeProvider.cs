namespace DivisionEngine;

/// <summary>
///     Variable-timestep time provider: yields exactly one update per distinct <see cref="Realtime" />.
///     <see cref="Time.Current" /> counts from the first update (not from the arbitrary Stopwatch
///     origin) and the first update reports a zero delta.
/// </summary>
public struct UpdateTimeProvider : ITimeProvider
{
    private bool _started;
    private Realtime _prev;
    private double _originSeconds;

    public bool DoUpdate(Realtime realtime, out Time time)
    {
        if (!_started)
        {
            _started = true;
            _prev = realtime;
            _originSeconds = realtime.Seconds;
            time = new Time(0, 0);
            return true;
        }

        if (realtime == _prev)
        {
            time = default;
            return false;
        }

        var delta = realtime.Seconds - _prev.Seconds;
        _prev = realtime;
        time = new Time(realtime.Seconds - _originSeconds, delta);
        return true;
    }
}
