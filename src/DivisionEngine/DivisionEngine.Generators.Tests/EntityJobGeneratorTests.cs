using System.Collections.Immutable;
using DivisionEngine.Generators.Entities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DivisionEngine.Generators.Tests;

/// <summary>
///     The generator's refusals. Each is a shape that would otherwise produce code that does not
///     compile, or a job that silently runs on everything.
/// </summary>
[TestFixture]
public sealed class EntityJobGeneratorTests
{
    private static (string[] Ids, string Generated) Run(string body)
    {
        var source = "using DivisionEngine;\nnamespace Probe;\n"
                     + "[Component][AutoSerialization] public partial struct Position { [Serialize] public float X; }\n"
                     + "[Component][AutoSerialization] public partial struct Velocity { [Serialize] public float X; }\n"
                     + body;

        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var references = trusted
            .Where(p => p.Length > 0)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(ComponentAttribute).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            "EntityJobProbe", [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // The driver parses what it generates, so it needs the same language version as the input.
        var driver = CSharpGeneratorDriver
            .Create([new EntityJobGenerator().AsSourceGenerator()], parseOptions: (CSharpParseOptions)tree.Options)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        var generated = string.Join("\n", driver.GetRunResult().GeneratedTrees.Select(t => t.ToString()));
        var ids = diagnostics
            .Concat(driver.GetRunResult().Diagnostics)
            .Select(d => d.Id)
            .Distinct()
            .ToArray();

        // Anything the generator emits must itself compile.
        if (ids.Length == 0)
        {
            var errors = output.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToImmutableArray();
            Assert.That(errors, Is.Empty, "generated code must compile:\n" + string.Join("\n", errors.AsEnumerable()));
        }

        return (ids, generated);
    }

    [Test]
    public void AWellFormedJob_GeneratesCompilingCode()
    {
        var (ids, generated) = Run("""
                                   [EntityJob]
                                   public partial struct Integrate
                                   {
                                       public float Delta;
                                       private void Execute(ref Position p, in Velocity v) => p.X += v.X * Delta;
                                   }
                                   """);

        Assert.That(ids, Is.Empty);
        Assert.Multiple(() =>
        {
            Assert.That(generated, Does.Contain("Write<global::Probe.Position>()"));
            Assert.That(generated, Does.Contain("Read<global::Probe.Velocity>()"));
            Assert.That(generated, Does.Contain("GetSpan<global::Probe.Position>()"));
            Assert.That(generated, Does.Contain("GetReadOnlySpan<global::Probe.Velocity>()"));
        });
    }

    [Test]
    public void AJobWithoutExecute_IsRejected()
    {
        var (ids, _) = Run("[EntityJob] public partial struct Empty { public float Delta; }");
        Assert.That(ids, Does.Contain("DIVENT005"));
    }

    [Test]
    public void AByValueComponentParameter_IsRejected()
    {
        // By value would silently copy, so the write would go nowhere.
        var (ids, _) = Run("""
                           [EntityJob]
                           public partial struct ByValue
                           {
                               private void Execute(Position p) { }
                           }
                           """);

        Assert.That(ids, Does.Contain("DIVENT006"));
    }

    [Test]
    public void AJobNamingNoComponents_IsRejected()
    {
        var (ids, _) = Run("""
                           [EntityJob]
                           public partial struct Everything
                           {
                               private void Execute(Entity entity) { }
                           }
                           """);

        Assert.That(ids, Does.Contain("DIVENT007"));
    }

    [Test]
    public void ANonPartialJob_IsRejected()
    {
        var (ids, _) = Run("""
                           [EntityJob]
                           public struct Sealed
                           {
                               private void Execute(ref Position p) { }
                           }
                           """);

        Assert.That(ids, Does.Contain("DIVENT008"));
    }

    [Test]
    public void FilterAttributes_ReachTheQuery()
    {
        var (ids, generated) = Run("""
                                   [Component] public struct Disabled;

                                   [EntityJob]
                                   [WithNone(typeof(Disabled))]
                                   public partial struct IntegrateActive
                                   {
                                       private void Execute(ref Position p, in Velocity v) => p.X += v.X;
                                   }
                                   """);

        Assert.That(ids, Is.Empty);
        Assert.That(generated, Does.Contain("ComponentType<global::Probe.Disabled>.Id"));
    }
}