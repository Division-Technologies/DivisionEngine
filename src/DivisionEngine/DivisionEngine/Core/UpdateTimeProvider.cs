namespace DivisionEngine;

public struct UpdateTimeProvider : ITimeProvider
{
    private Realtime _prev;
    private double _accumulated;

    public bool DoUpdate(Realtime realtime, out Time time)
    {
        if (realtime == _prev)
        {
            time = default;
            return false;
        }

        _prev = realtime;
        time = new Time(realtime.Seconds, realtime.Seconds - _accumulated);
        _accumulated += time.Delta;
        return true;
    }
}