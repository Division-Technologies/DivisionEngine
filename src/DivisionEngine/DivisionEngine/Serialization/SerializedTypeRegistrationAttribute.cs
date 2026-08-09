namespace DivisionEngine;

/// <summary>
///     Emitted by the source generator for every serializable class: its serialized type ID (the
///     <see cref="TypeIdAttribute" /> GUID, or the hash of the type name) and the type itself.
///     Type resolvers read these assembly-level attributes to map IDs back to runtime types
///     without enumerating every type in the assembly.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class SerializedTypeRegistrationAttribute(string typeId, Type type) : Attribute
{
    // "Id" rather than "TypeId" — System.Attribute already declares a virtual TypeId member.
    public string Id { get; } = typeId;
    public Type Type { get; } = type;
}