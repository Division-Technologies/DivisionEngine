using DivisionEngine.Generators.Entities;

namespace DivisionEngine.Generators.Tests;

/// <summary>
///     The generator's refusals. Each is a shape that would otherwise produce code that does not
///     compile, or a job that silently runs on everything.
/// </summary>
[TestFixture]
public sealed class EntityJobGeneratorTests
{
    private const string Components =
        "[Component][AutoSerialization] public partial struct Position { [Serialize] public float X; }\n"
        + "[Component][AutoSerialization] public partial struct Velocity { [Serialize] public float X; }\n";

    private static (string[] Ids, string Generated) Run(string body)
    {
        return RunSource("using DivisionEngine;\nnamespace Probe;\n" + Components + body);
    }

    private static (string[] Ids, string Generated) RunSource(string source)
    {
        return AnalyzerHarness.Generate(new EntityJobGenerator(), source);
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

    [Test]
    public void SameNamedJobsInDifferentScopes_AllGenerate()
    {
        // Three jobs called Move must not collide on the generated file's hint name.
        var (ids, generated) = RunSource(
            "using DivisionEngine;\n"
            + "namespace A\n{\n" + Components
            + "[EntityJob] public partial struct Move { private void Execute(ref Position p) { } }\n"
            + "public partial class Outer { [EntityJob] public partial struct Move { private void Execute(ref Position p) { } } }\n"
            + "}\n"
            + "namespace B\n{\n"
            + "[EntityJob] public partial struct Move { private void Execute(ref A.Position p) { } }\n"
            + "}\n");

        Assert.That(ids, Is.Empty);
        Assert.That(generated.Split("partial struct Move").Length - 1, Is.EqualTo(3));
    }

    [Test]
    public void AGenericJob_IsRejected()
    {
        var (ids, generated) = Run("""
                                   [EntityJob]
                                   public partial struct Generic<T>
                                   {
                                       private void Execute(ref Position p) { }
                                   }
                                   """);

        Assert.That(ids, Does.Contain("DIVENT009"));
        Assert.That(generated, Is.Empty);
    }

    [TestCase(
        "public partial class Outer<T> { [EntityJob] public partial struct Job { private void Execute(ref Position p) { } } }",
        TestName = "AJobNestedInAGenericType_IsRejected")]
    [TestCase(
        "public class Outer { [EntityJob] public partial struct Job { private void Execute(ref Position p) { } } }",
        TestName = "AJobNestedInANonPartialType_IsRejected")]
    [TestCase("[EntityJob] public ref partial struct Job { private void Execute(ref Position p) { } }",
        TestName = "ARefStructJob_IsRejected")]
    [TestCase("[EntityJob] public partial struct Job { private void Execute<T>(ref Position p) { } }",
        TestName = "AGenericExecute_IsRejected")]
    public void AnUnsupportedShape_IsRejected(string job)
    {
        var (ids, generated) = Run(job);

        Assert.That(ids, Does.Contain("DIVENT009"));
        Assert.That(generated, Is.Empty);
    }

    [TestCase("public partial class Outer")]
    [TestCase("public partial struct Outer")]
    [TestCase("public partial record Outer")]
    [TestCase("public partial record class Outer")]
    [TestCase("public partial record struct Outer")]
    [TestCase("public readonly partial record struct Outer")]
    [TestCase("public partial interface Outer")]
    public void AJobNestedInAnyPartialType_GeneratesCompilingCode(string containing)
    {
        var (ids, generated) = Run(containing
                                   + " { [EntityJob] public partial struct Job { private void Execute(ref Position p) { } } }");

        Assert.That(ids, Is.Empty);
        Assert.That(generated, Does.Contain("partial struct Job"));
    }

    [Test]
    public void ARecordStructJob_GeneratesCompilingCode()
    {
        var (ids, generated) = Run("""
                                   [EntityJob]
                                   public partial record struct Move(float Delta)
                                   {
                                       private void Execute(ref Position p) => p.X += Delta;
                                   }
                                   """);

        Assert.That(ids, Is.Empty);
        Assert.That(generated, Does.Contain("partial record struct Move"));
    }

    [TestCase("Entity e")]
    [TestCase("in Entity e")]
    [TestCase("ref readonly Entity e")]
    [TestCase("JobContext c")]
    [TestCase("in JobContext c")]
    [TestCase("ref readonly JobContext c")]
    [TestCase("ref readonly Velocity v")]
    [TestCase("Entity e, in JobContext c, in Velocity v")]
    public void ASupportedParameterForm_GeneratesCompilingCode(string parameters)
    {
        var (ids, _) = Run($$"""
                             [EntityJob]
                             public partial struct Job
                             {
                                 private void Execute(ref Position p, {{parameters}}) { }
                             }
                             """);

        Assert.That(ids, Is.Empty);
    }

    [TestCase("ref Entity e", "")]
    [TestCase("out Entity e", "e = default;")]
    [TestCase("ref JobContext c", "")]
    [TestCase("out JobContext c", "c = default;")]
    [TestCase("out Velocity v", "v = default;")]
    public void AnUnsupportedParameterForm_IsRejected(string parameter, string body)
    {
        var (ids, generated) = Run($$"""
                                     [EntityJob]
                                     public partial struct Job
                                     {
                                         private void Execute(ref Position p, {{parameter}}) { {{body}} }
                                     }
                                     """);

        Assert.That(ids, Does.Contain("DIVENT006"));
        Assert.That(generated, Is.Empty);
    }

    [Test]
    public void ATagOnlyJob_ReadsTheStructureWithoutWritingIt()
    {
        var (ids, generated) = Run("""
                                   [Component] public struct Tagged;

                                   [EntityJob]
                                   [WithAll(typeof(Tagged))]
                                   public partial struct CountTagged
                                   {
                                       private void Execute(Entity e) { }
                                   }
                                   """);

        Assert.That(ids, Is.Empty);
        Assert.That(generated, Does.Not.Contain("Exclusive"));
        Assert.That(generated, Does.Contain("Access.Read(global::DivisionEngine.ResourceId.Structure)"));

        // And that expression is a shared structure read, not a write.
        AccessSet access = Access.Read(ResourceId.Structure);
        Assert.Multiple(() =>
        {
            Assert.That(access.WritesStructure, Is.False);
            Assert.That(access.Reads, Is.EqualTo(new[] { ResourceId.Structure }));
        });
    }

    [Test]
    public void EachChunk_RunsOnItsOwnCopyOfTheJob()
    {
        // A mutating Execute on one shared captured copy would race across concurrently running chunks.
        var (ids, generated) = Run("""
                                   [EntityJob]
                                   public partial struct Accumulate
                                   {
                                       public float Sum;
                                       private void Execute(in Position p) => Sum += p.X;
                                   }
                                   """);

        Assert.That(ids, Is.Empty);
        var body = generated[
            generated.IndexOf("global::DivisionEngine.ArchetypeChunk __chunk", StringComparison.Ordinal)..];
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("var __local = __self;"));
            Assert.That(body, Does.Contain("__local.Execute("));
            Assert.That(body, Does.Not.Contain("__self.Execute("));
        });
    }
}