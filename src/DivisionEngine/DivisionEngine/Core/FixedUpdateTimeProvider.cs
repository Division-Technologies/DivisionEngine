namespace DivisionEngine;

public struct FixedUpdateTimeProvider : ITimeProvider
{
    private double _fixedDeltaTime;
    private Realtime? _initial;
    private int _frameCount;
    private double _accumulated;

    public FixedUpdateTimeProvider(double fixedDeltaTime)
    {
        _fixedDeltaTime = fixedDeltaTime;
    }

    public bool DoUpdate(Realtime realtime, out Time time)
    {
        var initial = _initial ??= realtime;

        var expectedCount = Math.Floor((realtime.Seconds - initial.Seconds) / _frameCount);

        if (_frameCount >= expectedCount)
        {
            time = default;
            return false;
        }

        time.Current = _accumulated + _frameCount++ * _fixedDeltaTime;
        time.Delta = _fixedDeltaTime;
        return true;
    }
}