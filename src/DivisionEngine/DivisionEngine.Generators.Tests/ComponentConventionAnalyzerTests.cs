using DivisionEngine.Generators.Entities;

namespace DivisionEngine.Generators.Tests;

/// <summary>
///     Each of these is a mistake that otherwise surfaces at run time, far from the declaration —
///     a component the scene loader cannot resolve, or one whose values are quietly dropped.
/// </summary>
[TestFixture]
public sealed class ComponentConventionAnalyzerTests
{
    private static string[] Diagnose(string body)
    {
        return AnalyzerHarness.Diagnose(new ComponentConventionAnalyzer(),
            "using DivisionEngine;\nnamespace Probe;\n" + body);
    }

    [Test]
    public void ASerializableComponent_IsAccepted()
    {
        Assert.That(Diagnose("""
                             [Component]
                             [AutoSerialization]
                             public partial struct Health
                             {
                                 [Serialize] public int Value;
                             }
                             """), Is.Empty);
    }

    [Test]
    public void ATagComponent_IsAccepted()
    {
        // No fields means nothing to write, so no formatter is needed.
        Assert.That(Diagnose("[Component] public struct Frozen;"), Is.Empty);
    }

    [Test]
    public void ATypeWithoutTheAttribute_IsIgnored()
    {
        Assert.That(Diagnose("public struct NotAComponent { public int Value; }"), Is.Empty);
    }

    [Test]
    public void AComponentWithFieldsAndNoFormatter_IsFlagged()
    {
        Assert.That(Diagnose("""
                             [Component]
                             public struct Health
                             {
                                 public int Value;
                             }
                             """), Is.EqualTo(new[] { ComponentConventionAnalyzer.UnserializableValueId }));
    }

    [Test]
    public void AComponentCoveredByACustomFormatter_IsAccepted()
    {
        Assert.That(Diagnose("""
                             [Component]
                             public struct Health
                             {
                                 public int Value;
                             }

                             [CustomFormatter(typeof(Health))]
                             public readonly struct HealthFormatter : IValueFormatter<Health>
                             {
                                 public void Serialize<TS>(ref TS s, int id, System.ReadOnlySpan<byte> hint, in Health value)
                                     where TS : ISerializer, allows ref struct => s.I32(id, hint, value.Value);

                                 public Health Deserialize<TD>(ref TD d, int id, System.ReadOnlySpan<byte> hint)
                                     where TD : IDeserializer, allows ref struct => new Health { Value = d.I32(id, hint) };
                             }
                             """), Is.Empty, "the formatter may be declared anywhere in the assembly");
    }

    [Test]
    public void AComponentNestedInAPrivateScope_IsFlagged()
    {
        Assert.That(Diagnose("""
                             public sealed class Outer
                             {
                                 [Component]
                                 private struct Hidden;
                             }
                             """), Is.EqualTo(new[] { ComponentConventionAnalyzer.NotAssemblyVisibleId }));
    }

    [Test]
    public void AComponentNestedInAPublicScope_IsAccepted()
    {
        Assert.That(Diagnose("""
                             public sealed class Outer
                             {
                                 [Component]
                                 public struct Visible;
                             }
                             """), Is.Empty);
    }

    [Test]
    public void AManagedComponentThatCannotSerialize_IsFlagged()
    {
        Assert.That(Diagnose("[Component] public sealed class Label { public string Value = \"\"; }"),
            Is.EqualTo(new[] { ComponentConventionAnalyzer.ManagedNotSerializableId }));
    }

    [Test]
    public void ABehaviour_IsAcceptedAsAManagedComponent()
    {
        // Behaviour implements ISerializable with an empty default, so a stateless one is fine.
        Assert.That(Diagnose("""
                             [Component]
                             public sealed class Idle : Behaviour
                             {
                                 protected override BehaviourTask Run(BehaviourContext context) => default;
                             }
                             """), Is.Empty);
    }

    [Test]
    public void AGenericComponent_IsAnError()
    {
        Assert.That(Diagnose("[Component] public struct Slot<T> { public int Index; }"),
            Is.EqualTo(new[] { ComponentConventionAnalyzer.GenericComponentId }));
    }

    [Test]
    public void AComponentNestedInAGenericType_IsAnError()
    {
        Assert.That(Diagnose("""
                             public sealed class Outer<T>
                             {
                                 [Component]
                                 public struct Inner { public int Value; }
                             }
                             """), Is.EqualTo(new[] { ComponentConventionAnalyzer.GenericComponentId }));
    }
}
