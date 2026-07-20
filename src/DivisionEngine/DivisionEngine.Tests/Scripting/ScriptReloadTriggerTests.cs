using DivisionEngine.Authoring.Assets;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DivisionEngine.Tests.Scripting;

[TestFixture]
public sealed class ScriptReloadTriggerTests
{
    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "DivisionReloadTrigger", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private string _dir = "";

    [Test]
    public void Reimport_OfCompiledScript_FlagsScriptsDirty()
    {
        FakeScriptImporter.DllPath = CompileTrivialDll();

        var assets = Path.Combine(_dir, "Assets");
        Directory.CreateDirectory(assets);
        var scriptPath = Path.Combine(assets, "user.dnscript");
        File.WriteAllText(scriptPath, "v1");

        using var db = new AssetDatabase([assets], Path.Combine(_dir, "Library"));
        var host = new ScriptHost();

        db.Refresh(); // first import flags a reload
        Assert.That(db.ScriptsDirty, Is.True);

        db.ReloadScriptsIfDirty(host); // consume the flag (and load the assembly)
        Assert.That(db.ScriptsDirty, Is.False);

        // Re-importing the script (as happens when a source file changes) must flag a reload again.
        File.WriteAllText(scriptPath, "v2");
        db.Reimport(scriptPath);
        Assert.That(db.ScriptsDirty, Is.True, "reimport of a compiled script must flag a reload");
    }

    private string CompileTrivialDll()
    {
        var tree = CSharpSyntaxTree.ParseText("public class Dummy { }");
        var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var references = trusted
            .Where(p => p.Length > 0)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));

        var compilation = CSharpCompilation.Create(
            "Trivial", [tree], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var path = Path.Combine(_dir, "trivial.dll");
        var result = compilation.Emit(path);
        if (!result.Success)
            Assert.Fail(string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return path;
    }
}
