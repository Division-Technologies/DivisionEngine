namespace DivisionEngine;

/// <summary>
///     Base class for serializable objects defined outside the engine assembly. It supplies the
///     <see cref="ISerializableObject" /> plumbing (<see cref="Scope" /> / <see cref="Id" />, whose
///     setters are internal to the engine) that user types cannot implement themselves. User types
///     derive from this and use [AutoSerialization] or implement <see cref="ISerializable" /> by hand.
/// </summary>
public abstract class SerializableObject : ISerializableObject
{
    /// <summary>The scope this object is materialized in. Assigned by the engine when the object is added or loaded.</summary>
    public SerializationScope Scope { get; internal set; } = null!;

    /// <summary>The object's id within <see cref="Scope" />. Assigned by the engine.</summary>
    public LocalId Id { get; internal set; }

    SerializationScope ISerializableObject.Scope
    {
        get => Scope;
        set => Scope = value;
    }

    LocalId ISerializableObject.Id
    {
        get => Id;
        set => Id = value;
    }
}