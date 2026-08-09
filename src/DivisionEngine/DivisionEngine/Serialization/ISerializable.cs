namespace DivisionEngine;

/// <summary>
///     Scope-level serialization entry point. [AutoSerialization] generates explicit
///     implementations of these members; field values are dispatched through
///     <see cref="FormatterStore{T}" />.
///     Serializers are mutable ref structs and must always be passed by ref — a by-value copy
///     forks the underlying writer/parser state.
/// </summary>
public interface ISerializable
{
    void Serialize<T>(ref T serializer) where T : ISerializer, allows ref struct
    {
        throw new NotImplementedException();
    }

    void Deserialize<T>(ref T deserializer) where T : IDeserializer, allows ref struct
    {
        throw new NotImplementedException();
    }
}