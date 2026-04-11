namespace DivisionEngine;

public readonly record struct ScopeId(Guid Value)
{
    internal Guid Value { get; } = Value;
    internal static ScopeId New() => new(Guid.NewGuid());
}