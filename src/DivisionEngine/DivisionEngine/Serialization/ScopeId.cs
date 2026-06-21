namespace DivisionEngine;

public readonly record struct ScopeId(Guid Value)
{
    internal Guid Value { get; } = Value;

    internal static ScopeId New()
    {
        return new ScopeId(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("N");
    }
}