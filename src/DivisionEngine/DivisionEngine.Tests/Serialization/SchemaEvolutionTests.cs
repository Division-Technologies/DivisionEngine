using System.Buffers;
using VYaml.Emitter;
using VYaml.Parser;

namespace DivisionEngine.Tests.Serialization;

// Explicit field ids so the order the reader walks is fixed by the test rather than by the hash of
// a field name. Each type stands for one version of the same user type.

[AutoSerialization]
public partial struct FieldsAC
{
    [Serialize(10)] public int A;
    [Serialize(30)] public int C;
}

[AutoSerialization]
public partial struct FieldsABC
{
    [Serialize(10)] public int A;
    [Serialize(20)] public int B;
    [Serialize(30)] public int C;
}

[AutoSerialization]
public partial struct FieldsACD
{
    [Serialize(10)] public int A;
    [Serialize(30)] public int C;
    [Serialize(40)] public int D;
}

[AutoSerialization]
public sealed partial class HolderAC : SerializableObject
{
    [Serialize(1)] public FieldsAC V;
}

[AutoSerialization]
public sealed partial class HolderABC : SerializableObject
{
    [Serialize(1)] public FieldsABC V;
}

[AutoSerialization]
public sealed partial class HolderACD : SerializableObject
{
    [Serialize(1)] public FieldsACD V;
}

/// <summary>
///     Reading data written by a different version of a type. Field ids default to a hash of the
///     field name, so an author adding a field cannot tell where it will land in the id order — which
///     means every position has to behave, not just the end.
/// </summary>
[TestFixture]
public sealed class SchemaEvolutionTests
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
    public void SameVersion_RoundTrips()
    {
        var loaded = Read<HolderAC>(Write(new HolderAC { V = new FieldsAC { A = 1, C = 3 } }));

        Assert.Multiple(() =>
        {
            Assert.That(loaded.V.A, Is.EqualTo(1));
            Assert.That(loaded.V.C, Is.EqualTo(3));
        });
    }

    [Test]
    public void AFieldAddedAfterTheExistingOnes_ReadsAsDefault()
    {
        var loaded = Read<HolderACD>(Write(new HolderAC { V = new FieldsAC { A = 1, C = 3 } }));

        Assert.Multiple(() =>
        {
            Assert.That(loaded.V.A, Is.EqualTo(1));
            Assert.That(loaded.V.C, Is.EqualTo(3));
            Assert.That(loaded.V.D, Is.EqualTo(0), "the new field has nothing to read");
        });
    }

    [Test]
    public void AFieldAddedBetweenExistingOnes_DoesNotDisturbTheFieldsAfterIt()
    {
        var loaded = Read<HolderABC>(Write(new HolderAC { V = new FieldsAC { A = 1, C = 3 } }));

        Assert.Multiple(() =>
        {
            Assert.That(loaded.V.A, Is.EqualTo(1));
            Assert.That(loaded.V.B, Is.EqualTo(0), "the new field has nothing to read");
            Assert.That(loaded.V.C, Is.EqualTo(3), "the field after the insertion point still loads");
        });
    }

    [Test]
    public void AFieldRemovedFromBetweenExistingOnes_DoesNotDisturbTheFieldsAfterIt()
    {
        var loaded = Read<HolderAC>(Write(new HolderABC { V = new FieldsABC { A = 1, B = 2, C = 3 } }));

        Assert.Multiple(() =>
        {
            Assert.That(loaded.V.A, Is.EqualTo(1));
            Assert.That(loaded.V.C, Is.EqualTo(3), "the field after the dropped one still loads");
        });
    }

    [Test]
    public void EveryFieldMissing_ReadsAsDefaults()
    {
        var loaded = Read<HolderACD>(Write(new HolderAC()));

        Assert.Multiple(() =>
        {
            Assert.That(loaded.V.A, Is.EqualTo(0));
            Assert.That(loaded.V.C, Is.EqualTo(0));
            Assert.That(loaded.V.D, Is.EqualTo(0));
        });
    }

    [Test]
    public void AHeldBackKey_DoesNotLeakOutOfItsStruct()
    {
        // The reader gives up on FieldsABC.B mid-struct, holding key 30 back. That key must not be
        // mistaken for a field of the enclosing holder once the struct closes.
        var written = Write(new HolderABC { V = new FieldsABC { A = 1, B = 2, C = 3 } });
        var loaded = Read<HolderAC>(written);

        Assert.That(loaded.V.C, Is.EqualTo(3));
        Assert.That(loaded.Id, Is.EqualTo(default(LocalId)), "the holder itself is unaffected");
    }
}