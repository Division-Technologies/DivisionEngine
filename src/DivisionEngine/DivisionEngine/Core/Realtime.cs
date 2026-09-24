using System.Diagnostics;

namespace DivisionEngine;

/// <summary>
///     A wall-clock timestamp. <see cref="Current" /> samples the high-resolution Stopwatch;
///     <see cref="FromTicks" /> / <see cref="FromSeconds" /> construct explicit values for tests,
///     headless runs and replay.
/// </summary>
public readonly record struct Realtime
{
    public readonly long Frequency;
    public readonly long Tick;

    private Realtime(long tick, long frequency)
    {
        Tick = tick;
        Frequency = frequency;
    }

    public double Seconds => Tick / (double)Frequency;

    public static Realtime Current => new(Stopwatch.GetTimestamp(), Stopwatch.Frequency);

    public static Realtime FromTicks(long tick, long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        return new Realtime(tick, frequency);
    }

    /// <summary>Builds a timestamp from seconds using the Stopwatch frequency.</summary>
    public static Realtime FromSeconds(double seconds)
    {
        return new Realtime((long)Math.Round(seconds * Stopwatch.Frequency), Stopwatch.Frequency);
    }
}