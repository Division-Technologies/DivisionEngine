namespace DivisionEngine;

public interface IContainerSerializer : ISerializer
{
    void BeginObject(LocalId id, Type type);
    void EndObject();
}