namespace DivisionEngine;

public interface ISerializedObjectResolver
{
    ISerializableObject? Resolve(GlobalId id);
}