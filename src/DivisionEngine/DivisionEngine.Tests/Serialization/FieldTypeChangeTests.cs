using System.Buffers;
using VYaml.Emitter;
using VYaml.Parser;

namespace DivisionEngine.Tests.Serialization;

// The same field id read back as a different type — what happens when an author changes
// `int Health` to `float Health` without touching the name.

[AutoSerialization]
public sealed partial class IntHolder : SerializableObject
{
    [Serialize(10)] public int Value;
    [Serialize(20)] public int After;
}

[AutoSerialization]
public sealed partial class FloatHolder : SerializableObject
{
    [Serialize(10)] public float Value;
    [Serialize(20)] public int After;
}

[AutoSerialization]
public sealed partial class StringHolder : SerializableObject
{
    [Serialize(10)] public string Value = "";
    [Serialize(20)] public int After;
}

/// <summary>
///     Changing a field's type keeps its id, so the reader still matches the key and then has to make
///     sense of a value written for another type. These tests pin what that actually does, because
///     the answer decides whether a type change is a safe edit or a corrupting one.
/// </summary>
[TestFixture]
public sealed class FieldTypeChangeTests
{
    private static byte[] Write(ISerializable value)
    {
        var writer = new ArrayBufferWriter<byte>();
        var emitter = new Utf8YamlEmitter(writer);
        var serializer = new YamlSerializer(emitter);
        serializer.BeginObject(new LocalId(0), value.GetType());
        value.Serialize(ref serializer);
        serializer.EndObject();
        return writer.WrittenSpan.ToArray();
    }

    private static T Read<T>(byte[] bytes) where T : ISerializable, new()
    {
        var parser = new YamlParser(new ReadOnlySequence<byte>(bytes));
        var deserializer = new YamlDeserializer(parser, null);
        deserializer.TryBeginObject(out _, out _);
        var result = new T();
        result.Deserialize(ref deserializer);
        return result;
    }

    [Test]
    public void IntWidenedToFloat_KeepsTheValue()
    {
        var loaded = Read<FloatHolder>(Write(new IntHolder { Value = 7, After = 3 }));

        Assert.Multiple(() =>
        {
            Assert.That(loaded.Value, Is.EqualTo(7f), "a YAML integer scalar reads as a float");
            Assert.That(loaded.After, Is.EqualTo(3), "the following field is unaffected");
        });
    }

    [Test]
    public void FloatNarrowedToInt_IsRejectedRatherThanTruncated()
    {
        var bytes = Write(new FloatHolder { Value = 2.5f, After = 3 });

        // Pinning the behavior, not endorsing it: what matters is that it is loud, not silent.
        Assert.That(() => Read<IntHolder>(bytes), Throws.Exception);
    }

    [Test]
    public void IntChangedToString_IsRejectedRatherThanMisread()
    {
        var bytes = Write(new IntHolder { Value = 7, After = 3 });

        Assert.That(() => Read<StringHolder>(bytes), Throws.Exception);
    }
}
