using System.Buffers;
using VYaml.Emitter;
using VYaml.Parser;

namespace DivisionEngine.Tests.Scenes;

/// <summary>A component nothing in the test assembly mentions, to prove the attribute alone registers it.</summary>
[Component]
[AutoSerialization]
[TypeId("b41d9f70-2c85-4f6e-9b22-7a0e5d31c900")]
public partial struct UntouchedComponent
{
    [Serialize] public int Value;
    [Serialize] public Entity Target;
}

/// <summary>An entity field one struct deep, which the generated remapper has to reach into.</summary>
[Component]
[AutoSerialization]
[TypeId("b41d9f70-2c85-4f6e-9b22-7a0e5d31c901")]
public partial struct NestedReferenceComponent
{
    [Serialize] public LinkData Link;

    [AutoSerialization]
    public partial struct LinkData
    {
        [Serialize] public Entity Target;
        [Serialize] public int Weight;
    }
}

/// <summary>A component with no fields: a tag, which has no storage and so no value serializer.</summary>
[Component]
[TypeId("b41d9f70-2c85-4f6e-9b22-7a0e5d31c902")]
public struct RegisteredTag;

/// <summary>
///     <c>[Component]</c> makes the source generator register a type with
///     <see cref="ComponentTypeRegistry" /> when its assembly loads, which is what lets a saved scene
///     name component types by an id that outlives the run that wrote it.
/// </summary>
[TestFixture]
public sealed class ComponentRegistrationTests
{
    private static ComponentTypeInfo InfoFor(Type type)
    {
        var serializedId = SerializedTypeId.Get(type);
        Assert.That(ComponentTypeRegistry.TryResolveBySerializedTypeId(serializedId, out var id), Is.True,
            $"{type.Name} should be registered by its module initializer");
        return ComponentTypeRegistry.GetInfo(id);
    }

    [Test]
    public void AComponentTypeNoCodeMentions_IsStillResolvableByItsPersistedId()
    {
        var info = InfoFor(typeof(UntouchedComponent));

        Assert.Multiple(() =>
        {
            Assert.That(info.Type, Is.EqualTo(typeof(UntouchedComponent)));
            Assert.That(info.SerializedTypeId, Is.EqualTo("b41d9f702c854f6e9b227a0e5d31c900"), "the pinned [TypeId]");
            Assert.That(info.ValueSerializer, Is.Not.Null, "a serializable component gets a value serializer");
            Assert.That(info.HasEntityFields, Is.True, "its Entity field was declared for remapping");
        });
    }

    [Test]
    public void TagComponents_GetNoValueSerializer()
    {
        var info = InfoFor(typeof(RegisteredTag));

        Assert.Multiple(() =>
        {
            Assert.That(info.IsTag, Is.True);
            Assert.That(info.ValueSerializer, Is.Null, "there is no storage to serialize");
            Assert.That(info.HasEntityFields, Is.False);
        });
    }

    [Test]
    public void EntityFieldsNestedInsideAStruct_AreRemappedToo()
    {
        InfoFor(typeof(NestedReferenceComponent));

        using var world = new World();
        var buffer = new EntityCommandBuffer();
        var target = buffer.CreateEntity();
        var holder = buffer.CreateEntity();
        buffer.AddComponent(holder, new NestedReferenceComponent
        {
            Link = new NestedReferenceComponent.LinkData { Target = target, Weight = 7 }
        });

        buffer.Playback(world);

        var entity = FindSingle<NestedReferenceComponent>(world);
        var value = world.GetComponentReadOnly<NestedReferenceComponent>(entity);
        Assert.Multiple(() =>
        {
            Assert.That(value.Link.Target.IsDeferred, Is.False, "the nested placeholder was resolved");
            Assert.That(world.IsAlive(value.Link.Target), Is.True);
            Assert.That(value.Link.Weight, Is.EqualTo(7));
        });
    }

    [Test]
    public void AUserComponentsEntityField_SurvivesASceneRoundTrip()
    {
        using var source = new World();
        var target = source.CreateEntity();
        var holder = source.CreateEntity(ComponentType<UntouchedComponent>.Id);
        source.SetComponent(holder, new UntouchedComponent { Value = 5, Target = target });

        var scene = RoundTrip(EntityScene.CaptureFrom(source));

        using var loaded = new World();
        var created = scene.ApplyTo(loaded);
        var value = loaded.GetComponentReadOnly<UntouchedComponent>(created[1]);

        Assert.Multiple(() =>
        {
            Assert.That(value.Value, Is.EqualTo(5));
            Assert.That(value.Target, Is.EqualTo(created[0]), "the reference points at the new world's entity");
        });
    }

    private static Entity FindSingle<T>(World world)
    {
        foreach (var chunk in world.Query().With<T>().Build())
        {
            foreach (var entity in chunk.Entities)
            {
                return entity;
            }
        }

        Assert.Fail($"no entity with {typeof(T).Name}");
        return Entity.Null;
    }

    private static EntityScene RoundTrip(EntityScene scene)
    {
        var writer = new ArrayBufferWriter<byte>();
        var emitter = new Utf8YamlEmitter(writer);
        var serializer = new YamlSerializer(emitter);
        serializer.BeginObject(new LocalId(0), typeof(EntityScene));
        ((ISerializable)scene).Serialize(ref serializer);
        serializer.EndObject();

        var parser = new YamlParser(new ReadOnlySequence<byte>(writer.WrittenMemory));
        var deserializer = new YamlDeserializer(parser, new NullResolver());
        deserializer.TryBeginObject(out _, out _);
        var loaded = new EntityScene();
        ((ISerializable)loaded).Deserialize(ref deserializer);
        return loaded;
    }

    private sealed class NullResolver : ISerializedObjectResolver
    {
        public ISerializableObject? Resolve(GlobalId id)
        {
            return null;
        }
    }
}
