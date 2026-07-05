using System.Runtime.InteropServices;

namespace DivisionEngine;

[CustomFormatter(typeof(byte[]))]
internal readonly struct ByteArrayFormatter : IValueFormatter<byte[]>
{
    // null round-trips as [] (Blob cannot express null)
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in byte[] value)
        where TS : ISerializer, allows ref struct
    {
        s.Blob(id, hint, value ?? [], BlobKind.ByteArray);
    }

    public byte[] Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct
    {
        return d.Blob(id, hint, out _);
    }
}

[CustomFormatter(typeof(Guid))]
internal readonly struct GuidFormatter : IValueFormatter<Guid>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in Guid value)
        where TS : ISerializer, allows ref struct
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);
        s.Blob(id, hint, bytes, BlobKind.ByteArray);
    }

    public Guid Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct
    {
        var bytes = d.Blob(id, hint, out _);
        return bytes.Length == 16 ? new Guid(bytes) : default;
    }
}

[CustomFormatter(typeof(DateTime))]
internal readonly struct DateTimeFormatter : IValueFormatter<DateTime>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in DateTime value)
        where TS : ISerializer, allows ref struct
    {
        s.I64(id, hint, value.ToBinary());
    }

    public DateTime Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct
    {
        return DateTime.FromBinary(d.I64(id, hint));
    }
}

[CustomFormatter(typeof(DateTimeOffset))]
internal readonly struct DateTimeOffsetFormatter : IValueFormatter<DateTimeOffset>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in DateTimeOffset value)
        where TS : ISerializer, allows ref struct
    {
        s.BeginStruct(id, hint);
        s.I64(0, "ticks"u8, value.Ticks);
        s.I64(1, "offset"u8, value.Offset.Ticks);
        s.EndStruct();
    }

    public DateTimeOffset Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct
    {
        if (!d.TryBeginStruct(id, hint)) return default;
        var ticks = d.I64(0, "ticks"u8);
        var offset = d.I64(1, "offset"u8);
        d.EndStruct();
        return new DateTimeOffset(ticks, new TimeSpan(offset));
    }
}

[CustomFormatter(typeof(TimeSpan))]
internal readonly struct TimeSpanFormatter : IValueFormatter<TimeSpan>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in TimeSpan value)
        where TS : ISerializer, allows ref struct
    {
        s.I64(id, hint, value.Ticks);
    }

    public TimeSpan Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct
    {
        return new TimeSpan(d.I64(id, hint));
    }
}

[CustomFormatter(typeof(decimal))]
internal readonly struct DecimalFormatter : IValueFormatter<decimal>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in decimal value)
        where TS : ISerializer, allows ref struct
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        s.Blob(id, hint, MemoryMarshal.AsBytes(bits), BlobKind.ByteArray);
    }

    public decimal Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct
    {
        var bytes = d.Blob(id, hint, out _);
        return bytes.Length == 16 ? new decimal(MemoryMarshal.Cast<byte, int>(bytes)) : default;
    }
}