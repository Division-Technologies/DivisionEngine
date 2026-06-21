using System.Numerics;

namespace DivisionEngine.Tests.Serialization;

public enum TestEnum : short
{
    A = 1,
    B = 2,
    C = 300
}

[AutoSerialization]
public partial struct TestPoint
{
    [Serialize] public int X;
    [Serialize] public int Y;
}

[AutoSerialization]
public partial class RoundTripContainer
{
    [Serialize] public bool BoolValue;
    [Serialize] public byte ByteValue;
    [Serialize] public char CharValue;
    [Serialize] public int IntValue;
    [Serialize] public ulong ULongValue;
    [Serialize] public float FloatValue;
    [Serialize] public double DoubleValue;
    [Serialize] public string StringValue = "";
    [Serialize] public TestEnum EnumValue;
    [Serialize] public Guid GuidValue;
    [Serialize] public DateTime DateTimeValue;
    [Serialize] public TimeSpan TimeSpanValue;
    [Serialize] public decimal DecimalValue;
    [Serialize] public Vector3 Vector3Value;
    [Serialize] public Quaternion QuaternionValue;
    [Serialize] public byte[] Bytes = [];
    [Serialize] public int[]? IntArray;
    [Serialize] public List<string>? StringList;
    [Serialize] public List<TestEnum>? EnumList;
    [Serialize] public Dictionary<string, int>? Map;
    [Serialize] public HashSet<int>? Set;
    [Serialize] public int? NullableInt;
    [Serialize] public TestPoint Point;
    [Serialize] public List<TestPoint>? PointList;
}

[CustomFormatter(typeof(Version))]
public sealed class VersionFormatter : IValueFormatter<Version>
{
    public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in Version value)
        where TS : ISerializer, allows ref struct
    {
        SerializerExtensions.Utf16(ref s, id, hint, (value ?? new Version(0, 0)).ToString());
    }

    public Version Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
        where TD : IDeserializer, allows ref struct
    {
        return Version.Parse(DeserializerExtensions.String(ref d, id, hint));
    }
}

[AutoSerialization]
public partial class VersionContainer
{
    [Serialize] public Version? Ver;
}
