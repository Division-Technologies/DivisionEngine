using System.Buffers;
using System.Numerics;
using VYaml.Emitter;

namespace DivisionEngine.Tests.Scenes;

/// <summary>
///     A world's entities survive a trip through the serialization layer: component values keep their
///     fields, and references between entities are rebuilt against the entities of the new world
///     rather than the handles of the old one.
/// </summary>
[TestFixture]
public sealed class EntitySceneTests
{
    private World _world = null!;

    [SetUp]
    public void SetUp()
    {
        _world = new World();
    }

    [TearDown]
    public void TearDown()
    {
        _world.Dispose();
    }

    /// <summary>Writes the scene as an object document and reads it back, as an asset file would.</summary>
    private static EntityScene RoundTrip(EntityScene scene)
    {
        return EntityScene.FromBytes(scene.ToBytes());
    }

    [Test]
    public void ComponentValues_SurviveTheRoundTrip()
    {
        _world.CreateTransform(LocalTransform.FromPosition(new Vector3(1, 2, 3)));

        var loaded = RoundTrip(EntityScene.CaptureFrom(_world));
        using var target = new World();
        var created = loaded.ApplyTo(target);

        Assert.That(created, Has.Length.EqualTo(1));
        var local = target.GetComponentReadOnly<LocalTransform>(created[0]);
        Assert.Multiple(() =>
        {
            Assert.That(local.Position, Is.EqualTo(new Vector3(1, 2, 3)));
            Assert.That(local.Scale, Is.EqualTo(Vector3.One), "identity scale is not the struct default");
            Assert.That(target.HasComponent<WorldTransform>(created[0]), Is.True);
        });
    }

    [Test]
    public void Hierarchy_IsRebuiltAgainstTheNewWorldsEntities()
    {
        var root = _world.CreateTransform(LocalTransform.FromPosition(new Vector3(10, 0, 0)));
        var a = _world.CreateTransform(LocalTransform.FromPosition(new Vector3(0, 1, 0)));
        var b = _world.CreateTransform(LocalTransform.FromPosition(new Vector3(0, 2, 0)));
        _world.SetParent(a, root);
        _world.SetParent(b, root);

        var loaded = RoundTrip(EntityScene.CaptureFrom(_world));
        using var target = new World();
        var created = loaded.ApplyTo(target);

        var newRoot = created[0];
        var children = new List<Entity>();
        foreach (var child in target.GetChildren(newRoot))
        {
            children.Add(child);
        }

        Assert.Multiple(() =>
        {
            Assert.That(children, Is.EqualTo(new[] { created[1], created[2] }), "order and identity are preserved");
            Assert.That(target.GetParent(created[1]), Is.EqualTo(newRoot));
            Assert.That(target.GetParent(created[2]), Is.EqualTo(newRoot));
            Assert.That(target.GetParent(newRoot), Is.EqualTo(Entity.Null));
        });
    }

    [Test]
    public void ReferencesLeavingTheCapturedSet_BecomeNull()
    {
        var root = _world.CreateTransform();
        var child = _world.CreateTransform();
        _world.SetParent(child, root);

        // Capture the child alone: its Parent points outside the set.
        var loaded = RoundTrip(EntityScene.CaptureFrom(_world, [child]));
        using var target = new World();
        var created = loaded.ApplyTo(target);

        Assert.That(target.GetParent(created[0]), Is.EqualTo(Entity.Null));
    }

    [Test]
    public void ACapturedScene_CanBeAppliedMoreThanOnce()
    {
        var root = _world.CreateTransform();
        var child = _world.CreateTransform();
        _world.SetParent(child, root);
        var scene = EntityScene.CaptureFrom(_world);

        using var target = new World();
        var first = scene.ApplyTo(target);
        var second = scene.ApplyTo(target);

        Assert.Multiple(() =>
        {
            Assert.That(target.GetParent(first[1]), Is.EqualTo(first[0]));
            Assert.That(target.GetParent(second[1]), Is.EqualTo(second[0]), "the second copy links to its own root");
            Assert.That(second[0], Is.Not.EqualTo(first[0]));
            Assert.That(target.EntityCount, Is.EqualTo(4));
        });
    }

    [Test]
    public void ACapturedScene_HoldsNoLiveHandles()
    {
        var root = _world.CreateTransform();
        var child = _world.CreateTransform();
        _world.SetParent(child, root);
        var scene = EntityScene.CaptureFrom(_world);

        // Destroying the source world must not affect what the scene can produce.
        _world.DestroyEntity(root);

        using var target = new World();
        var created = scene.ApplyTo(target);
        Assert.That(target.GetParent(created[1]), Is.EqualTo(created[0]));
    }

    [Test]
    public void TagComponents_ArePreserved()
    {
        var entity = _world.CreateEntity(ComponentType<LocalTransform>.Id, ComponentType<SceneTag>.Id);
        _world.SetComponent(entity, LocalTransform.Identity);

        var loaded = RoundTrip(EntityScene.CaptureFrom(_world));
        using var target = new World();
        var created = loaded.ApplyTo(target);

        Assert.That(target.HasComponent<SceneTag>(created[0]), Is.True);
    }

    [Test]
    public void ManagedComponents_AreRejectedWithAClearError()
    {
        var entity = _world.CreateEntity();
        _world.AddManagedComponent(entity, new SceneLabel { Value = "x" });

        Assert.That(() => EntityScene.CaptureFrom(_world),
            Throws.TypeOf<EntitySceneException>().With.Message.Contains("managed"));
    }

    [Test]
    public void SerializingAnEntityOutsideAScene_IsRejected()
    {
        Assert.That(EntitySerializationContext.Current, Is.Null);
        Assert.That(SerializeAParentWithNoContext, Throws.TypeOf<EntitySerializationException>());
    }

    /// <summary>A separate method because the serializer is a ref struct and cannot enter a lambda.</summary>
    private static void SerializeAParentWithNoContext()
    {
        var writer = new ArrayBufferWriter<byte>();
        var emitter = new Utf8YamlEmitter(writer);
        var serializer = new YamlSerializer(emitter);
        serializer.BeginObject(new LocalId(0), typeof(Parent));
        FormatterStore<Parent>.Formatter.Serialize(ref serializer, 0, "p"u8, new Parent { Value = new Entity(3, 1) });
    }

    [Component]
    private struct SceneTag;

    private sealed class SceneLabel
    {
        public string Value = "";
    }
}
