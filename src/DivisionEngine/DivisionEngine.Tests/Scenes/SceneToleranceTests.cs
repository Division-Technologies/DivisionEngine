using System.Numerics;
using System.Text;

namespace DivisionEngine.Tests.Scenes;

/// <summary>An asset-like object for managed components to point at.</summary>
[AutoSerialization]
[TypeId("c5a0e8f4-2b17-4d63-8e9a-7f1b3c2d4e10")]
public sealed partial class Palette : SerializableObject
{
    [Serialize] public int Hue;
}

/// <summary>A managed component holding an asset reference.</summary>
[Component]
[AutoSerialization]
[TypeId("c5a0e8f4-2b17-4d63-8e9a-7f1b3c2d4e11")]
public sealed partial class Painted : SerializableObject
{
    [Serialize] public int Layer;
    [Serialize] public Palette? Palette;
}

/// <summary>
///     What a scene does with what it cannot load, and with references to assets: the parts of a
///     reload that used to lose data or stop the reload altogether.
/// </summary>
[TestFixture]
public sealed class SceneToleranceTests
{
    private const string FakeTypeId = "0badc0de0badc0de0badc0de0badc0de";

    private static string TicksTypeId => ComponentType<Ticks>.Info.SerializedTypeId;

    private static EntityScene Rewrite(EntityScene scene, string from, string to)
    {
        var text = Encoding.UTF8.GetString(scene.ToBytes());
        Assert.That(text, Does.Contain(from), "the text being rewritten must be in the scene");
        return EntityScene.FromBytes(Encoding.UTF8.GetBytes(text.Replace(from, to)));
    }

    [Test]
    public void AComponentWhoseTypeIsGone_IsKeptAside_AndComesBackWithItsType()
    {
        using var source = new World();
        var entity = source.CreateTransform(LocalTransform.FromPosition(new Vector3(1, 2, 3)));
        source.AddComponent(entity, new Ticks { Count = 5 });

        // As if the script defining Ticks had been deleted.
        var orphaned = Rewrite(EntityScene.CaptureFrom(source), TicksTypeId, FakeTypeId);
        using var middle = new World();
        var warnings = new List<string>();
        var loaded = orphaned.ApplyTo(middle, null, warnings)[0];

        Assert.Multiple(() =>
        {
            Assert.That(middle.GetComponentReadOnly<LocalTransform>(loaded).Position.X, Is.EqualTo(1f),
                "the rest of the entity loads");
            Assert.That(middle.HasComponent<Ticks>(loaded), Is.False);
            Assert.That(middle.GetManagedComponent<MissingComponents>(loaded).Entries.Single().TypeId,
                Is.EqualTo(FakeTypeId));
            Assert.That(warnings, Has.Count.EqualTo(1));
        });

        // Saved again while missing, then loaded once the type exists again: nothing was lost.
        var restored = Rewrite(EntityScene.CaptureFrom(middle), FakeTypeId, TicksTypeId);
        using var target = new World();
        var back = restored.ApplyTo(target)[0];

        Assert.Multiple(() =>
        {
            Assert.That(target.GetComponentReadOnly<Ticks>(back).Count, Is.EqualTo(5));
            Assert.That(target.HasComponent<MissingComponents>(back), Is.False);
        });
    }

    [Test]
    public void AValueThatNoLongerReads_IsKeptAside_WithoutStoppingTheLoad()
    {
        using var source = new World();
        var entity = source.CreateTransform();
        source.AddComponent(entity, new Ticks { Count = 424242 });

        // As if Count had changed from int to string.
        var scene = Rewrite(EntityScene.CaptureFrom(source), "424242", "\"not a number\"");
        using var target = new World();
        var warnings = new List<string>();
        var loaded = scene.ApplyTo(target, null, warnings)[0];

        Assert.Multiple(() =>
        {
            Assert.That(target.HasComponent<LocalTransform>(loaded), Is.True);
            Assert.That(target.HasComponent<Ticks>(loaded), Is.False, "not half-read");
            Assert.That(target.GetManagedComponent<MissingComponents>(loaded).Entries.Single().Value,
                Is.Not.Null, "the value is kept as written");
            Assert.That(warnings.Single(), Does.Contain(TicksTypeId));
        });
    }

    [Test]
    public void AssetReferencesInManagedComponents_SurviveCaptureAndApply()
    {
        var scope = new SerializationScope(new ScopeId(Guid.NewGuid()), new NoLoader());
        var palette = new Palette { Hue = 3 };
        scope.Add(palette);

        using var source = new World();
        var entity = source.CreateEntity();
        source.AddManagedComponent(entity, new Painted { Palette = palette, Layer = 2 });

        var scene = EntityScene.FromBytes(EntityScene.CaptureFrom(source).ToBytes());
        using var target = new World();
        var loaded = scene.ApplyTo(target, new ScopeResolver(scope))[0];

        var painted = target.GetManagedComponent<Painted>(loaded);
        Assert.Multiple(() =>
        {
            Assert.That(painted.Palette, Is.SameAs(palette));
            Assert.That(painted.Layer, Is.EqualTo(2));
        });
    }

    [Test]
    public void AReferenceToAnObjectWithNoScope_IsSavedAsNull_WithAWarning()
    {
        using var source = new World();
        var entity = source.CreateEntity();
        source.AddManagedComponent(entity, new Painted { Palette = new Palette(), Layer = 2 });

        var scene = EntityScene.CaptureFrom(source);
        using var target = new World();
        var loaded = scene.ApplyTo(target)[0];

        Assert.Multiple(() =>
        {
            Assert.That(scene.Warnings, Has.One.Contains(nameof(Palette)));
            Assert.That(target.GetManagedComponent<Painted>(loaded).Palette, Is.Null);
            Assert.That(target.GetManagedComponent<Painted>(loaded).Layer, Is.EqualTo(2),
                "the rest of the component is kept");
        });
    }

    [Test]
    public void AfterSwap_RebuildsEveryEntityUnderItsOldHandle()
    {
        using var world = new World();
        var first = world.CreateEntity(ComponentType<Ticks>.Id);
        var gone = world.CreateEntity();
        var second = world.CreateEntity(ComponentType<Ticks>.Id);
        world.DestroyEntity(gone);
        world.AddManagedComponent(first, new Follower { Target = second });
        world.SetComponent(second, new Ticks { Count = 2 });

        var snapshot = WorldReload.BeforeSwap(world);
        Assert.That(world.IsAlive(first), Is.False, "the world is empty in between");
        WorldReload.AfterSwap(world, snapshot);

        Assert.Multiple(() =>
        {
            Assert.That(world.IsAlive(first) && world.IsAlive(second), Is.True, "the same handles are alive again");
            Assert.That(world.IsAlive(gone), Is.False);
            Assert.That(world.GetComponentReadOnly<Ticks>(second).Count, Is.EqualTo(2));
            Assert.That(world.GetManagedComponent<Follower>(first).Target, Is.EqualTo(second));
        });

        var fresh = world.CreateEntity();
        Assert.That(fresh, Is.Not.EqualTo(first).And.Not.EqualTo(second), "new entities do not reuse restored slots");
    }

    [Test]
    public void ApplyingAScene_ChecksIdsBeforeTouchingTheWorld()
    {
        var text = Encoding.UTF8.GetString(CaptureTwo().ToBytes());
        // Both records claiming id 0.
        var duplicated = EntityScene.FromBytes(Encoding.UTF8.GetBytes(text.Replace("\"0\": 1", "\"0\": 0")));

        using var target = new World();
        Assert.That(() => duplicated.ApplyTo(target), Throws.TypeOf<EntitySceneException>());
        Assert.That(target.EntityCount, Is.Zero);
    }

    private static EntityScene CaptureTwo()
    {
        using var world = new World();
        world.CreateEntity();
        world.CreateEntity();
        return EntityScene.CaptureFrom(world);
    }

    private sealed class NoLoader : ISerializationScopeLoader
    {
        public ISerializableObject? Load(LocalId id)
        {
            return null;
        }

        public void Deserialize(ISerializableObject obj, ISerializedObjectResolver resolver)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class ScopeResolver(SerializationScope scope) : ISerializedObjectResolver
    {
        public ISerializableObject? Resolve(GlobalId id)
        {
            return id.ScopeId == scope.Id ? scope.Objects.GetValueOrDefault(id.LocalId) : null;
        }
    }
}