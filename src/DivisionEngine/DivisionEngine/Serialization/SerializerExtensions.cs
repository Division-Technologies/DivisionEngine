using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace DivisionEngine;

public static class SerializerExtensions
{
    public static void Utf16<T>(ref T serializer, int id, ReadOnlySpan<byte> hintUtf8, ReadOnlySpan<char> value)
        where T : ISerializer, allows ref struct
    {
        serializer.Blob(id, hintUtf8, MemoryMarshal.Cast<char, byte>(value), BlobKind.Utf16);
    }

    public static void Utf8<T>(ref T serializer, int id, ReadOnlySpan<byte> hintUtf8, ReadOnlySpan<char> value)
        where T : ISerializer, allows ref struct
    {
        var utf8 = Encoding.UTF8;
        var maxCount = utf8.GetMaxByteCount(value.Length);
        if (maxCount <= 1024)
        {
            Span<byte> buffer = stackalloc byte[maxCount];
            buffer = buffer[..utf8.GetBytes(value, buffer)];
            serializer.Blob(id, hintUtf8, buffer, BlobKind.Utf8);
        }
        else
        {
            var array = ArrayPool<byte>.Shared.Rent(maxCount);
            try
            {
                var buffer = array.AsMemory(0, maxCount).Span;
                buffer = buffer[..utf8.GetBytes(value, buffer)];
                serializer.Blob(id, hintUtf8, buffer, BlobKind.Utf8);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(array);
            }
        }
    }
}