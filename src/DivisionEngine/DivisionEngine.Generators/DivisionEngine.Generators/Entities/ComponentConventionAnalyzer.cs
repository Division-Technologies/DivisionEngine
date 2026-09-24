using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DivisionEngine.Generators.Entities;

/// <summary>
///     Checks the conventions a <c>[Component]</c> type has to follow for the engine to be able to
///     register and persist it.
///     <para>
///         Every one of these is something that otherwise goes wrong at run time, usually far from
///         the declaration: a component the scene loader cannot resolve, or one whose values are
///         quietly dropped when a scene is saved. Catching them at the declaration is the whole point.
///     </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ComponentConventionAnalyzer : DiagnosticAnalyzer
{
    public const string UnserializableValueId = "DIVENT001";
    public const string NotAssemblyVisibleId = "DIVENT002";
    public const string ManagedNotSerializableId = "DIVENT003";
    public const string GenericComponentId = "DIVENT004";

    private const string Category = "DivisionEngine.Entities";

    private const string ComponentAttributeFullName = "DivisionEngine.ComponentAttribute";
    private const string AutoSerializationAttributeFullName = "DivisionEngine.AutoSerializationAttribute";
    private const string CustomFormatterAttributeFullName = "DivisionEngine.CustomFormatterAttribute";
    private const string FormatterRegistrationAttributeFullName = "DivisionEngine.FormatterRegistrationAttribute";
    private const string SerializableInterfaceFullName = "DivisionEngine.ISerializable";

    private static readonly DiagnosticDescriptor UnserializableValue = new(
        UnserializableValueId,
        "Component values cannot be serialized",
        "Component '{0}' has fields but no value formatter, so its values are dropped when a scene is saved; "
        + "mark it [AutoSerialization] or register an IValueFormatter for it with [CustomFormatter]",
        Category,
        DiagnosticSeverity.Warning,
        true);

    private static readonly DiagnosticDescriptor NotAssemblyVisible = new(
        NotAssemblyVisibleId,
        "Component is not visible to the whole assembly",
        "Component '{0}' is nested inside a private or protected scope, so no registration can be generated for it; "
        + "a scene saved with it cannot be loaded unless some code has already mentioned the type",
        Category,
        DiagnosticSeverity.Warning,
        true);

    private static readonly DiagnosticDescriptor ManagedNotSerializable = new(
        ManagedNotSerializableId,
        "Managed component cannot be serialized",
        "Component '{0}' is a class that does not implement ISerializable, so saving a scene containing it fails; "
        + "derive it from SerializableObject and mark it [AutoSerialization]",
        Category,
        DiagnosticSeverity.Warning,
        true);

    private static readonly DiagnosticDescriptor GenericComponent = new(
        GenericComponentId,
        "Component type cannot be generic",
        "Component '{0}' is generic, so it cannot be registered with the component type registry",
        Category,
        DiagnosticSeverity.Error,
        true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(UnserializableValue, NotAssemblyVisible, ManagedNotSerializable, GenericComponent);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(Start);
    }

    private static void Start(CompilationStartAnalysisContext context)
    {
        // Whether a struct's values can be written is not visible from its own declaration: a
        // formatter may be declared anywhere in this assembly, or come from a referenced one. So the
        // candidates are collected as types go by and judged once the compilation is complete.
        var formatterTargets = new ConcurrentDictionary<string, byte>();
        foreach (var name in ReferencedFormatterTargets(context.Compilation))
        {
            formatterTargets.TryAdd(name, 0);
        }

        var candidates = new ConcurrentBag<INamedTypeSymbol>();

        context.RegisterSymbolAction(symbolContext =>
        {
            if (symbolContext.Symbol is not INamedTypeSymbol symbol)
            {
                return;
            }

            foreach (var name in DeclaredFormatterTargets(symbol))
            {
                formatterTargets.TryAdd(name, 0);
            }

            if (!IsComponent(symbol))
            {
                return;
            }

            if (IsGeneric(symbol))
            {
                symbolContext.ReportDiagnostic(Diagnostic.Create(GenericComponent, Location(symbol), symbol.Name));
                return;
            }

            if (!IsAssemblyVisible(symbol))
            {
                symbolContext.ReportDiagnostic(Diagnostic.Create(NotAssemblyVisible, Location(symbol), symbol.Name));
            }

            if (symbol.TypeKind == TypeKind.Class)
            {
                if (!Implements(symbol, SerializableInterfaceFullName))
                {
                    symbolContext.ReportDiagnostic(
                        Diagnostic.Create(ManagedNotSerializable, Location(symbol), symbol.Name));
                }

                return;
            }

            // A tag carries no data, so there is nothing to write and nothing to warn about.
            if (symbol.TypeKind == TypeKind.Struct && HasInstanceFields(symbol))
            {
                candidates.Add(symbol);
            }
        }, SymbolKind.NamedType);

        context.RegisterCompilationEndAction(endContext =>
        {
            foreach (var symbol in candidates)
            {
                if (HasAttribute(symbol, AutoSerializationAttributeFullName))
                {
                    continue;
                }

                if (formatterTargets.ContainsKey(FullName(symbol)))
                {
                    continue;
                }

                endContext.ReportDiagnostic(Diagnostic.Create(UnserializableValue, Location(symbol), symbol.Name));
            }
        });
    }

    /// <summary>Formatter targets a referenced assembly advertises through [FormatterRegistration].</summary>
    private static IEnumerable<string> ReferencedFormatterTargets(Compilation compilation)
    {
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        foreach (var attribute in assembly.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != FormatterRegistrationAttributeFullName ||
                attribute.ConstructorArguments.Length < 1 ||
                attribute.ConstructorArguments[0].Value is not ITypeSymbol target)
            {
                continue;
            }

            yield return FullName(target);
        }
    }

    /// <summary>Formatter targets this type declares by being a [CustomFormatter] for them.</summary>
    private static IEnumerable<string> DeclaredFormatterTargets(INamedTypeSymbol symbol)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != CustomFormatterAttributeFullName ||
                attribute.ConstructorArguments.Length < 1 ||
                attribute.ConstructorArguments[0].Value is not ITypeSymbol target)
            {
                continue;
            }

            yield return FullName(target);
        }
    }

    private static string FullName(ITypeSymbol symbol)
    {
        return symbol.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private static Location Location(INamedTypeSymbol symbol)
    {
        return symbol.Locations.Length > 0 ? symbol.Locations[0] : Microsoft.CodeAnalysis.Location.None;
    }

    private static bool IsComponent(INamedTypeSymbol symbol)
    {
        return HasAttribute(symbol, ComponentAttributeFullName);
    }

    private static bool HasAttribute(INamedTypeSymbol symbol, string fullName)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == fullName)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Implements(INamedTypeSymbol symbol, string interfaceFullName)
    {
        return symbol.AllInterfaces.Any(i => i.ToDisplayString() == interfaceFullName);
    }

    private static bool HasInstanceFields(INamedTypeSymbol symbol)
    {
        return symbol.GetMembers().OfType<IFieldSymbol>().Any(f => !f.IsStatic && !f.IsConst);
    }

    private static bool IsGeneric(INamedTypeSymbol symbol)
    {
        for (var t = symbol; t != null; t = t.ContainingType)
        {
            if (t.Arity > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAssemblyVisible(INamedTypeSymbol symbol)
    {
        for (ISymbol? s = symbol; s is INamedTypeSymbol; s = s.ContainingType)
        {
            if (s.DeclaredAccessibility != Accessibility.Public && s.DeclaredAccessibility != Accessibility.Internal)
            {
                return false;
            }
        }

        return true;
    }
}