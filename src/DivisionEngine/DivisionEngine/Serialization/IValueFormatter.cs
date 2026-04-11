namespace DivisionEngine;

public interface IValueFormatter<T> where T : struct, IValueSerializable<T>
{
    void Serialize<TSerializer>(TSerializer serializer, ref T obj) where TSerializer : ISerializer, allows ref struct;

    void Deserialize<TDeserializer>(TDeserializer deserializer, ref T obj)
        where TDeserializer : IDeserializer, allows ref struct;
}