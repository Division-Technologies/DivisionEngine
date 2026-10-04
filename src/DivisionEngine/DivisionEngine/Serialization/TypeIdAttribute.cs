namespace DivisionEngine;

/// <summary>
///     Pins the serialized type ID of a type to an explicit GUID, decoupling persisted data from the
///     C# type name. Without the attribute the ID is an MD5 hash of <see cref="Type.FullName" />, so
///     before renaming or moving a type, pin its current ID (the analyzer code fix inserts the
///     matching value) to keep existing data readable. Once assigned, the ID must never change.
///     Structs are covered as well as classes because component structs are identified by this ID in
///     saved scenes (see <see cref="ComponentAttribute" />).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class TypeIdAttribute(string id) : Attribute
{
    public Guid Id { get; } = Guid.Parse(id);
}