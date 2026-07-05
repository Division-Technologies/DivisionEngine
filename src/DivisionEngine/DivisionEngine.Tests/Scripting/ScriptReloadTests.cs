using DivisionEngine;
using DivisionEngine.Authoring.Assets;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DivisionEngine.Tests.Scripting;

[TestFixture]
public sealed class ScriptReloadTests
{
    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "DivisionScriptTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private string _dir = "";

    // A user Component that derives from the engine's Component (so it inherits Scope/Id) and
    // serializes a single field. Implemented by hand so the test needs no source generator.
    private const string GreeterSource = """
        using DivisionEngine;
        namespace UserScripts;
        public sealed class Greeter : Component, ISerializable
        {
            public int Value;
            public void Serialize<T>(ref T s) where T : ISerializer, allows ref struct => s.I32(0, default, Value);
            public void Deserialize<T>(ref T d) where T : IDeserializer, allows ref struct => Value = d.I32(0, default);
        }
        """;

    [Test]
    public void ReloadScripts_MigratesStateAndRebindsToNewAssembly()
    {
        var dll1 = CompileUserAssembly("UserScriptsV1", GreeterSource);

        var host = new ScriptHost();
        host.Swap([dll1]);
        var greeterTypeV1 = host.LoadedAssemblies[0].GetType("UserScripts.Greeter")!;

        using var db = new AssetDatabase(Path.Combine(_dir, "cache"));
        var scope = db.CreateScope();
        var greeter = (ISerializableObject)Activator.CreateInstance(greeterTypeV1)!;
        greeterTypeV1.GetField("Value")!.SetValue(greeter, 42);
        db.AddObject(scope, greeter);

        // Recompile (a new assembly) and reload — state must migrate, type must bind to the new assembly.
        var dll2 = CompileUserAssembly("UserScriptsV2", GreeterSource);
        db.ReloadScripts(host, [dll2]);

        var reloaded = db.LoadAsset<ISerializableObject>(scope.Id);

        Assert.That(reloaded, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(reloaded!.GetType().FullName, Is.EqualTo("UserScripts.Greeter"));
            Assert.That(reloaded.GetType().GetField("Value")!.GetValue(reloaded), Is.EqualTo(42));
            Assert.That(reloaded.GetType().Assembly, Is.Not.SameAs(greeterTypeV1.Assembly),
                "reloaded type should come from the new user assembly");
        });
    }

    private string CompileUserAssembly(string name, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));

        var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var references = trusted
            .Where(p => p.Length > 0)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(ISerializableObject).Assembly.Location))
            .ToList();

        var compilation = CSharpCompilation.Create(
            name,
            [tree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var path = Path.Combine(_dir, name + ".dll");
        var result = compilation.Emit(path);
        if (!result.Success)
            Assert.Fail("user compile failed:\n" +
                        string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return path;
    }
}
