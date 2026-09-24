namespace DivisionEngine;

public interface ISerializer
{
    void Bool(int id, ReadOnlySpan<byte> hintUtf8, bool value);
    void I8(int id, ReadOnlySpan<byte> hintUtf8, sbyte value);
    void I16(int id, ReadOnlySpan<byte> hintUtf8, short value);
    void I32(int id, ReadOnlySpan<byte> hintUtf8, int value);
    void I64(int id, ReadOnlySpan<byte> hintUtf8, long value);
    void F32(int id, ReadOnlySpan<byte> hintUtf8, float value);
    void F64(int id, ReadOnlySpan<byte> hintUtf8, double value);
    void Blob(int id, ReadOnlySpan<byte> hintUtf8, scoped ReadOnlySpan<byte> value, BlobKind kind);
    void ObjectReference(int id, ReadOnlySpan<byte> hintUtf8, ISerializableObject? value);
    void BeginArray(int id, ReadOnlySpan<byte> hintUtf8, int length);
    void EndArray();
    void BeginStruct(int id, ReadOnlySpan<byte> hintUtf8);
    void EndStruct();

    /// <summary>
    ///     Writes a node captured by <see cref="IDeserializer.RawNode" /> back unchanged: data that
    ///     was read without a type to read it into, kept so that saving does not lose it.
    /// </summary>
    void RawNode(int id, ReadOnlySpan<byte> hintUtf8, ReadOnlyMemory<byte> node);
}