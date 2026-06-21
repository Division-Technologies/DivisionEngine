namespace DivisionEngine;

// Primitive formatters exist so primitives can appear as elements of collections
// (List<int>, int[], ...). Top-level [Serialize] fields of these types are emitted
// directly by the generator and never go through the store.

[CustomFormatter(typeof(bool))]
internal readonly struct BoolFormatter : IValueFormatter<bool>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in bool value)
        where TS : ISerializer, allows ref struct => s.Bool(id, hint, value);

    public bool Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct => d.Bool(id, hint);
}

[CustomFormatter(typeof(sbyte))]
internal readonly struct SByteFormatter : IValueFormatter<sbyte>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in sbyte value)
        where TS : ISerializer, allows ref struct => s.I8(id, hint, value);

    public sbyte Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct => d.I8(id, hint);
}

[CustomFormatter(typeof(byte))]
internal readonly struct ByteFormatter : IValueFormatter<byte>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in byte value)
        where TS : ISerializer, allows ref struct => s.I8(id, hint, unchecked((sbyte)value));

    public byte Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct => unchecked((byte)d.I8(id, hint));
}

[CustomFormatter(typeof(short))]
internal readonly struct Int16Formatter : IValueFormatter<short>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in short value)
        where TS : ISerializer, allows ref struct => s.I16(id, hint, value);

    public short Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct => d.I16(id, hint);
}

[CustomFormatter(typeof(ushort))]
internal readonly struct UInt16Formatter : IValueFormatter<ushort>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in ushort value)
        where TS : ISerializer, allows ref struct => s.I16(id, hint, unchecked((short)value));

    public ushort Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct => unchecked((ushort)d.I16(id, hint));
}

[CustomFormatter(typeof(char))]
internal readonly struct CharFormatter : IValueFormatter<char>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in char value)
        where TS : ISerializer, allows ref struct => s.I16(id, hint, unchecked((short)value));

    public char Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct => unchecked((char)d.I16(id, hint));
}

[CustomFormatter(typeof(int))]
internal readonly struct Int32Formatter : IValueFormatter<int>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in int value)
        where TS : ISerializer, allows ref struct => s.I32(id, hint, value);

    public int Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct => d.I32(id, hint);
}

[CustomFormatter(typeof(uint))]
internal readonly struct UInt32Formatter : IValueFormatter<uint>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in uint value)
        where TS : ISerializer, allows ref struct => s.I32(id, hint, unchecked((int)value));

    public uint Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct => unchecked((uint)d.I32(id, hint));
}

[CustomFormatter(typeof(long))]
internal readonly struct Int64Formatter : IValueFormatter<long>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in long value)
        where TS : ISerializer, allows ref struct => s.I64(id, hint, value);

    public long Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct => d.I64(id, hint);
}

[CustomFormatter(typeof(ulong))]
internal readonly struct UInt64Formatter : IValueFormatter<ulong>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in ulong value)
        where TS : ISerializer, allows ref struct => s.I64(id, hint, unchecked((long)value));

    public ulong Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct => unchecked((ulong)d.I64(id, hint));
}

[CustomFormatter(typeof(float))]
internal readonly struct SingleFormatter : IValueFormatter<float>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in float value)
        where TS : ISerializer, allows ref struct => s.F32(id, hint, value);

    public float Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct => d.F32(id, hint);
}

[CustomFormatter(typeof(double))]
internal readonly struct DoubleFormatter : IValueFormatter<double>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in double value)
        where TS : ISerializer, allows ref struct => s.F64(id, hint, value);

    public double Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct => d.F64(id, hint);
}

[CustomFormatter(typeof(string))]
internal readonly struct StringFormatter : IValueFormatter<string>
{
    // null round-trips as "" (Blob cannot express null)
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in string value)
        where TS : ISerializer, allows ref struct => SerializerExtensions.Utf16(ref s, id, hint, value ?? "");

    public string Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct => DeserializerExtensions.String(ref d, id, hint);
}
