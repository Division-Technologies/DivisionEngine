using System.Numerics;
using DivisionEngine.Authoring.Assets;

namespace DivisionEngine.Tests.Scenes;

/// <summary>
///     A scene as an asset on disk. One scope holds one <see cref="EntityScene" /> — the entities
///     live inside that object rather than as objects of their own, so a scene is a single document
///     and the per-object rescan in <c>FileScopeLoader</c> never has more than one object to find.
///     Field keys are still numeric ids rather than names, so the file is diffable but not pleasant
///     to read; emitting the hints is an open item in the serialization notes.
/// </summary>
[TestFixture]
public sealed class SceneAssetTests
{
    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "DivisionSceneAssets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private string _dir = "";

    private string CachePath => Path.Combine(_dir, "cache");

    private static World BuildWorld()
    {
        var world = new World();
        var root = world.CreateTransform(LocalTransform.FromPosition(new Vector3(4, 0, 0)));
        var child = world.CreateTransform(LocalTransform.FromPosition(new Vector3(0, 1, 0)));
        world.SetParent(child, root);
        world.AddManagedComponent(child, new Follower { Target = root, Distance = 1.5f });
        return world;
    }

    [Test]
    public void ASceneSavedAsAnAsset_LoadsBackIntoAWorld()
    {
        var path = Path.Combine(_dir, "level.scene");
        ScopeId guid;

        using (var world = BuildWorld())
        using (var db = new AssetDatabase(CachePath))
        {
            var scope = db.CreateScope();
            db.AddObject(scope, EntityScene.CaptureFrom(world));
            db.SaveAsset(scope, path);
            guid = scope.Id;
        }

        // A fresh database, as a later run of the editor would have.
        using var reader = new AssetDatabase(CachePath);
        reader.Register(guid, path);
        var scene = reader.LoadAsset<EntityScene>(guid);

        Assert.That(scene, Is.Not.Null);
        Assert.That(scene!.EntityCount, Is.EqualTo(2));

        using var loaded = new World();
        var created = scene.ApplyTo(loaded);

        Assert.Multiple(() =>
        {
            Assert.That(loaded.GetComponentReadOnly<LocalTransform>(created[0]).Position,
                Is.EqualTo(new Vector3(4, 0, 0)));
            Assert.That(loaded.GetParent(created[1]), Is.EqualTo(created[0]), "the hierarchy came back");
            Assert.That(loaded.GetManagedComponent<Follower>(created[1]).Target, Is.EqualTo(created[0]),
                "the managed component's reference points inside the loaded scene");
            Assert.That(loaded.GetManagedComponent<Follower>(created[1]).Distance, Is.EqualTo(1.5f));
        });
    }

    [Test]
    public void TheSceneFile_StoresValuesInline_NotAsEncodedBlobs()
    {
        var path = Path.Combine(_dir, "level.scene");
        using (var world = BuildWorld())
        using (var db = new AssetDatabase(CachePath))
        {
            var scope = db.CreateScope();
            db.AddObject(scope, EntityScene.CaptureFrom(world));
            db.SaveAsset(scope, path);
        }

        var text = File.ReadAllText(path);
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("2d5b8e0714af4c93a6d29f01b3e6c800"), "the scene's pinned type id");
            Assert.That(text, Does.Contain("6f1a2c485f6b4a0e9a310f3d5c7e1b01"), "a component's pinned type id");
            Assert.That(text, Does.Contain("1.5"), "the managed component's field sits inline, not base64-encoded");
        });
    }

    [Test]
    public void OneSceneAsset_CanBeInstantiatedMoreThanOnce()
    {
        var path = Path.Combine(_dir, "prefab.scene");
        ScopeId guid;
        using (var world = BuildWorld())
        using (var db = new AssetDatabase(CachePath))
        {
            var scope = db.CreateScope();
            db.AddObject(scope, EntityScene.CaptureFrom(world));
            db.SaveAsset(scope, path);
            guid = scope.Id;
        }

        using var reader = new AssetDatabase(CachePath);
        reader.Register(guid, path);
        var scene = reader.LoadAsset<EntityScene>(guid)!;

        using var loaded = new World();
        var first = scene.ApplyTo(loaded);
        var second = scene.ApplyTo(loaded);

        Assert.Multiple(() =>
        {
            Assert.That(loaded.EntityCount, Is.EqualTo(4));
            Assert.That(loaded.GetParent(first[1]), Is.EqualTo(first[0]));
            Assert.That(loaded.GetParent(second[1]), Is.EqualTo(second[0]),
                "each instance is wired to its own copy — a scene asset is already a prefab");
        });
    }
}