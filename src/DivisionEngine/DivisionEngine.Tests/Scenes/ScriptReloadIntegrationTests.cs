using DivisionEngine.Authoring.Assets;
using DivisionEngine.Tests.Scripting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;

namespace DivisionEngine.Tests.Scenes;

/// <summary>The bit of world state the reload has to carry.</summary>
[Component]
[AutoSerialization]
[TypeId("c9a3e510-7b64-4d21-8f05-2e6b1d4a7700")]
public partial struct Score
{
    [Serialize] public int Value;
}

/// <summary>
///     A reload driven the way the engine drives one: a dirty script database, a frame, and the
///     entity world coming out the other side. Uses <see cref="FakeScriptImporter" /> so the test
///     needs no MSBuild — what is exercised here is the wiring, not the compiler.
/// </summary>
[TestFixture]
public sealed class ScriptReloadIntegrationTests
{
    private string _dir = "";

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "DivisionReloadWiring", Guid.NewGuid().ToString("N"));
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

    private sealed class Ticker : Behaviour
    {
        public static int Started;

        protected override async BehaviourTask Run(BehaviourContext context)
        {
            Interlocked.Increment(ref Started);
            while (true)
            {
                await context.Phase(PhaseId.Update);
            }
            // ReSharper disable once FunctionNeverReturns
        }
    }

    [Test]
    public void AFrameWithDirtyScripts_CarriesTheWorldAcrossAndStopsBehaviours()
    {
        using var engine = new Engine(NullLogger.Instance, new JobScheduler(2));
        using var database = new AssetDatabase(Path.Combine(_dir, "cache"));
        var host = new ScriptHost();
        var reload = new ScriptReloadSystem(database, host);
        engine.AddSystem(reload);

        var entity = engine.World.CreateEntity(ComponentType<Score>.Id);
        engine.World.SetComponent(entity, new Score { Value = 11 });
        Ticker.Started = 0;
        engine.Graph.Start(entity, new Ticker());

        // Let the behaviour get going, then make the database want a reload.
        engine.RunFrame(Realtime.FromSeconds(0));
        engine.RunFrame(Realtime.FromSeconds(0.01));
        Assert.That(Ticker.Started, Is.EqualTo(1), "the behaviour ran before the reload");
        Assert.That(engine.Graph.LiveTurnCount, Is.EqualTo(1));

        MakeScriptsDirty(database);
        engine.RunFrame(Realtime.FromSeconds(0.02));

        Assert.That(reload.LastReload, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(engine.World.EntityCount, Is.EqualTo(1), "the world was rebuilt from its snapshot");
            Assert.That(reload.LastReload!.CancelledTurns, Is.EqualTo(1));
            Assert.That(engine.Graph.LiveTurnCount, Is.EqualTo(0), "turns do not survive a reload");
        });

        var rebuilt = SingleEntity(engine.World);
        Assert.That(engine.World.GetComponentReadOnly<Score>(rebuilt).Value, Is.EqualTo(11),
            "component state crossed the reload");

        // The cancelled turn must stay dead rather than resuming on the next phase.
        engine.RunFrame(Realtime.FromSeconds(0.03));
        Assert.That(Ticker.Started, Is.EqualTo(1), "the cancelled behaviour was not resumed");
    }

    [Test]
    public void AFrameWithCleanScripts_DoesNotTouchTheWorld()
    {
        using var engine = new Engine(NullLogger.Instance, new JobScheduler(2));
        using var database = new AssetDatabase(Path.Combine(_dir, "cache"));
        var host = new ScriptHost();
        var reload = new ScriptReloadSystem(database, host);
        engine.AddSystem(reload);

        var entity = engine.World.CreateEntity(ComponentType<Score>.Id);
        engine.World.SetComponent(entity, new Score { Value = 5 });

        engine.RunFrame(Realtime.FromSeconds(0));

        Assert.Multiple(() =>
        {
            Assert.That(reload.LastReload, Is.Null, "no reload was attempted");
            Assert.That(SingleEntity(engine.World), Is.EqualTo(entity), "the same entity handle is still valid");
            Assert.That(engine.World.GetComponentReadOnly<Score>(entity).Value, Is.EqualTo(5));
        });
    }

    private static Entity SingleEntity(World world)
    {
        foreach (var chunk in world.Query().With<Score>().Build())
        {
            foreach (var e in chunk.Entities)
            {
                return e;
            }
        }

        Assert.Fail("no entity with Score");
        return Entity.Null;
    }

    /// <summary>Imports a fake compiled script so the database flags a pending reload.</summary>
    private void MakeScriptsDirty(AssetDatabase database)
    {
        FakeScriptImporter.DllPath = CompileTrivialDll();
        var scriptPath = Path.Combine(_dir, "user.dnscript");
        File.WriteAllText(scriptPath, "v1");
        database.Reimport(scriptPath);
        Assert.That(database.ScriptsDirty, Is.True, "the fake importer should have flagged a reload");
    }

    /// <summary>A real but empty assembly, so the swap has something loadable to put in the new context.</summary>
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
        {
            Assert.Fail(string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        }

        return path;
    }
}
