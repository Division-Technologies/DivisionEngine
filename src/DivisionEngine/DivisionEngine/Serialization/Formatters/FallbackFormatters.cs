using System.Runtime.CompilerServices;

namespace DivisionEngine;

// Created reflectively by FormatterRegistry's fallback chain — no [CustomFormatter] needed.

internal sealed class EnumFormatter<T> : IValueFormatter<T> where T : struct, Enum
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in T value)
        where TS : ISerializer, allows ref struct
    {
        switch (Unsafe.SizeOf<T>())
        {
            case 1:
                s.I8(id, hint, Unsafe.BitCast<T, sbyte>(value));
                break;
            case 2:
                s.I16(id, hint, Unsafe.BitCast<T, short>(value));
                break;
            case 4:
                s.I32(id, hint, Unsafe.BitCast<T, int>(value));
                break;
            case 8:
                s.I64(id, hint, Unsafe.BitCast<T, long>(value));
                break;
        }
    }

    public T Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct
    {
        return Unsafe.SizeOf<T>() switch
        {
            1 => Unsafe.BitCast<sbyte, T>(d.I8(id, hint)),
            2 => Unsafe.BitCast<short, T>(d.I16(id, hint)),
            4 => Unsafe.BitCast<int, T>(d.I32(id, hint)),
            8 => Unsafe.BitCast<long, T>(d.I64(id, hint)),
            _ => throw new NotSupportedException()
        };
    }
}

/// <summary>
///     Serializes a reference to a scope-managed object (scope id + local id), not the object
///     itself. null is encoded as (Guid.Empty, -1), which the resolver fails to resolve → null.
/// </summary>
internal sealed class ObjectReferenceFormatter<T> : IValueFormatter<T> where T : class, ISerializableObject
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in T value)
        where TS : ISerializer, allows ref struct
    {
        s.ObjectReference(id, hint, value);
    }

    public T Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct
    {
        return (T)d.ObjectReference(id, hint)!;
    }
}

/// <summary>Rank-1 arrays. null is encoded as length -1. byte[] has a dedicated Blob formatter.</summary>
internal sealed class ArrayFormatter<T> : IValueFormatter<T[]>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in T[] value)
        where TS : ISerializer, allows ref struct
    {
        if (value is null)
        {
            s.BeginArray(id, hint, -1);
            s.EndArray();
            return;
        }

        s.BeginArray(id, hint, value.Length);
        var formatter = FormatterStore<T>.Formatter;
        foreach (var item in value) formatter.Serialize(ref s, 0, ""u8, in item);
        s.EndArray();
    }

    public T[] Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct
    {
        if (!d.TryBeginArray(id, hint, out var length)) return null!;
        if (length < 0)
        {
            d.EndArray();
            return null!;
        }

        var result = new T[length];
        var formatter = FormatterStore<T>.Formatter;
        for (var i = 0; i < length; i++) result[i] = formatter.Deserialize(ref d, 0, ""u8);
        d.EndArray();
        return result;
    }
}
