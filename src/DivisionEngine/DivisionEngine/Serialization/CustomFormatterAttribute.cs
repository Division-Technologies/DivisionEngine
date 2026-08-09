namespace DivisionEngine;

/// <summary>
///     Declares that the annotated type is an <see cref="IValueFormatter{T}" /> for
///     <paramref name="targetType" />. The source generator emits a module initializer that
///     registers the formatter into <see cref="FormatterRegistry" /> and an assembly-level
///     <see cref="FormatterRegistrationAttribute" /> so downstream compilations can statically
///     verify resolvability. Open generic targets (e.g. typeof(List&lt;&gt;)) are registered as
///     factories; the formatter's generic arity must match the target's.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class CustomFormatterAttribute(Type targetType) : Attribute
{
    public Type TargetType { get; } = targetType;
}