namespace DivisionEngine;

public interface ISerializationScopeLoader : IDisposable
{
    ISerializableObject? Load(LocalId id);
    void Deserialize(ISerializableObject obj, ISerializedObjectResolver resolver);
}