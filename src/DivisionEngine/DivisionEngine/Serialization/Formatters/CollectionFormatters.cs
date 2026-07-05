namespace DivisionEngine;

// Collection formatters encode null as length -1.

[CustomFormatter(typeof(List<>))]
internal sealed class ListFormatter<T> : IValueFormatter<List<T>>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in List<T> value)
        where TS : ISerializer, allows ref struct
    {
        if (value is null)
        {
            s.BeginArray(id, hint, -1);
            s.EndArray();
            return;
        }

        s.BeginArray(id, hint, value.Count);
        var formatter = FormatterStore<T>.Formatter;
        foreach (var item in value) formatter.Serialize(ref s, 0, ""u8, in item);
        s.EndArray();
    }

    public List<T> Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct
    {
        if (!d.TryBeginArray(id, hint, out var length)) return null!;
        if (length < 0)
        {
            d.EndArray();
            return null!;
        }

        var result = new List<T>(length);
        var formatter = FormatterStore<T>.Formatter;
        for (var i = 0; i < length; i++) result.Add(formatter.Deserialize(ref d, 0, ""u8));
        d.EndArray();
        return result;
    }
}

[CustomFormatter(typeof(HashSet<>))]
internal sealed class HashSetFormatter<T> : IValueFormatter<HashSet<T>>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in HashSet<T> value)
        where TS : ISerializer, allows ref struct
    {
        if (value is null)
        {
            s.BeginArray(id, hint, -1);
            s.EndArray();
            return;
        }

        s.BeginArray(id, hint, value.Count);
        var formatter = FormatterStore<T>.Formatter;
        foreach (var item in value) formatter.Serialize(ref s, 0, ""u8, in item);
        s.EndArray();
    }

    public HashSet<T> Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct
    {
        if (!d.TryBeginArray(id, hint, out var length)) return null!;
        if (length < 0)
        {
            d.EndArray();
            return null!;
        }

        var result = new HashSet<T>(length);
        var formatter = FormatterStore<T>.Formatter;
        for (var i = 0; i < length; i++) result.Add(formatter.Deserialize(ref d, 0, ""u8));
        d.EndArray();
        return result;
    }
}

[CustomFormatter(typeof(Dictionary<,>))]
internal sealed class DictionaryFormatter<TKey, TValue> : IValueFormatter<Dictionary<TKey, TValue>>
    where TKey : notnull
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in Dictionary<TKey, TValue> value)
        where TS : ISerializer, allows ref struct
    {
        if (value is null)
        {
            s.BeginArray(id, hint, -1);
            s.EndArray();
            return;
        }

        s.BeginArray(id, hint, value.Count);
        var keyFormatter = FormatterStore<TKey>.Formatter;
        var valueFormatter = FormatterStore<TValue>.Formatter;
        foreach (var (k, v) in value)
        {
            s.BeginStruct(0, ""u8);
            keyFormatter.Serialize(ref s, 0, "key"u8, in k);
            valueFormatter.Serialize(ref s, 1, "value"u8, in v);
            s.EndStruct();
        }

        s.EndArray();
    }

    public Dictionary<TKey, TValue> Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct
    {
        if (!d.TryBeginArray(id, hint, out var length)) return null!;
        if (length < 0)
        {
            d.EndArray();
            return null!;
        }

        var result = new Dictionary<TKey, TValue>(length);
        var keyFormatter = FormatterStore<TKey>.Formatter;
        var valueFormatter = FormatterStore<TValue>.Formatter;
        for (var i = 0; i < length; i++)
        {
            if (!d.TryBeginStruct(0, ""u8)) continue;
            var k = keyFormatter.Deserialize(ref d, 0, "key"u8);
            var v = valueFormatter.Deserialize(ref d, 1, "value"u8);
            d.EndStruct();
            result[k] = v;
        }

        d.EndArray();
        return result;
    }
}

[CustomFormatter(typeof(Nullable<>))]
internal sealed class NullableFormatter<T> : IValueFormatter<T?> where T : struct
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in T? value)
        where TS : ISerializer, allows ref struct
    {
        s.BeginStruct(id, hint);
        s.Bool(0, "hasValue"u8, value.HasValue);
        if (value.HasValue)
        {
            var inner = value.GetValueOrDefault();
            FormatterStore<T>.Formatter.Serialize(ref s, 1, "value"u8, in inner);
        }

        s.EndStruct();
    }

    public T? Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct
    {
        if (!d.TryBeginStruct(id, hint)) return null;
        var hasValue = d.Bool(0, "hasValue"u8);
        T? result = hasValue ? FormatterStore<T>.Formatter.Deserialize(ref d, 1, "value"u8) : null;
        d.EndStruct();
        return result;
    }
}