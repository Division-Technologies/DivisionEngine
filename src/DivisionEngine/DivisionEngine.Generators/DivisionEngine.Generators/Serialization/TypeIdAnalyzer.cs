using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DivisionEngine.Generators.Serialization;

/// <summary>
///     Flags serializable classes whose serialized type ID is derived from the type name, so the
///     accompanying code fix can pin the current ID with [TypeId] before a rename.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TypeIdAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "DIVSER005";

    private const string AutoSerializationAttributeFullName = "DivisionEngine.AutoSerializationAttribute";
    private const string TypeIdAttributeFullName = "DivisionEngine.TypeIdAttribute";
    private const string SerializableInterfaceFullName = "DivisionEngine.ISerializable";

    private static readonly DiagnosticDescriptor Descriptor = new(
        DiagnosticId,
        "Serialized type ID is derived from the type name",
        "Class '{0}' derives its serialized type ID from its name; apply [TypeId] to pin the ID so the class can be renamed without breaking persisted data",
        "DivisionEngine.Serialization",
        DiagnosticSeverity.Info,
        true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptor);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(Analyze, SymbolKind.NamedType);
    }

    private static void Analyze(SymbolAnalysisContext context)
    {
        // Only concrete classes are object-framed (instantiated by ID during deserialization).
        if (context.Symbol is not INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } symbol)
        {
            return;
        }

        for (var t = symbol; t != null; t = t.ContainingType)
        {
            if (t.Arity > 0)
            {
                return;
            }
        }

        var isSerializable = false;
        foreach (var attribute in symbol.GetAttributes())
        {
            var name = attribute.AttributeClass?.ToDisplayString();
            if (name == TypeIdAttributeFullName)
            {
                return;
            }

            if (name == AutoSerializationAttributeFullName)
            {
                isSerializable = true;
            }
        }

        if (!isSerializable)
        {
            foreach (var implemented in symbol.AllInterfaces)
            {
                if (implemented.ToDisplayString() == SerializableInterfaceFullName)
                {
                    isSerializable = true;
                    break;
                }
            }
        }

        if (!isSerializable)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Descriptor, symbol.Locations[0], symbol.Name));
    }
}