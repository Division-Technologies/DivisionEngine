using System.Buffers;
using System.Text;
using VYaml.Emitter;
using VYaml.Parser;

namespace DivisionEngine.Tests.Serialization;

[AutoSerialization]
[TypeId("6f9619ff-8b86-d011-b42d-00cf4fc964ff")]
internal sealed partial class RenamedWidget
{
    [Serialize] public int Value;
}

[AutoSerialization]
internal sealed partial class PlainWidget
{
    [Serialize] public int Value;
}

[TestFixture]
public sealed class TypeIdTests
{
    private const string RenamedWidgetId = "6f9619ff8b86d011b42d00cf4fc964ff";

    private static (string Yaml, byte[] Data) Serialize<T>(T source) where T : ISerializable
    {
        var writer = new ArrayBufferWriter<byte>();
        var serializer = new YamlSerializer(new Utf8YamlEmitter(writer));
        serializer.BeginObject(new LocalId(0), typeof(T));
        source.Serialize(ref serializer);
        serializer.EndObject();

        var data = writer.WrittenSpan.ToArray();
        return (Encoding.UTF8.GetString(data), data);
    }

    private static Type? ReadFramedType(byte[] data)
    {
        var deserializer = new YamlDeserializer(new YamlParser(new ReadOnlySequence<byte>(data)), null);
        return deserializer.TryBeginObject(out _, out var type) ? type : null;
    }

    [Test]
    public void ExplicitId_IsWrittenInsteadOfTypeName()
    {
        var (yaml, _) = Serialize(new RenamedWidget { Value = 1 });

        Assert.That(yaml, Does.Contain(RenamedWidgetId));
        Assert.That(yaml, Does.Not.Contain(nameof(RenamedWidget)));
    }

    [Test]
    public void ExplicitId_ResolvesBackToType()
    {
        var (_, data) = Serialize(new RenamedWidget { Value = 42 });

        Assert.That(ReadFramedType(data), Is.EqualTo(typeof(RenamedWidget)));
    }

    [Test]
    public void ExplicitId_ResolvesFromDocumentWrittenBeforeRename()
    {
        // A document persisted before a rename references the pinned GUID, not the class name.
        // "D" format also parses — the ID is normalized before lookup.
        var data = Encoding.UTF8.GetBytes("0: 0\n1: 6f9619ff-8b86-d011-b42d-00cf4fc964ff\n2: {}\n");

        Assert.That(ReadFramedType(data), Is.EqualTo(typeof(RenamedWidget)));
    }

    [Test]
    public void DefaultId_IsHashOfFullName_AndRoundTrips()
    {
        var (yaml, data) = Serialize(new PlainWidget { Value = 1 });

        // The registry entry is computed at compile time by the generator; SerializedTypeId.Get
        // computes at runtime. Resolving back proves the two hash computations agree.
        Assert.That(yaml, Does.Contain(SerializedTypeId.Get(typeof(PlainWidget))));
        Assert.That(yaml, Does.Not.Contain(nameof(PlainWidget)));
        Assert.That(ReadFramedType(data), Is.EqualTo(typeof(PlainWidget)));
    }

    [Test]
    public void LegacyDocument_WithFullName_StillResolves()
    {
        var data = Encoding.UTF8.GetBytes($"0: 0\n1: {typeof(PlainWidget).FullName}\n2: {{}}\n");

        Assert.That(ReadFramedType(data), Is.EqualTo(typeof(PlainWidget)));
    }
}