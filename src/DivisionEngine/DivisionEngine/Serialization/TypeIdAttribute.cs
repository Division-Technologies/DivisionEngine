namespace DivisionEngine;

/// <summary>
///     Pins the serialized type ID of a class to an explicit GUID, decoupling persisted data from
///     the C# type name. Without the attribute the ID is an MD5 hash of <see cref="Type.FullName" />,
///     so before renaming or moving a class, pin its current ID (the analyzer code fix inserts the
///     matching value) to keep existing data readable. Once assigned, the ID must never change.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class TypeIdAttribute(string id) : Attribute
{
    public Guid Id { get; } = Guid.Parse(id);
}