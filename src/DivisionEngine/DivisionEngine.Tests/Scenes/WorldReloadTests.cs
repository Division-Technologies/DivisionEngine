using System.Reflection;
using System.Runtime.CompilerServices;
using DivisionEngine.Authoring.Assets;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DivisionEngine.Tests.Scenes;

/// <summary>
///     Component state crossing a user-assembly swap. These use a real collectible load context,
///     because the thing being tested — that the registry no longer pins user types — is only
///     observable as an assembly actually unloading.
/// </summary>
[TestFixture]
public sealed class WorldReloadTests
{
    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "DivisionWorldReloadTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        // Each test loads its own copy of UserScripts.Counter; one left registered would collide with
        // the next test's copy. The tests' worlds are disposed by now, which the registry requires.
        ComponentTypeRegistry.UnregisterUnloadable();

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private string _dir = "";

    /// <summary>
    ///     A user component with a hand-written formatter and registration, because the test compiles
    ///     this without the source generator — <c>[Component]</c> would otherwise do the same.
    ///     <c>{{EXTRA_FIELDS}}</c> lets a version of the type grow a field.
    /// </summary>
    private const string CounterSource = """
                                         using System;
                                         using System.Runtime.CompilerServices;
                                         using DivisionEngine;

                                         namespace UserScripts;

                                         public struct Counter
                                         {
                                             public int Value;
                                         {{EXTRA_FIELDS}}

                                             public sealed class Formatter : IValueFormatter<Counter>
                                             {
                                                 public void Serialize<TS>(ref TS s, int id, ReadOnlySpan<byte> hint, in Counter v)
                                                     where TS : ISerializer, allows ref struct
                                                 {
                                                     s.BeginStruct(id, hint);
                                                     s.I32(1, default, v.Value);
                                         {{EXTRA_WRITE}}
                                                     s.EndStruct();
                                                 }

                                                 public Counter Deserialize<TD>(ref TD d, int id, ReadOnlySpan<byte> hint)
                                                     where TD : IDeserializer, allows ref struct
                                                 {
                                                     var v = default(Counter);
                                                     if (d.TryBeginStruct(id, hint))
                                                     {
                                                         v.Value = d.I32(1, default);
                                         {{EXTRA_READ}}
                                                         d.EndStruct();
                                                     }
                                                     return v;
                                                 }
                                             }
                                         }

                                         public static class Bootstrap
                                         {
                                             public static Entity Spawn(World world, int value)
                                             {
                                                 var e = world.CreateEntity(ComponentType<Counter>.Id);
                                                 world.SetComponent(e, new Counter { Value = value });
                                                 return e;
                                             }

                                             public static int Read(World world, Entity e)
                                                 => world.GetComponentReadOnly<Counter>(e).Value;

                                             public static int ReadExtra(World world, Entity e)
                                                 => {{EXTRA_READBACK}};
                                         }

                                         internal static class Registration
                                         {
                                             [ModuleInitializer]
                                             internal static void Register()
                                             {
                                                 FormatterRegistry.Register<Counter>(new Counter.Formatter());
                                                 ComponentTypeRegistry.RegisterComponent<Counter>();
                                                 ComponentTypeRegistry.RegisterValueSerializer<Counter>();
                                             }
                                         }
                                         """;

    private static string CounterV1()
    {
        return CounterSource
            .Replace("{{EXTRA_FIELDS}}", "")
            .Replace("{{EXTRA_WRITE}}", "")
            .Replace("{{EXTRA_READ}}", "")
            .Replace("{{EXTRA_READBACK}}", "0");
    }

    /// <summary>The same component after the user added a field — the schema-evolution case.</summary>
    private static string CounterV2()
    {
        return CounterSource
            .Replace("{{EXTRA_FIELDS}}", "    public int Bonus;")
            .Replace("{{EXTRA_WRITE}}", "                s.I32(2, default, v.Bonus);")
            .Replace("{{EXTRA_READ}}", "                v.Bonus = d.I32(2, default);")
            .Replace("{{EXTRA_READBACK}}", "world.GetComponentReadOnly<Counter>(e).Bonus");
    }

    private static object? Call(Assembly assembly, string method, params object?[] args)
    {
        var type = assembly.GetType("UserScripts.Bootstrap")!;
        return type.GetMethod(method)!.Invoke(null, args);
    }

    [Test]
    public void ComponentState_SurvivesAnAssemblySwap_AndTheOldAssemblyUnloads()
    {
        var host = new ScriptHost();
        using var world = new World();

        var (snapshot, entityCount, oldAssembly) = RunV1(host, world);

        host.Swap([Compile("CounterV2", CounterV2())]);
        var created = WorldReload.AfterSwap(world, snapshot);

        Assert.That(created, Has.Length.EqualTo(entityCount));

        var v2 = host.LoadedAssemblies[0];
        Assert.Multiple(() =>
        {
            Assert.That(Call(v2, "Read", world, created[0]), Is.EqualTo(7), "the old field's value carried over");
            Assert.That(Call(v2, "ReadExtra", world, created[0]), Is.EqualTo(0),
                "the field the user added reads as its default");
            Assert.That(v2, Is.Not.SameAs(oldAssembly.Target));
        });

        // The registry was the only thing holding the old types; with that released the context
        // should actually come apart. ScriptHost.Unload already collects, but be generous.
        for (var i = 0; i < 10 && oldAssembly.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.That(oldAssembly.IsAlive, Is.False, "the previous user assembly should have unloaded");
    }

    /// <summary>
    ///     Kept out of the test body, and non-inlined, so no local of the test method holds the V1
    ///     assembly alive once it returns.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private (EntityScene Snapshot, int EntityCount, WeakReference OldAssembly) RunV1(ScriptHost host, World world)
    {
        host.Swap([Compile("CounterV1", CounterV1())]);
        var v1 = host.LoadedAssemblies[0];

        Call(v1, "Spawn", world, 7);
        Call(v1, "Spawn", world, 9);
        var count = world.EntityCount;

        return (WorldReload.BeforeSwap(world), count, new WeakReference(v1));
    }

    /// <summary>Carries a bare world across a reload, the way WorldReloadParticipant carries an engine's.</summary>
    private sealed class WorldParticipant(World world) : IReloadParticipant
    {
        private EntityScene? _snapshot;

        public void Capture()
        {
            _snapshot = WorldReload.Capture(world);
        }

        public void Release()
        {
            WorldReload.Release(world);
        }

        public void Restore(ISerializedObjectResolver references)
        {
            WorldReload.AfterSwap(world, _snapshot!, references);
        }
    }

    [Test]
    public void AFailedSwap_PutsThePreviousAssembliesBack_AndTheWorldWithThem()
    {
        var host = new ScriptHost();
        using var world = new World();
        using var database = new AssetDatabase(Path.Combine(_dir, "cache"));
        host.Swap([Compile("CounterV1", CounterV1())]);
        var entity = (Entity)Call(host.LoadedAssemblies[0], "Spawn", world, 7)!;

        var broken = Compile("Broken", CounterV2().Replace(
            "ComponentTypeRegistry.RegisterValueSerializer<Counter>();",
            "ComponentTypeRegistry.RegisterValueSerializer<Counter>(); throw new InvalidOperationException(\"init\");"));

        Assert.That(() => database.ReloadScripts(host, [broken], new WorldParticipant(world)),
            Throws.TypeOf<ScriptReloadException>());

        var current = host.LoadedAssemblies.Single();
        Assert.Multiple(() =>
        {
            Assert.That(current.GetName().Name, Is.EqualTo("CounterV1"), "the previous build is loaded again");
            Assert.That(world.IsAlive(entity), Is.True, "under the same handle");
            Assert.That(Call(current, "Read", world, entity), Is.EqualTo(7));
        });
    }

    [Test]
    public void ReloadingWithoutAParticipant_ReleasesTheOldComponentTypes()
    {
        var host = new ScriptHost();
        using var database = new AssetDatabase(Path.Combine(_dir, "cache"));
        host.Swap([Compile("CounterV1", CounterV1())]);

        // The same component again: its persisted id would collide if the old type were still registered.
        Assert.That(() => database.ReloadScripts(host, [Compile("CounterV1Again", CounterV1())]), Throws.Nothing);
    }

    [Test]
    public void BeforeSwap_EmptiesTheWorldAndReleasesUserComponentTypes()
    {
        var host = new ScriptHost();
        using var world = new World();
        host.Swap([Compile("CounterOnly", CounterV1())]);
        Call(host.LoadedAssemblies[0], "Spawn", world, 1);

        var registered = ComponentTypeRegistry.TryResolveBySerializedTypeId(
            SerializedTypeId.Get(host.LoadedAssemblies[0].GetType("UserScripts.Counter")!), out var id);
        Assert.That(registered, Is.True);

        WorldReload.BeforeSwap(world);

        Assert.Multiple(() =>
        {
            Assert.That(world.EntityCount, Is.EqualTo(0));
            Assert.That(world.Archetypes, Has.Count.EqualTo(1), "only the empty root archetype remains");
            Assert.That(ComponentTypeRegistry.TryResolveBySerializedTypeId(
                SerializedTypeId.Get(host.LoadedAssemblies[0].GetType("UserScripts.Counter")!), out _), Is.False);
            Assert.That(() => ComponentTypeRegistry.GetInfo(id), Throws.InvalidOperationException,
                "the dropped id fails loudly rather than naming a different component");
        });
    }

    [Test]
    public void Clear_KeepsCachedQueriesUsable()
    {
        using var world = new World();
        var query = world.Query().With<LocalTransform>().Build();
        world.CreateTransform();
        Assert.That(query.CalculateEntityCount(), Is.EqualTo(1));

        world.Clear();
        Assert.That(query.CalculateEntityCount(), Is.EqualTo(0));

        world.CreateTransform();
        world.CreateTransform();
        Assert.That(query.CalculateEntityCount(), Is.EqualTo(2), "the same query object picks up the new archetypes");
    }

    [Test]
    public void Clear_KeepsStructuralHooksSoTheHierarchyStaysConsistent()
    {
        using var world = new World();
        var parent = world.CreateTransform();
        world.SetParent(world.CreateTransform(), parent);
        Assert.That(world.StructuralHooks, Is.Not.Empty);

        world.Clear();

        var newParent = world.CreateTransform();
        var newChild = world.CreateTransform();
        world.SetParent(newChild, newParent);
        world.DestroyEntity(newParent);

        Assert.That(world.EntityCount, Is.EqualTo(0), "the subtree still goes with its parent");
    }

    private string Compile(string name, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));

        var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var references = trusted
            .Where(p => p.Length > 0)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(World).Assembly.Location))
            .ToList();

        var compilation = CSharpCompilation.Create(
            name, [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var path = Path.Combine(_dir, name + ".dll");
        var result = compilation.Emit(path);
        if (!result.Success)
        {
            Assert.Fail("user compile failed:\n" +
                        string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        }

        return path;
    }
}