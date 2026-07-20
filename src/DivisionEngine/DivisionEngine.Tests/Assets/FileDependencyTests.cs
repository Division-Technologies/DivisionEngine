using DivisionEngine.Authoring.Assets;

namespace DivisionEngine.Tests.Assets;

[TestFixture]
public sealed class FileDependencyTests
{
    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "DivisionFileDepTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _assets = Path.Combine(_dir, "Assets");
        Directory.CreateDirectory(_assets);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private string _dir = "";
    private string _assets = "";

    private (AssetDatabase db, string mainPath, string dataFile) SetupAsset()
    {
        var dataFile = Path.Combine(_dir, "data.txt"); // outside the Assets root
        File.WriteAllText(dataFile, "v1");
        var mainPath = Path.Combine(_assets, "main.dndf");
        File.WriteAllText(mainPath, dataFile);

        var db = new AssetDatabase([_assets], Path.Combine(_dir, "Library"));
        db.Refresh();
        Assert.That(db.LoadAsset<AssetNode>(mainPath)?.Name, Is.EqualTo("v1"), "initial import");
        return (db, mainPath, dataFile);
    }

    [Test]
    public void ChangingDependsOnFile_ReimportsViaWatcherQueue()
    {
        var (db, mainPath, dataFile) = SetupAsset();
        using (db)
        {
            db.StartWatching();
            File.WriteAllText(dataFile, "v2");
            db.EnqueueChanged(dataFile); // simulate the watcher observing the input file change
            db.Refresh();

            Assert.That(db.LoadAsset<AssetNode>(mainPath)?.Name, Is.EqualTo("v2"));
        }
    }

    [Test]
    public void ChangingDependsOnFile_ReimportsOnRescan()
    {
        var (db, mainPath, dataFile) = SetupAsset();
        using (db)
        {
            // No watcher: Refresh() performs a full rescan; the combined input hash detects the change.
            File.WriteAllText(dataFile, "v2");
            db.Refresh();

            Assert.That(db.LoadAsset<AssetNode>(mainPath)?.Name, Is.EqualTo("v2"));
        }
    }
}