using DivisionEngine.Authoring.Assets;

namespace DivisionEngine.Tests.Assets;

[TestFixture]
public sealed class AssetDatabaseTests
{
    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "DivisionAssetTests", Guid.NewGuid().ToString("N"));
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

    [Test]
    public void SaveAndLoad_SingleScope_RoundTrips()
    {
        var path = Path.Combine(_dir, "node.asset");

        ScopeId guid;
        {
            var db = new AssetDatabase();
            var scope = db.CreateScope();
            var root = new AssetNode { Name = "root" };
            var child = new AssetNode { Name = "child" };
            db.AddObject(scope, root);
            db.AddObject(scope, child);
            root.Ref = child;
            db.SaveAsset(scope, path);
            guid = scope.Id;
        }

        var loadDb = new AssetDatabase();
        loadDb.Register(guid, path);
        var loaded = loadDb.LoadAsset<AssetNode>(guid);

        Assert.That(loaded, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(loaded!.Name, Is.EqualTo("root"));
            Assert.That(loaded.Ref, Is.Not.Null);
            Assert.That(loaded.Ref!.Name, Is.EqualTo("child"));
        });
    }

    [Test]
    public void LoadAsset_ByPath_RoundTrips()
    {
        var path = Path.Combine(_dir, "node.asset");

        var db = new AssetDatabase();
        var scope = db.CreateScope();
        db.AddObject(scope, new AssetNode { Name = "solo" });
        db.SaveAsset(scope, path);

        var loadDb = new AssetDatabase();
        loadDb.Register(scope.Id, path);
        var loaded = loadDb.LoadAsset<AssetNode>(path);

        Assert.That(loaded?.Name, Is.EqualTo("solo"));
    }

    [Test]
    public void LoadAsset_ResolvesCrossScopeReference()
    {
        var pathA = Path.Combine(_dir, "a.asset");
        var pathB = Path.Combine(_dir, "b.asset");

        ScopeId guidA, guidB;
        {
            var db = new AssetDatabase();
            var scopeA = db.CreateScope();
            var scopeB = db.CreateScope();
            var a = new AssetNode { Name = "a" };
            var b = new AssetNode { Name = "b" };
            db.AddObject(scopeB, b); // b lives in scope B
            db.AddObject(scopeA, a); // a lives in scope A
            a.Ref = b; // cross-scope reference
            db.SaveAsset(scopeA, pathA);
            db.SaveAsset(scopeB, pathB);
            guidA = scopeA.Id;
            guidB = scopeB.Id;
        }

        var loadDb = new AssetDatabase();
        loadDb.Register(guidA, pathA);
        loadDb.Register(guidB, pathB);
        var a2 = loadDb.LoadAsset<AssetNode>(guidA);

        Assert.That(a2, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(a2!.Ref, Is.Not.Null);
            Assert.That(a2.Ref!.Name, Is.EqualTo("b"));
            Assert.That(a2.Ref.Scope.Id, Is.EqualTo(guidB));
        });
    }

    [Test]
    public void ImportAsset_RunsRegisteredImporter()
    {
        var path = Path.Combine(_dir, "hello.dntxt");
        File.WriteAllText(path, "hello world");

        var db = new AssetDatabase();
        var node = db.ImportAsset<AssetNode>(path);

        Assert.That(node?.Name, Is.EqualTo("hello world"));

        // The imported asset is registered and loadable by GUID from the same database.
        var reloaded = db.LoadAsset<AssetNode>(db.GetGuid(path)!.Value);
        Assert.That(reloaded?.Name, Is.EqualTo("hello world"));
    }

    [Test]
    public void ImportAsset_UnknownExtension_ReturnsNull()
    {
        var path = Path.Combine(_dir, "data.unknownext");
        File.WriteAllText(path, "x");

        var db = new AssetDatabase();
        Assert.That(db.ImportAsset<AssetNode>(path), Is.Null);
    }

    [Test]
    public void ImportAsset_SecondImport_HitsCacheWithoutReimporting()
    {
        var path = Path.Combine(_dir, "cached.dntxt");
        File.WriteAllText(path, "data");
        var cacheDir = Path.Combine(_dir, "cache");

        TextNodeImporter.ImportCount = 0;

        var first = new AssetDatabase(cacheDir).ImportAsset<AssetNode>(path);
        Assert.That(first?.Name, Is.EqualTo("data"));
        Assert.That(TextNodeImporter.ImportCount, Is.EqualTo(1));

        // A fresh database over the same cache dir + .meta sidecar must not re-run the importer.
        var second = new AssetDatabase(cacheDir).ImportAsset<AssetNode>(path);
        Assert.That(second?.Name, Is.EqualTo("data"));
        Assert.That(TextNodeImporter.ImportCount, Is.EqualTo(1), "importer should not run on cache hit");
    }

    [Test]
    public void ImportAsset_SourceChanged_Reimports()
    {
        var path = Path.Combine(_dir, "changing.dntxt");
        File.WriteAllText(path, "before");
        var cacheDir = Path.Combine(_dir, "cache");

        TextNodeImporter.ImportCount = 0;

        var first = new AssetDatabase(cacheDir).ImportAsset<AssetNode>(path);
        Assert.That(first?.Name, Is.EqualTo("before"));

        File.WriteAllText(path, "after");
        var second = new AssetDatabase(cacheDir).ImportAsset<AssetNode>(path);

        Assert.Multiple(() =>
        {
            Assert.That(second?.Name, Is.EqualTo("after"));
            Assert.That(TextNodeImporter.ImportCount, Is.EqualTo(2), "changed source should re-run importer");
        });
    }

    [Test]
    public void Reimport_UpdatesExistingInstanceInPlace()
    {
        var path = Path.Combine(_dir, "live.dntxt");
        File.WriteAllText(path, "v1");

        var db = new AssetDatabase(Path.Combine(_dir, "cache"));
        var node = db.ImportAsset<AssetNode>(path);
        Assert.That(node?.Name, Is.EqualTo("v1"));

        File.WriteAllText(path, "v2");
        db.Reimport(path);

        // The same instance is updated, so any external reference to it sees the new value.
        Assert.That(node!.Name, Is.EqualTo("v2"));
    }

    [Test]
    public void Reimport_CascadesToDependents()
    {
        var db = new AssetDatabase(Path.Combine(_dir, "cache"));

        var depPath = Path.Combine(_dir, "dep.dntxt");
        File.WriteAllText(depPath, "dep-v1");
        db.ImportAsset<AssetNode>(depPath);
        var depGuid = db.GetGuid(depPath)!.Value;

        var userPath = Path.Combine(_dir, "user.dndep");
        File.WriteAllText(userPath, depGuid.ToString());
        DependentImporter.ImportCount = 0;
        db.ImportAsset<AssetNode>(userPath);
        Assert.That(DependentImporter.ImportCount, Is.EqualTo(1));

        // Changing the dependency re-imports the dependent via the reverse dependency edge.
        File.WriteAllText(depPath, "dep-v2");
        db.Reimport(depPath);
        Assert.That(DependentImporter.ImportCount, Is.EqualTo(2), "dependent should re-import");
    }

    [Test]
    public void Refresh_ImportsAllAssetsUnderRoots()
    {
        var assets = Path.Combine(_dir, "Assets");
        Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(assets, "a.dntxt"), "aa");
        File.WriteAllText(Path.Combine(assets, "b.dntxt"), "bb");

        using var db = new AssetDatabase(new[] { assets }, Path.Combine(_dir, "Library"));
        db.Refresh();

        Assert.That(db.AllAssets, Has.Count.EqualTo(2));
        Assert.That(db.LoadAsset<AssetNode>(Path.Combine(assets, "a.dntxt"))?.Name, Is.EqualTo("aa"));
    }

    [Test]
    public void Refresh_DropsDeletedAssets()
    {
        var assets = Path.Combine(_dir, "Assets");
        Directory.CreateDirectory(assets);
        var file = Path.Combine(assets, "gone.dntxt");
        File.WriteAllText(file, "x");

        using var db = new AssetDatabase(new[] { assets }, Path.Combine(_dir, "Library"));
        db.Refresh();
        Assert.That(db.GetGuid(file), Is.Not.Null);

        File.Delete(file);
        File.Delete(file + ".meta");
        db.Refresh();

        Assert.Multiple(() =>
        {
            Assert.That(db.GetGuid(file), Is.Null);
            Assert.That(db.AllAssets, Is.Empty);
        });
    }

    [Test]
    public void Refresh_AcrossInstances_ReusesPersistedCache()
    {
        var assets = Path.Combine(_dir, "Assets");
        var library = Path.Combine(_dir, "Library");
        Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(assets, "p.dntxt"), "persisted");

        TextNodeImporter.ImportCount = 0;
        using (var db = new AssetDatabase(new[] { assets }, library))
        {
            db.Refresh();
        }

        Assert.That(TextNodeImporter.ImportCount, Is.EqualTo(1));

        // A new database over the same project (same cache + .meta) must not re-import.
        using (var db = new AssetDatabase(new[] { assets }, library))
        {
            db.Refresh();
        }

        Assert.That(TextNodeImporter.ImportCount, Is.EqualTo(1), "persisted cache should be reused");
    }

    [Test]
    public void QueuedChange_IsAppliedOnlyOnExplicitRefresh()
    {
        var assets = Path.Combine(_dir, "Assets");
        Directory.CreateDirectory(assets);
        var file = Path.Combine(assets, "q.dntxt");
        File.WriteAllText(file, "v1");

        using var db = new AssetDatabase(new[] { assets }, Path.Combine(_dir, "Library"));
        TextNodeImporter.ImportCount = 0;
        db.Refresh(); // initial full scan imports once
        Assert.That(TextNodeImporter.ImportCount, Is.EqualTo(1));

        db.StartWatching();
        File.WriteAllText(file, "v2");
        db.EnqueueChanged(file); // simulate a watcher event — queue only, no reimport

        Assert.Multiple(() =>
        {
            Assert.That(TextNodeImporter.ImportCount, Is.EqualTo(1), "queued change must not reimport yet");
            Assert.That(db.LoadAsset<AssetNode>(file)?.Name, Is.EqualTo("v1"));
        });

        db.Refresh(); // explicit refresh applies the queued change

        Assert.Multiple(() =>
        {
            Assert.That(TextNodeImporter.ImportCount, Is.EqualTo(2), "refresh should reimport queued change");
            Assert.That(db.LoadAsset<AssetNode>(file)?.Name, Is.EqualTo("v2"));
        });
    }
}