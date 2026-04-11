namespace DivisionEngine;

public readonly record struct LocalId(int Value)
{
    internal int Value { get; } = Value;
}