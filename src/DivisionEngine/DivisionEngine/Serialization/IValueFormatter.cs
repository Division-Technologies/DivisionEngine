namespace DivisionEngine;

/// <summary>
///     Reads and writes a value of type <typeparamref name="T" /> as a single keyed (id/hint) entry.
///     Scalar types write one value (e.g. a Blob); composite types frame themselves with
///     BeginStruct/EndStruct. Formatters for reference types are responsible for encoding null.
///     Serializers are mutable ref structs and must always be passed by ref.
/// </summary>
public interface IValueFormatter<T>
{
    void Serialize<TSerializer>(ref TSerializer serializer, int id, ReadOnlySpan<byte> hintUtf8, in T value)
        where TSerializer : ISerializer, allows ref struct;

    T Deserialize<TDeserializer>(ref TDeserializer deserializer, int id, ReadOnlySpan<byte> hintUtf8)
        where TDeserializer : IDeserializer, allows ref struct;
}
