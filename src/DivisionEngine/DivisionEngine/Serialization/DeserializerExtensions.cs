using System.Text;

namespace DivisionEngine;

public static class DeserializerExtensions
{
    public static string String<T>(this T deserializer, int id, ReadOnlySpan<byte> hintUtf8)
        where T : IDeserializer, allows ref struct
    {
        var bytes = deserializer.Blob(id, hintUtf8, out var kind);
        switch (kind)
        {
            case BlobKind.ByteArray:
            {
                throw new InvalidOperationException("Cannot deserialize byte array as string.");
            }
            case BlobKind.Utf8:
            {
                return Encoding.UTF8.GetString(bytes);
            }
            case BlobKind.Utf16:
            {
                return Encoding.Unicode.GetString(bytes);
            }
            default:
                throw new ArgumentOutOfRangeException();
        }
    }
}