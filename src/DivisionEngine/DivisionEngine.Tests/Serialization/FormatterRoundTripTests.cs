using System.Buffers;
using System.Numerics;
using VYaml.Emitter;
using VYaml.Parser;

namespace DivisionEngine.Tests.Serialization;

[TestFixture]
public sealed class FormatterRoundTripTests
{
    private sealed class NullResolver : ISerializedObjectResolver
    {
        public ISerializableObject? Resolve(GlobalId id)
        {
            return null;
        }
    }

    private static T RoundTrip<T>(T source) where T : ISerializable, new()
    {
        var writer = new ArrayBufferWriter<byte>();
        var emitter = new Utf8YamlEmitter(writer);
        var serializer = new YamlSerializer(emitter);
        serializer.BeginObject(new LocalId(0), typeof(T));
        source.Serialize(ref serializer);
        serializer.EndObject();

        var buffer = new ReadOnlySequence<byte>(writer.WrittenMemory);
        var deserializer = new YamlDeserializer(new YamlParser(buffer), new NullResolver());
        Assert.That(deserializer.TryBeginObject(out _, out _), Is.True, "TryBeginObject failed");

        var result = new T();
        result.Deserialize(ref deserializer);
        return result;
    }

    [Test]
    public void Primitives_RoundTrip()
    {
        var result = RoundTrip(new RoundTripContainer
        {
            BoolValue = true,
            ByteValue = 200,
            CharValue = 'あ',
            IntValue = -123456,
            ULongValue = ulong.MaxValue,
            FloatValue = 1.5f,
            DoubleValue = -2.25,
            StringValue = "hello 世界"
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.BoolValue, Is.True);
            Assert.That(result.ByteValue, Is.EqualTo(200));
            Assert.That(result.CharValue, Is.EqualTo('あ'));
            Assert.That(result.IntValue, Is.EqualTo(-123456));
            Assert.That(result.ULongValue, Is.EqualTo(ulong.MaxValue));
            Assert.That(result.FloatValue, Is.EqualTo(1.5f));
            Assert.That(result.DoubleValue, Is.EqualTo(-2.25));
            Assert.That(result.StringValue, Is.EqualTo("hello 世界"));
        });
    }

    [Test]
    public void Enum_RoundTrip()
    {
        var result = RoundTrip(new RoundTripContainer { EnumValue = TestEnum.C });
        Assert.That(result.EnumValue, Is.EqualTo(TestEnum.C));
    }

    [Test]
    public void BclScalars_RoundTrip()
    {
        var guid = Guid.NewGuid();
        var now = new DateTime(2026, 6, 7, 12, 34, 56, DateTimeKind.Utc);
        var result = RoundTrip(new RoundTripContainer
        {
            GuidValue = guid,
            DateTimeValue = now,
            TimeSpanValue = TimeSpan.FromMinutes(90),
            DecimalValue = 1234.5678m
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.GuidValue, Is.EqualTo(guid));
            Assert.That(result.DateTimeValue, Is.EqualTo(now));
            Assert.That(result.DateTimeValue.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(result.TimeSpanValue, Is.EqualTo(TimeSpan.FromMinutes(90)));
            Assert.That(result.DecimalValue, Is.EqualTo(1234.5678m));
        });
    }

    [Test]
    public void Numerics_RoundTrip()
    {
        var result = RoundTrip(new RoundTripContainer
        {
            Vector3Value = new Vector3(1, -2, 3.5f),
            QuaternionValue = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f)
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Vector3Value, Is.EqualTo(new Vector3(1, -2, 3.5f)));
            Assert.That(result.QuaternionValue, Is.EqualTo(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f)));
        });
    }

    [Test]
    public void Collections_RoundTrip()
    {
        var result = RoundTrip(new RoundTripContainer
        {
            Bytes = [1, 2, 3, 255],
            IntArray = [10, -20, 30],
            StringList = ["a", "b", "c"],
            EnumList = [TestEnum.A, TestEnum.C],
            Map = new Dictionary<string, int> { ["one"] = 1, ["two"] = 2 },
            Set = [5, 7, 11]
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Bytes, Is.EqualTo(new byte[] { 1, 2, 3, 255 }));
            Assert.That(result.IntArray, Is.EqualTo(new[] { 10, -20, 30 }));
            Assert.That(result.StringList, Is.EqualTo(new[] { "a", "b", "c" }));
            Assert.That(result.EnumList, Is.EqualTo(new[] { TestEnum.A, TestEnum.C }));
            Assert.That(result.Map, Is.EquivalentTo(new Dictionary<string, int> { ["one"] = 1, ["two"] = 2 }));
            Assert.That(result.Set, Is.EquivalentTo(new[] { 5, 7, 11 }));
        });
    }

    [Test]
    public void NullCollections_RoundTripAsNull()
    {
        var result = RoundTrip(new RoundTripContainer
        {
            IntArray = null,
            StringList = null,
            Map = null,
            Set = null
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.IntArray, Is.Null);
            Assert.That(result.StringList, Is.Null);
            Assert.That(result.Map, Is.Null);
            Assert.That(result.Set, Is.Null);
        });
    }

    [Test]
    public void NullableInt_RoundTrip()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RoundTrip(new RoundTripContainer { NullableInt = 42 }).NullableInt, Is.EqualTo(42));
            Assert.That(RoundTrip(new RoundTripContainer { NullableInt = null }).NullableInt, Is.Null);
        });
    }

    [Test]
    public void AutoSerializationStruct_RoundTrip()
    {
        var result = RoundTrip(new RoundTripContainer
        {
            Point = new TestPoint { X = 3, Y = -4 },
            PointList = [new TestPoint { X = 1, Y = 2 }, new TestPoint { X = 5, Y = 6 }]
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Point.X, Is.EqualTo(3));
            Assert.That(result.Point.Y, Is.EqualTo(-4));
            Assert.That(result.PointList, Has.Count.EqualTo(2));
            Assert.That(result.PointList![0].X, Is.EqualTo(1));
            Assert.That(result.PointList[1].Y, Is.EqualTo(6));
        });
    }

    [Test]
    public void CustomFormatter_ExternalType_RoundTrip()
    {
        var result = RoundTrip(new VersionContainer { Ver = new Version(1, 2, 3) });
        Assert.That(result.Ver, Is.EqualTo(new Version(1, 2, 3)));
    }
}