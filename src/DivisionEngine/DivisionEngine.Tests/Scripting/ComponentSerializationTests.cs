using System.Buffers;
using DivisionEngine.Authoring.Assets;
using VYaml.Emitter;
using VYaml.Parser;

namespace DivisionEngine.Tests.Scripting;

/// <summary>
///     Verifies the source generator produces working serialization for a user-style
///     <see cref="Component" />-derived type: it re-declares <c>ISerializable</c> and emits explicit
///     implementations, so the type's generated Serialize/Deserialize are used (not the throwing
///     default interface methods inherited via Component).
/// </summary>
[TestFixture]
public sealed class ComponentSerializationTests
{
    private sealed class NullResolver : ISerializedObjectResolver
    {
        public ISerializableObject? Resolve(GlobalId id)
        {
            return null;
        }
    }

    [Test]
    public void ComponentDerived_AutoSerialization_RoundTrips()
    {
        var source = new HealthComponent { Health = 7, Label = "hp", Enabled = true };

        var writer = new ArrayBufferWriter<byte>();
        var emitter = new Utf8YamlEmitter(writer);
        var serializer = new YamlSerializer(emitter);
        serializer.BeginObject(new LocalId(0), typeof(HealthComponent));
        ((ISerializable)source).Serialize(ref serializer);
        serializer.EndObject();

        var deserializer = new YamlDeserializer(
            new YamlParser(new ReadOnlySequence<byte>(writer.WrittenMemory)), new NullResolver());
        Assert.That(deserializer.TryBeginObject(out _, out var type), Is.True);
        Assert.That(type, Is.EqualTo(typeof(HealthComponent)));

        var result = (HealthComponent)Activator.CreateInstance(type)!;
        ((ISerializable)result).Deserialize(ref deserializer);

        Assert.Multiple(() =>
        {
            Assert.That(result.Health, Is.EqualTo(7));
            Assert.That(result.Label, Is.EqualTo("hp"));
        });
    }

    [Test]
    public void ComponentDerived_RoundTripsThroughAssetDatabase()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DivisionComponentTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "comp.asset");
            ScopeId guid;
            {
                using var db = new AssetDatabase(Path.Combine(dir, "cache"));
                var scope = db.CreateScope();
                db.AddObject(scope, new HealthComponent { Health = 99, Label = "boss" });
                db.SaveAsset(scope, path);
                guid = scope.Id;
            }

            using var load = new AssetDatabase(Path.Combine(dir, "cache"));
            load.Register(guid, path);
            var loaded = load.LoadAsset<HealthComponent>(guid);

            Assert.That(loaded, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(loaded!.Health, Is.EqualTo(99));
                Assert.That(loaded.Label, Is.EqualTo("boss"));
            });
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}

/// <summary>A user-style component: derives from the engine's Component and adds serialized fields.</summary>
[AutoSerialization]
public sealed partial class HealthComponent : Component
{
    [Serialize] public int Health;
    [Serialize] public string Label = "";
}