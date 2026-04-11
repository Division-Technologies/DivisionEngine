namespace DivisionEngine;

public interface ISerializableObject : ISerializable
{
    SerializationScope Scope { get; internal set; }
    LocalId Id { get; internal set; }
}