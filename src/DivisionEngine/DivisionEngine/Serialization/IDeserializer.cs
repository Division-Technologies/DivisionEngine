namespace DivisionEngine;

public interface IDeserializer
{
    bool Bool(int id, ReadOnlySpan<byte> hintUtf8);
    sbyte I8(int id, ReadOnlySpan<byte> hintUtf8);
    short I16(int id, ReadOnlySpan<byte> hintUtf8);
    int I32(int id, ReadOnlySpan<byte> hintUtf8);
    long I64(int id, ReadOnlySpan<byte> hintUtf8);
    float F32(int id, ReadOnlySpan<byte> hintUtf8);
    double F64(int id, ReadOnlySpan<byte> hintUtf8);
    byte[] Blob(int id, ReadOnlySpan<byte> hintUtf8, out BlobKind kind);
    ISerializableObject? ObjectReference(int id, ReadOnlySpan<byte> hintUtf8);
    bool TryBeginArray(int id, ReadOnlySpan<byte> hintUtf8, out int length);
    void EndArray();
    bool TryBeginStruct(int id, ReadOnlySpan<byte> hintUtf8);
    void EndStruct();
}