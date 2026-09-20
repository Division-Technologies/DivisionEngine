namespace DivisionEngine;

/// <summary>
///     Fixed-timestep time provider (accumulator style). Each frame it computes how many fixed steps
///     are owed given the elapsed real time and yields them one by one; the same
///     <see cref="Realtime" /> may be passed repeatedly within a frame and only the first call
///     recomputes the budget.
///     Catch-up is capped by <c>maxStepsPerFrame</c>. When more steps are owed than the cap, the
///     excess is dropped by moving the simulation origin forward, so simulated time lags real time
///     instead of jumping (Unity's <c>maximumDeltaTime</c> behavior). The simulated clock therefore
///     stays continuous across steps.
/// </summary>
public struct FixedUpdateTimeProvider : ITimeProvider
{
    public const int DefaultMaxStepsPerFrame = 8;
    private const double StepEpsilon = 1e-6;

    private readonly double _fixedDeltaTime;
    private readonly int _maxStepsPerFrame;
    private bool _started;
    private double _originSeconds;
    private long _stepCount;
    private long _frameBudget;
    private Realtime _lastRealtime;

    public FixedUpdateTimeProvider(double fixedDeltaTime, int maxStepsPerFrame = DefaultMaxStepsPerFrame)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fixedDeltaTime);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxStepsPerFrame);
        _fixedDeltaTime = fixedDeltaTime;
        _maxStepsPerFrame = maxStepsPerFrame;
    }

    public double FixedDeltaTime => _fixedDeltaTime;

    public bool DoUpdate(Realtime realtime, out Time time)
    {
        if (!_started)
        {
            _started = true;
            _originSeconds = realtime.Seconds;
            _lastRealtime = realtime;
            _frameBudget = 0;
        }
        else if (realtime != _lastRealtime)
        {
            _lastRealtime = realtime;
            var elapsed = realtime.Seconds - _originSeconds;
            // Bias by a fraction of a step so that an elapsed time that lands exactly on a step
            // boundary (e.g. 0.06 / 0.02) is not floored to one step short by rounding error.
            var expected = (long)Math.Floor(elapsed / _fixedDeltaTime + StepEpsilon);
            var pending = expected - _stepCount;
            if (pending > _maxStepsPerFrame)
            {
                // Drop the excess: shift the origin so that after this frame's steps the
                // simulation is exactly maxStepsPerFrame steps behind where it would have been.
                var dropped = pending - _maxStepsPerFrame;
                _originSeconds += dropped * _fixedDeltaTime;
                pending = _maxStepsPerFrame;
            }

            _frameBudget = pending;
        }

        if (_frameBudget <= 0)
        {
            time = default;
            return false;
        }

        _frameBudget--;
        time = new Time(_stepCount * _fixedDeltaTime, _fixedDeltaTime);
        _stepCount++;
        return true;
    }
}
