using DivisionEngine.Authoring.Assets;

namespace DivisionEngine.Tests.Assets;

[TestFixture]
public sealed class BinaryImporterTests
{
    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "DivisionBinaryTests", Guid.NewGuid().ToString("N"));
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
    public void ImportAsset_UncoveredExtension_ImportsAsBinary()
    {
        var path = Path.Combine(_dir, "blob.xyzzy");
        byte[] bytes = [1, 2, 3, 4, 250];
        File.WriteAllBytes(path, bytes);

        using var db = new AssetDatabase(Path.Combine(_dir, "cache"));
        var asset = db.ImportAsset<BinaryAsset>(path);

        Assert.That(asset, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(asset!.Data, Is.EqualTo(bytes));
            Assert.That(asset.Extension, Is.EqualTo(".xyzzy"));
        });
    }

    [Test]
    public void BinaryAsset_CacheHit_ReloadsFromCache()
    {
        var path = Path.Combine(_dir, "blob.raw");
        byte[] bytes = [9, 8, 7];
        File.WriteAllBytes(path, bytes);
        var cache = Path.Combine(_dir, "cache");

        var first = new AssetDatabase(cache).ImportAsset<BinaryAsset>(path);
        Assert.That(first!.Data, Is.EqualTo(bytes));

        // A fresh database over the same cache + .meta returns the cached binary asset.
        using var second = new AssetDatabase(cache);
        var reloaded = second.ImportAsset<BinaryAsset>(path);
        Assert.That(reloaded!.Data, Is.EqualTo(bytes));
    }

    [Test]
    public void ImportAsset_ImporterThrows_SkipsWithoutRegistering()
    {
        var path = Path.Combine(_dir, "bad.dnthrow");
        File.WriteAllText(path, "x");

        using var db = new AssetDatabase(Path.Combine(_dir, "cache"));

        Assert.That(db.ImportAsset<AssetNode>(path), Is.Null);
        Assert.Multiple(() =>
        {
            // A failed import registers nothing and writes no .meta sidecar.
            Assert.That(db.GetGuid(path), Is.Null);
            Assert.That(File.Exists(path + ".meta"), Is.False);
        });
    }

    [Test]
    public void Refresh_SkipsThrowingImporter_ContinuesWithOthers()
    {
        var assets = Path.Combine(_dir, "Assets");
        Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(assets, "good.dntxt"), "ok");
        File.WriteAllText(Path.Combine(assets, "bad.dnthrow"), "x");

        using var db = new AssetDatabase([assets], Path.Combine(_dir, "Library"));

        Assert.DoesNotThrow(() => db.Refresh());
        Assert.Multiple(() =>
        {
            Assert.That(db.LoadAsset<AssetNode>(Path.Combine(assets, "good.dntxt"))?.Name, Is.EqualTo("ok"));
            Assert.That(db.GetGuid(Path.Combine(assets, "bad.dnthrow")), Is.Null);
        });
    }

    [Test]
    public void Refresh_ImportsUncoveredFileAsBinaryAsset()
    {
        var assets = Path.Combine(_dir, "Assets");
        Directory.CreateDirectory(assets);
        File.WriteAllBytes(Path.Combine(assets, "data.bin"), [1, 2, 3]);

        using var db = new AssetDatabase([assets], Path.Combine(_dir, "Library"));
        db.Refresh();

        Assert.That(db.AllAssets, Has.Count.EqualTo(1));
        Assert.That(db.LoadAsset<BinaryAsset>(Path.Combine(assets, "data.bin"))!.Data,
            Is.EqualTo(new byte[] { 1, 2, 3 }));
    }
}