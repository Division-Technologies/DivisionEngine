namespace DivisionEngine;

[AttributeUsage(AttributeTargets.Field)]
public sealed class SerializeAttribute : Attribute
{
    private readonly int? _explicitId;
    private readonly string? _explicitName;

    public SerializeAttribute(int? explicitId = null, string? explicitName = null)
    {
    }
}