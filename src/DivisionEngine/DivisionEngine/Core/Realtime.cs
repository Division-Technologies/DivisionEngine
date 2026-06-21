using System.Diagnostics;

namespace DivisionEngine;

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
}