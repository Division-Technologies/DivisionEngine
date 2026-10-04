using DivisionEngine.Generators.Entities;

namespace DivisionEngine.Generators.Tests;

/// <summary>
///     Which entity-holding members the generated remapper rewrites. Whatever it cannot assign is left
///     to <see cref="ComponentConventionAnalyzer" /> to report, never emitted as code that fails to compile.
/// </summary>
[TestFixture]
public sealed class ComponentRegistrationGeneratorTests
{
    private static (string[] Ids, string Generated) Run(string body)
    {
        return AnalyzerHarness.Generate(new ComponentRegistrationGenerator(),
            "using DivisionEngine;\nnamespace Probe;\n" + body);
    }

    [Test]
    public void APublicEntityField_IsRemapped()
    {
        var (ids, generated) = Run("[Component] public struct Link { public Entity Target; }");

        Assert.That(ids, Is.Empty);
        Assert.That(generated, Does.Contain("value.Target = map.Resolve(value.Target);"));
    }

    [Test]
    public void AnEntityInANestedStruct_IsRemapped()
    {
        var (ids, generated) = Run("""
                                   public struct Pair { public Entity First; public Entity Second; }
                                   [Component] public struct Link { public Pair Ends; }
                                   """);

        Assert.That(ids, Is.Empty);
        Assert.Multiple(() =>
        {
            Assert.That(generated, Does.Contain("value.Ends.First = map.Resolve(value.Ends.First);"));
            Assert.That(generated, Does.Contain("value.Ends.Second = map.Resolve(value.Ends.Second);"));
        });
    }

    [TestCase("public struct Link { public readonly Entity Target; }")]
    [TestCase("public readonly struct Link { public readonly Entity Target; }")]
    [TestCase("public struct Link { public Entity Target { get; } }")]
    [TestCase("public struct Link { public Entity Target { get; init; } }")]
    [TestCase("public readonly record struct Link(Entity Target);")]
    [TestCase("public struct Link { private Entity _target; public Entity Get() => _target; }")]
    [TestCase(
        "public struct Link { public Inner Wrapped; } public struct Inner { private Entity _target; public Entity Get() => _target; }")]
    [TestCase("public struct Link { public readonly Inner Wrapped; } public struct Inner { public Entity Target; }")]
    public void AnEntityGeneratedCodeCannotAssign_IsLeftOut(string declaration)
    {
        var (ids, generated) = Run("[Component] " + declaration);

        // The harness requires the output to compile; what matters is that no remapper was emitted.
        Assert.That(ids, Is.Empty);
        Assert.That(generated, Does.Not.Contain("RegisterEntityFields"));
    }

    [TestCase("public struct Link { public Entity Target { get; set; } }")]
    [TestCase("public record struct Link(Entity Target);")]
    public void ASettableEntityAutoProperty_IsRemapped(string declaration)
    {
        var (ids, generated) = Run("[Component] " + declaration);

        Assert.That(ids, Is.Empty);
        Assert.That(generated, Does.Contain("value.Target = map.Resolve(value.Target);"));
    }
}