namespace DivisionEngine;

public interface IFormatter<T> where T : class, ISerializable<T>, new()
{
    void Serialize<TSerializer>(TSerializer serializer, T obj) where TSerializer : ISerializer, allows ref struct;
    void Deserialize<TDeserializer>(TDeserializer deserializer, T obj) where TDeserializer : IDeserializer, allows ref struct;
}