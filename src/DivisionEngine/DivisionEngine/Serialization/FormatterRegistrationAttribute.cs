namespace DivisionEngine;

/// <summary>
///     Emitted by the source generator for every formatter an assembly provides
///     ([CustomFormatter] formatters and [AutoSerialization] structs).
///     Compile time: downstream generators enumerate these to build the resolvable-type set.
///     Run time: <see cref="FormatterRegistry" /> uses them as a lazy fallback to force module
///     initializers of not-yet-initialized assemblies.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class FormatterRegistrationAttribute(Type targetType, Type formatterType) : Attribute
{
    public Type TargetType { get; } = targetType;
    public Type FormatterType { get; } = formatterType;
}