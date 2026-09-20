using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace DivisionEngine.Generators.Entities;

/// <summary>
///     What a <c>[Component]</c> type needs registered, flattened to strings so the incremental
///     pipeline can compare it cheaply.
/// </summary>
public readonly record struct ComponentRegistrationInfo(
    string FullTypeRef,
    string SafeName,
    bool RegisterValueSerializer,
    EquatableArray<string> EntityFieldPaths);

/// <summary>
///     Emits a <c>[ModuleInitializer]</c> per <c>[Component]</c> type so it enters
///     <c>ComponentTypeRegistry</c> as soon as its assembly loads.
///     <para>
///         Registration cannot wait until some code mentions <c>ComponentType&lt;T&gt;</c>: loading a
///         scene has only the persisted type id to go on, and turning that back into a live component
///         type would otherwise mean constructing the generic type reflectively, which the engine
///         avoids for AOT compatibility.
///     </para>
///     <para>
///         The same initializer declares the type's <c>Entity</c> fields, which is what lets command
///         buffer playback and scene loading rewrite references to the entities they actually created.
///     </para>
/// </summary>
[Generator]
public sealed class ComponentRegistrationGenerator : IIncrementalGenerator
{
    private const string ComponentAttributeFullName = "DivisionEngine.ComponentAttribute";
    private const string AutoSerializationAttributeFullName = "DivisionEngine.AutoSerializationAttribute";
    private const string EntityFullName = "DivisionEngine.Entity";

    /// <summary>Depth limit for the walk into nested structs looking for entity fields.</summary>
    private const int MaxNesting = 8;

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var components = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ComponentAttributeFullName,
                static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax,
                static (ctx, _) => ctx.TargetSymbol is INamedTypeSymbol symbol ? Describe(symbol) : null)
            .Where(static info => info.HasValue)
            .Select(static (info, _) => info!.Value);

        context.RegisterSourceOutput(components, static (spc, info) => Emit(spc, info));
    }

    private static ComponentRegistrationInfo? Describe(INamedTypeSymbol symbol)
    {
        // A generated module initializer lives at assembly scope, so it can only name a type the
        // whole assembly can see. Types nested inside a private scope still register the moment code
        // mentions them, which is enough for types only that code can construct anyway.
        if (!IsAssemblyVisible(symbol) || symbol.IsGenericType)
        {
            return null;
        }

        var isUnmanagedStruct = symbol.IsValueType && symbol.IsUnmanagedType;
        var hasFields = InstanceFields(symbol).Any();
        var isSerializable = symbol.GetAttributes().Any(static a =>
            a.AttributeClass?.ToDisplayString() == AutoSerializationAttributeFullName);

        var entityFields = new List<string>();
        if (isUnmanagedStruct)
        {
            CollectEntityFields(symbol, "value", entityFields, 0);
        }

        return new ComponentRegistrationInfo(
            symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            SafeName(symbol),
            // A tag has no storage to serialize, and a component with no formatter would only fail
            // later; both are left without a value serializer on purpose.
            isUnmanagedStruct && hasFields && isSerializable,
            new EquatableArray<string>(entityFields.ToImmutableArray()));
    }

    private static bool IsAssemblyVisible(INamedTypeSymbol symbol)
    {
        for (ISymbol? s = symbol; s is INamedTypeSymbol; s = s.ContainingType)
        {
            if (s.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<IFieldSymbol> InstanceFields(INamedTypeSymbol symbol)
    {
        return symbol.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(static f => !f.IsStatic && !f.IsConst);
    }

    /// <summary>
    ///     Records an access path per <c>Entity</c> field, descending into nested structs so an entity
    ///     held one or more structs deep is still rewritten.
    /// </summary>
    private static void CollectEntityFields(INamedTypeSymbol symbol, string path, List<string> into, int depth)
    {
        if (depth > MaxNesting)
        {
            return;
        }

        foreach (var field in InstanceFields(symbol))
        {
            // A private field of a nested struct cannot be reached from generated code.
            if (field.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
            {
                continue;
            }

            var fieldType = field.Type;
            if (fieldType.ToDisplayString() == EntityFullName)
            {
                into.Add($"{path}.{field.Name}");
            }
            else if (fieldType is INamedTypeSymbol { IsValueType: true } nested && !nested.IsGenericType)
            {
                CollectEntityFields(nested, $"{path}.{field.Name}", into, depth + 1);
            }
        }
    }

    private static string SafeName(INamedTypeSymbol symbol)
    {
        var builder = new StringBuilder();
        foreach (var c in symbol.ToDisplayString())
        {
            builder.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        return builder.ToString();
    }

    private static void Emit(SourceProductionContext spc, ComponentRegistrationInfo info)
    {
        var body = new StringBuilder();
        body.Append($"            global::DivisionEngine.ComponentTypeRegistry.RegisterComponent<{info.FullTypeRef}>();");

        if (info.RegisterValueSerializer)
        {
            body.AppendLine();
            body.Append(
                $"            global::DivisionEngine.ComponentTypeRegistry.RegisterValueSerializer<{info.FullTypeRef}>();");
        }

        var paths = info.EntityFieldPaths.AsImmutableArray();
        if (paths.Length > 0)
        {
            var assignments = string.Join("\n", paths.Select(static p => $"                    {p} = map.Resolve({p});"));
            body.AppendLine();
            body.Append($$"""
                                      global::DivisionEngine.ComponentTypeRegistry.RegisterEntityFields<{{info.FullTypeRef}}>(
                                          static (ref {{info.FullTypeRef}} value, global::DivisionEngine.EntityRemap map) =>
                                          {
                          {{assignments}}
                                          });
                          """);
        }

        var source = $$"""
                       // <auto-generated/>
                       #nullable enable

                       namespace DivisionEngine.Generated
                       {
                           internal static class {{info.SafeName}}_ComponentRegistration
                           {
                               [global::System.Runtime.CompilerServices.ModuleInitializer]
                               internal static void Register()
                               {
                       {{body}}
                               }
                           }
                       }
                       """;

        spc.AddSource($"{info.SafeName}.ComponentRegistration.g.cs", source);
    }
}
