using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using ValueTaskSupplement;

namespace DivisionEngine;

public interface ISerializable
{
    void Serialize<T>(T serializer) where T : ISerializer, allows ref struct
    {
        throw new NotImplementedException();
    }

    void Deserialize<T>(T deserializer) where T : IDeserializer, allows ref struct
    {
        throw new NotImplementedException();
    }
}

public interface ISerializable<T> : ISerializable where T : class, ISerializable<T>, new()
{
    static abstract IFormatter<T> Formatter { get; }

    void ISerializable.Serialize<TSerializer>(TSerializer serializer)
    {
        T.Formatter.Serialize(serializer, (T)this);
    }

    void ISerializable.Deserialize<TDeserializer>(TDeserializer deserializer)
    {
        T.Formatter.Deserialize(deserializer, (T)this);
    }
}
