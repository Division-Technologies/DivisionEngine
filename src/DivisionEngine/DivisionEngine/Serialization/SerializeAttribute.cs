namespace DivisionEngine;

[AttributeUsage(AttributeTargets.Field)]
public sealed class SerializeAttribute : Attribute
{
    private readonly int? _explicitId;
    private readonly string? _explicitName;

    public SerializeAttribute()
    {
    }

    public SerializeAttribute(int explicitId)
    {
        _explicitId = explicitId;
    }

    public SerializeAttribute(string explicitName)
    {
        _explicitName = explicitName;
    }

    public SerializeAttribute(int explicitId, string explicitName)
    {
        _explicitId = explicitId;
        _explicitName = explicitName;
    }
}