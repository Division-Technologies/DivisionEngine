namespace DivisionEngine.Tests.Scenes;

/// <summary>A managed component that points at another entity — the case byte-level remapping cannot reach.</summary>
[Component]
[AutoSerialization]
[TypeId("8e3f0a21-64cd-4f79-9b48-31a7c05d2200")]
public sealed partial class Follower : SerializableObject
{
    [Serialize] public Entity Target;
    [Serialize] public float Distance;
}

/// <summary>A behavior holding an entity reference, which is a natural thing to write.</summary>
[Component]
[AutoSerialization]
[TypeId("8e3f0a21-64cd-4f79-9b48-31a7c05d2201")]
public sealed partial class Chaser : Behavior
{
    [Serialize] public Entity Quarry;

    protected override async BehaviorTask Run(BehaviorContext context)
    {
        while (true)
        {
            await context.Phase(PhaseId.Update);
        }
        // ReSharper disable once FunctionNeverReturns
    }
}

/// <summary>
///     Entity references held by managed components have to survive a scene the same way the ones in
///     unmanaged components do, even though nothing can rewrite them at the byte level.
/// </summary>
[TestFixture]
public sealed class ManagedEntityReferenceTests
{
    [Test]
    public void AManagedComponentsEntityField_PointsAtTheNewWorldsEntity()
    {
        using var source = new World();
        var target = source.CreateEntity();
        var follower = source.CreateEntity();
        source.AddManagedComponent(follower, new Follower { Target = target, Distance = 2.5f });

        var scene = EntityScene.FromBytes(EntityScene.CaptureFrom(source).ToBytes());

        using var loaded = new World();
        var created = scene.ApplyTo(loaded);
        var value = loaded.GetManagedComponent<Follower>(created[1]);

        Assert.Multiple(() =>
        {
            Assert.That(value.Target, Is.EqualTo(created[0]), "the reference follows into the new world");
            Assert.That(loaded.IsAlive(value.Target), Is.True);
            Assert.That(value.Distance, Is.EqualTo(2.5f), "the other fields are untouched");
        });
    }

    [Test]
    public void ABehaviorsEntityField_SurvivesToo()
    {
        using var source = new World();
        var quarry = source.CreateEntity();
        var hunter = source.CreateEntity();
        source.AddManagedComponent(hunter, new Chaser { Quarry = quarry });

        var scene = EntityScene.FromBytes(EntityScene.CaptureFrom(source).ToBytes());

        using var loaded = new World();
        var created = scene.ApplyTo(loaded);

        Assert.That(loaded.GetManagedComponent<Chaser>(created[1]).Quarry, Is.EqualTo(created[0]));
    }

    [Test]
    public void ACapturedSceneHoldsNoLiveHandles_EvenInManagedComponents()
    {
        using var source = new World();
        var target = source.CreateEntity();
        var follower = source.CreateEntity();
        source.AddManagedComponent(follower, new Follower { Target = target });
        var scene = EntityScene.CaptureFrom(source);

        // Destroying the source world must not change what the scene produces.
        source.DestroyEntity(target);

        using var loaded = new World();
        var created = scene.ApplyTo(loaded);
        Assert.That(loaded.GetManagedComponent<Follower>(created[1]).Target, Is.EqualTo(created[0]));
    }

    [Test]
    public void AReferenceLeavingTheCapturedSet_BecomesNull()
    {
        using var source = new World();
        var outside = source.CreateEntity();
        var follower = source.CreateEntity();
        source.AddManagedComponent(follower, new Follower { Target = outside });

        var scene = EntityScene.FromBytes(EntityScene.CaptureFrom(source, [follower]).ToBytes());

        using var loaded = new World();
        var created = scene.ApplyTo(loaded);
        Assert.That(loaded.GetManagedComponent<Follower>(created[0]).Target, Is.EqualTo(Entity.Null));
    }

    [Test]
    public void ApplyingTwice_LinksEachCopyToItsOwnEntities()
    {
        using var source = new World();
        var target = source.CreateEntity();
        var follower = source.CreateEntity();
        source.AddManagedComponent(follower, new Follower { Target = target });
        var scene = EntityScene.CaptureFrom(source);

        using var loaded = new World();
        var first = scene.ApplyTo(loaded);
        var second = scene.ApplyTo(loaded);

        Assert.Multiple(() =>
        {
            Assert.That(loaded.GetManagedComponent<Follower>(first[1]).Target, Is.EqualTo(first[0]));
            Assert.That(loaded.GetManagedComponent<Follower>(second[1]).Target, Is.EqualTo(second[0]),
                "the second copy points inside itself, not at the first");
        });
    }
}
