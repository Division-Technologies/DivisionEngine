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
        time.Current = realtime.Seconds;
        time.Delta = time.Current - _accumulated;
        _accumulated += time.Delta;
        return true;
    }
}