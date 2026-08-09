using System.Diagnostics.CodeAnalysis;

namespace DivisionEngine;

public interface IContainerDeserializer : IDeserializer
{
    bool TryBeginObject(out LocalId id, [NotNullWhen(true)] out Type? type);
    void EndObject();
}