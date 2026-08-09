namespace DivisionEngine;

public readonly struct Time
{
    public readonly double Current;
    public readonly double Delta;

    public Time(double current, double delta)
    {
        Current = current;
        Delta = delta;
    }
}