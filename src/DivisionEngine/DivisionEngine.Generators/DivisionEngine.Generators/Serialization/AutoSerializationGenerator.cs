using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DivisionEngine.Generators.Serialization;

public readonly record struct SerializeFieldInfo(
    string Name,
    string HintName,
    ResolvedTypeInfo TypeInfo,
    int Id,
    bool IsProperty)
{
    public string SerializerLine(string root)
    {
        var acc = $"{root}.{Name}";
        var hint = $"\"{HintName}\"u8";
        return TypeInfo.Kind switch
        {
            ResolvedTypeKind.Bool => $"serializer.Bool({Id}, {hint}, {acc});",
            ResolvedTypeKind.I8 => $"serializer.I8({Id}, {hint}, unchecked((sbyte){acc}));",
            ResolvedTypeKind.I16 => $"serializer.I16({Id}, {hint}, unchecked((short){acc}));",
            ResolvedTypeKind.I32 => $"serializer.I32({Id}, {hint}, unchecked((int){acc}));",
            ResolvedTypeKind.I64 => $"serializer.I64({Id}, {hint}, unchecked((long){acc}));",
            ResolvedTypeKind.F32 => $"serializer.F32({Id}, {hint}, {acc});",
            ResolvedTypeKind.F64 => $"serializer.F64({Id}, {hint}, {acc});",
            ResolvedTypeKind.String =>
                $"global::DivisionEngine.SerializerExtensions.Utf16(ref serializer, {Id}, {hint}, {acc});",
            ResolvedTypeKind.Enum =>
                $"serializer.{IntegralMethod(TypeInfo.EnumUnderlyingKind)}({Id}, {hint}, unchecked(({IntegralCast(TypeInfo.EnumUnderlyingKind)}){acc}));",
            // properties cannot be passed with an explicit `in` modifier (CS8156); the compiler
            // creates a temporary when the modifier is omitted
            ResolvedTypeKind.Store =>
                $"global::DivisionEngine.FormatterStore<{TypeInfo.StoreTypeRef}>.Formatter.Serialize(ref serializer, {Id}, {hint}, {(IsProperty ? "" : "in ")}{acc});",
            _ => $"// unsupported: {acc}"
        };
    }

    public string DeserializerLine(string root)
    {
        var acc = $"{root}.{Name}";
        var hint = $"\"{HintName}\"u8";
        return TypeInfo.Kind switch
        {
            ResolvedTypeKind.Bool => $"{acc} = deserializer.Bool({Id}, {hint});",
            ResolvedTypeKind.I8 => $"{acc} = unchecked(({TypeInfo.StoreTypeRef})deserializer.I8({Id}, {hint}));",
            ResolvedTypeKind.I16 => $"{acc} = unchecked(({TypeInfo.StoreTypeRef})deserializer.I16({Id}, {hint}));",
            ResolvedTypeKind.I32 => $"{acc} = unchecked(({TypeInfo.StoreTypeRef})deserializer.I32({Id}, {hint}));",
            ResolvedTypeKind.I64 => $"{acc} = unchecked(({TypeInfo.StoreTypeRef})deserializer.I64({Id}, {hint}));",
            ResolvedTypeKind.F32 => $"{acc} = deserializer.F32({Id}, {hint});",
            ResolvedTypeKind.F64 => $"{acc} = deserializer.F64({Id}, {hint});",
            ResolvedTypeKind.String =>
                $"{acc} = global::DivisionEngine.DeserializerExtensions.String(ref deserializer, {Id}, {hint});",
            ResolvedTypeKind.Enum =>
                $"{acc} = unchecked(({TypeInfo.StoreTypeRef})deserializer.{IntegralMethod(TypeInfo.EnumUnderlyingKind)}({Id}, {hint}));",
            ResolvedTypeKind.Store =>
                $"{acc} = global::DivisionEngine.FormatterStore<{TypeInfo.StoreTypeRef}>.Formatter.Deserialize(ref deserializer, {Id}, {hint})!;",
            _ => $"// unsupported: {acc}"
        };
    }

    private static string IntegralMethod(ResolvedTypeKind kind)
    {
        return kind switch
        {
            ResolvedTypeKind.I8 => "I8",
            ResolvedTypeKind.I16 => "I16",
            ResolvedTypeKind.I64 => "I64",
            _ => "I32"
        };
    }

    private static string IntegralCast(ResolvedTypeKind kind)
    {
        return kind switch
        {
            ResolvedTypeKind.I8 => "sbyte",
            ResolvedTypeKind.I16 => "short",
            ResolvedTypeKind.I64 => "long",
            _ => "int"
        };
    }
}

public readonly record struct SerializationTargetInfo(
    string TypeName,
    string NamespaceName,
    string FullTypeRef,
    bool IsStruct,
    bool IsGeneric,
    EquatableArray<SerializeFieldInfo> Fields,
    EquatableArray<string> ContainingTypes)
{
    public readonly EquatableArray<string> ContainingTypes = ContainingTypes;
    public readonly EquatableArray<SerializeFieldInfo> Fields = Fields;
    public readonly string FullTypeRef = FullTypeRef;
    public readonly bool IsGeneric = IsGeneric;
    public readonly bool IsStruct = IsStruct;
    public readonly string NamespaceName = NamespaceName;
    public readonly string TypeName = TypeName;
}

public readonly record struct CustomFormatterInfo(
    string RegistrationClassName,
    string TargetTypeOfExpr,
    string FormatterTypeOfExpr,
    string TargetTypeRef,
    string FormatterTypeRef,
    string TargetRegistrationName,
    bool IsOpenGeneric);

[Generator(LanguageNames.CSharp)]
public class AutoSerializationGenerator : IIncrementalGenerator
{
    private const string AutoSerializationAttributeFullName = "DivisionEngine.AutoSerializationAttribute";
    private const string SerializeAttributeFullName = "DivisionEngine.SerializeAttribute";
    private const string CustomFormatterAttributeFullName = "DivisionEngine.CustomFormatterAttribute";
    private const string FormatterRegistrationAttributeFullName = "DivisionEngine.FormatterRegistrationAttribute";

    private static readonly DiagnosticDescriptor DuplicateIdDescriptor = new(
        "DIVSER001",
        "Duplicate serialization field ID",
        "Fields '{0}' and '{1}' in type '{2}' have the same serialization ID {3}. Use an explicit ID via [Serialize(id)] to resolve the collision.",
        "DivisionEngine.Serialization",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor UnresolvableFieldDescriptor = new(
        "DIVSER002",
        "No formatter for serialized field type",
        "Field '{0}' in type '{1}' has type '{2}' which has no registered formatter (missing: {3}). Annotate a formatter with [CustomFormatter(typeof(...))], mark the type with [AutoSerialization], or implement ISerializableObject.",
        "DivisionEngine.Serialization",
        DiagnosticSeverity.Error,
        true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var targets = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AutoSerializationAttributeFullName,
                static (node, _) => node is ClassDeclarationSyntax or StructDeclarationSyntax,
                static (ctx, ct) => GetTargetInfo(ctx.TargetSymbol, ctx.TargetNode, ct))
            .Where(static info => info.HasValue)
            .Select(static (info, _) => info!.Value);

        var customFormatters = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                CustomFormatterAttributeFullName,
                static (node, _) => node is ClassDeclarationSyntax or StructDeclarationSyntax,
                static (ctx, ct) => GetCustomFormatterInfos(ctx))
            .SelectMany(static (infos, _) => infos);

        var referencedNames = context.CompilationProvider.Select(static (compilation, ct) =>
        {
            var builder = ImmutableArray.CreateBuilder<string>();
            foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
            foreach (var attribute in assembly.GetAttributes())
            {
                ct.ThrowIfCancellationRequested();
                if (attribute.AttributeClass?.ToDisplayString() != FormatterRegistrationAttributeFullName) continue;
                if (attribute.ConstructorArguments.Length < 1) continue;
                if (attribute.ConstructorArguments[0].Value is not ITypeSymbol target) continue;
                builder.Add(target.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            }

            return new EquatableArray<string>(builder.ToImmutable());
        });

        var localFormatterNames = customFormatters
            .Select(static (info, _) => info.TargetRegistrationName)
            .Collect();

        var localStructNames = targets
            .Select(static (info, _) => info is { IsStruct: true, IsGeneric: false } ? info.FullTypeRef : "")
            .Collect();

        var registeredNames = referencedNames
            .Combine(localFormatterNames)
            .Combine(localStructNames)
            .Select(static (pair, _) =>
            {
                var ((referenced, local), structs) = pair;
                var merged = referenced
                    .Concat(local)
                    .Concat(structs)
                    .Where(static n => n.Length > 0)
                    .Distinct()
                    .OrderBy(static n => n, System.StringComparer.Ordinal)
                    .ToImmutableArray();
                return new EquatableArray<string>(merged);
            });

        context.RegisterSourceOutput(targets.Combine(registeredNames),
            static (spc, pair) => Execute(spc, pair.Left, pair.Right));

        context.RegisterSourceOutput(customFormatters,
            static (spc, info) => EmitFormatterRegistration(spc, info));
    }

    private static SerializationTargetInfo? GetTargetInfo(
        ISymbol targetSymbol,
        SyntaxNode targetNode,
        CancellationToken ct)
    {
        if (targetNode is not TypeDeclarationSyntax typeDecl) return null;
        if (targetSymbol is not INamedTypeSymbol typeSymbol) return null;

        ct.ThrowIfCancellationRequested();

        var isStruct = typeDecl is StructDeclarationSyntax;
        var typeName = typeDecl.Identifier.Text;
        var namespaceName = GetNamespace(typeDecl);

        var isGeneric = false;
        for (var t = typeSymbol; t != null; t = t.ContainingType)
            if (t.Arity > 0)
            {
                isGeneric = true;
                break;
            }

        // Collect containing types for nested type support (syntax-based)
        var containingTypes = ImmutableArray.CreateBuilder<string>();
        var parent = typeDecl.Parent;
        while (parent is TypeDeclarationSyntax outerType)
        {
            var keyword = outerType is StructDeclarationSyntax ? "struct" : "class";
            containingTypes.Insert(0, $"partial {keyword} {outerType.Identifier.Text}");
            parent = outerType.Parent;
        }

        var fields = CollectFieldsFromHierarchy(typeSymbol, ct);

        return new SerializationTargetInfo(
            typeName,
            namespaceName,
            typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            isStruct,
            isGeneric,
            new EquatableArray<SerializeFieldInfo>(fields),
            new EquatableArray<string>(containingTypes.ToImmutable()));
    }

    private static ImmutableArray<CustomFormatterInfo> GetCustomFormatterInfos(
        GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol formatterSymbol)
            return ImmutableArray<CustomFormatterInfo>.Empty;

        var builder = ImmutableArray.CreateBuilder<CustomFormatterInfo>();
        foreach (var attribute in ctx.Attributes)
        {
            if (attribute.ConstructorArguments.Length < 1) continue;
            if (attribute.ConstructorArguments[0].Value is not ITypeSymbol targetType) continue;

            var isOpenGeneric = targetType is INamedTypeSymbol { IsUnboundGenericType: true };

            string targetTypeOf, targetTypeRef, targetRegistrationName;
            if (targetType is INamedTypeSymbol named)
            {
                targetRegistrationName =
                    named.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                targetTypeOf = isOpenGeneric ? UnboundTypeOf(named.OriginalDefinition) : targetRegistrationName;
                targetTypeRef = isOpenGeneric ? "" : named.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            }
            else
            {
                // e.g. byte[] — never looked up by name (arrays recurse into elements)
                var display = targetType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                targetRegistrationName = display;
                targetTypeOf = display;
                targetTypeRef = display;
            }

            var formatterTypeOf = formatterSymbol.Arity > 0
                ? UnboundTypeOf(formatterSymbol.OriginalDefinition)
                : formatterSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            builder.Add(new CustomFormatterInfo(
                Sanitize($"{formatterSymbol.ToDisplayString()}_{targetType.ToDisplayString()}"),
                targetTypeOf,
                formatterTypeOf,
                targetTypeRef,
                formatterSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                targetRegistrationName,
                isOpenGeneric));
        }

        return builder.ToImmutable();
    }

    private static string UnboundTypeOf(INamedTypeSymbol originalDefinition)
    {
        var display = originalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var open = display.IndexOf('<');
        var baseName = open >= 0 ? display.Substring(0, open) : display;
        return $"{baseName}<{new string(',', originalDefinition.Arity - 1)}>";
    }

    private static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
            builder.Append(char.IsLetterOrDigit(c) ? c : '_');
        return builder.ToString();
    }

    private static string GetNamespace(SyntaxNode node)
    {
        var parent = node.Parent;
        while (parent != null)
        {
            switch (parent)
            {
                case FileScopedNamespaceDeclarationSyntax fileScopedNs:
                    return fileScopedNs.Name.ToString();
                case NamespaceDeclarationSyntax ns:
                    return ns.Name.ToString();
            }

            parent = parent.Parent;
        }

        return "";
    }

    /// <summary>
    ///     Walks the type hierarchy from base to derived, collecting all fields marked with [Serialize].
    ///     For auto-properties with [field: Serialize], the compiler-generated backing field carries the attribute;
    ///     we detect this via <see cref="IFieldSymbol.IsImplicitlyDeclared" /> and use the property name.
    /// </summary>
    private static ImmutableArray<SerializeFieldInfo> CollectFieldsFromHierarchy(
        INamedTypeSymbol typeSymbol,
        CancellationToken ct)
    {
        var fields = ImmutableArray.CreateBuilder<SerializeFieldInfo>();

        // Build base-first ordering
        var typeChain = new List<INamedTypeSymbol>();
        var current = typeSymbol;
        while (current != null && current.SpecialType != SpecialType.System_Object)
        {
            typeChain.Add(current);
            current = current.BaseType;
        }

        typeChain.Reverse();

        foreach (var type in typeChain)
        foreach (var member in type.GetMembers())
        {
            ct.ThrowIfCancellationRequested();

            if (member is not IFieldSymbol field) continue;

            var serializeAttr = field.GetAttributes()
                .FirstOrDefault(a =>
                    a.AttributeClass?.ToDisplayString() == SerializeAttributeFullName);
            if (serializeAttr == null) continue;

            // Determine name: backing fields of auto-properties use the property name
            string name;
            bool isProperty;
            if (field.IsImplicitlyDeclared && field.AssociatedSymbol is IPropertySymbol prop)
            {
                name = prop.Name;
                isProperty = true;
            }
            else
            {
                name = field.Name;
                isProperty = false;
            }

            // [Serialize], [Serialize(id)], [Serialize(name)], [Serialize(id, name)]
            int? explicitId = null;
            string? explicitName = null;
            foreach (var arg in serializeAttr.ConstructorArguments)
                switch (arg.Value)
                {
                    case int i:
                        explicitId = i;
                        break;
                    case string s:
                        explicitName = s;
                        break;
                }

            var hintName = explicitName ?? name;
            var id = explicitId ?? HashFieldName(hintName);

            fields.Add(new SerializeFieldInfo(name, hintName, ResolvedTypeInfo.FromSymbol(field.Type), id,
                isProperty));
        }

        return fields.ToImmutable();
    }

    /// <summary>
    ///     FNV-1a 32-bit hash.
    /// </summary>
    private static int HashFieldName(string name)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in name)
            {
                hash ^= c;
                hash *= 16777619u;
            }

            return (int)hash;
        }
    }

    private static void Execute(SourceProductionContext spc, SerializationTargetInfo info,
        EquatableArray<string> registeredNames)
    {
        var fields = info.Fields.OrderBy(f => f.Id).ToArray();

        // Check for duplicate IDs
        var seenIds = new Dictionary<int, string>(fields.Length);
        var hasError = false;
        foreach (var field in fields)
            if (seenIds.TryGetValue(field.Id, out var existingName))
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    DuplicateIdDescriptor,
                    Location.None,
                    field.Name, existingName, info.TypeName, field.Id));
                hasError = true;
            }
            else
            {
                seenIds[field.Id] = field.Name;
            }

        // Check formatter resolvability
        var registered = new HashSet<string>(registeredNames);
        foreach (var field in fields)
        {
            if (field.TypeInfo.Kind == ResolvedTypeKind.Unsupported)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    UnresolvableFieldDescriptor,
                    Location.None,
                    field.Name, info.TypeName, field.TypeInfo.DisplayName, field.TypeInfo.DisplayName));
                hasError = true;
                continue;
            }

            if (field.TypeInfo.Kind != ResolvedTypeKind.Store) continue;

            var missing = field.TypeInfo.RequiredFormatters
                .Where(n => !registered.Contains(n))
                .ToArray();
            if (missing.Length == 0) continue;

            spc.ReportDiagnostic(Diagnostic.Create(
                UnresolvableFieldDescriptor,
                Location.None,
                field.Name, info.TypeName, field.TypeInfo.DisplayName, string.Join(", ", missing)));
            hasError = true;
        }

        if (hasError) return;

        var source = info.IsStruct ? GenerateStruct(info, fields) : GenerateClass(info, fields);

        var hintName = string.IsNullOrEmpty(info.NamespaceName)
            ? $"{info.TypeName}.g.cs"
            : $"{info.NamespaceName}.{info.TypeName}.g.cs";

        spc.AddSource(hintName, source);
    }

    private static string BuildBody(SerializeFieldInfo[] fields, string root, bool serialize, int indent)
    {
        var builder = new StringBuilder();
        var pad = new string(' ', indent);
        foreach (var field in fields)
        {
            builder.Append(pad);
            builder.AppendLine(serialize ? field.SerializerLine(root) : field.DeserializerLine(root));
        }

        return builder.ToString().TrimEnd('\r', '\n');
    }

    private static string GenerateClass(SerializationTargetInfo info, SerializeFieldInfo[] fields)
    {
        var namespaceDecl = string.IsNullOrEmpty(info.NamespaceName)
            ? ""
            : $"namespace {info.NamespaceName};\n\n";

        var (containingOpen, containingClose) = BuildContaining(info);

        return $$"""
                 // <auto-generated/>
                 #nullable enable
                 #nullable disable warnings

                 {{namespaceDecl}}{{containingOpen}}partial class {{info.TypeName}} : global::DivisionEngine.ISerializable
                 {
                     void global::DivisionEngine.ISerializable.Serialize<TSerializer>(ref TSerializer serializer)
                     {
                 {{BuildBody(fields, "this", true, 8)}}
                     }

                     void global::DivisionEngine.ISerializable.Deserialize<TDeserializer>(ref TDeserializer deserializer)
                     {
                 {{BuildBody(fields, "this", false, 8)}}
                     }
                 }{{containingClose}}
                 """;
    }

    private static string GenerateStruct(SerializationTargetInfo info, SerializeFieldInfo[] fields)
    {
        var namespaceDecl = string.IsNullOrEmpty(info.NamespaceName)
            ? ""
            : $"namespace {info.NamespaceName};\n\n";

        var (containingOpen, containingClose) = BuildContaining(info);

        var registration = info.IsGeneric
            ? ""
            : $$"""


                internal static class {{Sanitize(info.FullTypeRef)}}_FormatterRegistration
                {
                    [global::System.Runtime.CompilerServices.ModuleInitializer]
                    internal static void Register()
                    {
                        global::DivisionEngine.FormatterRegistry.Register<{{info.FullTypeRef}}>({{info.FullTypeRef}}.Formatter);
                    }
                }
                """;

        var assemblyAttribute = info.IsGeneric
            ? ""
            : $"[assembly: global::DivisionEngine.FormatterRegistration(typeof({info.FullTypeRef}), typeof({info.FullTypeRef}))]\n\n";

        return $$"""
                 // <auto-generated/>
                 #nullable enable
                 #nullable disable warnings

                 {{assemblyAttribute}}{{namespaceDecl}}{{containingOpen}}partial struct {{info.TypeName}}
                 {
                     public static global::DivisionEngine.IValueFormatter<{{info.FullTypeRef}}> Formatter { get; } = new GeneratedFormatter();

                     private sealed class GeneratedFormatter : global::DivisionEngine.IValueFormatter<{{info.FullTypeRef}}>
                     {
                         public void Serialize<TSerializer>(ref TSerializer serializer, int id, global::System.ReadOnlySpan<byte> hintUtf8, in {{info.FullTypeRef}} value)
                             where TSerializer : global::DivisionEngine.ISerializer, allows ref struct
                         {
                             serializer.BeginStruct(id, hintUtf8);
                 {{BuildBody(fields, "value", true, 12)}}
                             serializer.EndStruct();
                         }

                         public {{info.FullTypeRef}} Deserialize<TDeserializer>(ref TDeserializer deserializer, int id, global::System.ReadOnlySpan<byte> hintUtf8)
                             where TDeserializer : global::DivisionEngine.IDeserializer, allows ref struct
                         {
                             var obj = default({{info.FullTypeRef}});
                             if (deserializer.TryBeginStruct(id, hintUtf8))
                             {
                 {{BuildBody(fields, "obj", false, 16)}}
                                 deserializer.EndStruct();
                             }
                             return obj;
                         }
                     }
                 }{{containingClose}}{{registration}}
                 """;
    }

    private static (string Open, string Close) BuildContaining(SerializationTargetInfo info)
    {
        var containingTypes = info.ContainingTypes.AsImmutableArray();
        var open = string.Join("\n", containingTypes.Select(c => $"{c}\n{{"));
        var close = string.Join("\n", containingTypes.Select(_ => "}"));

        if (open.Length > 0) open += "\n";
        if (close.Length > 0) close = "\n" + close;

        return (open, close);
    }

    private static void EmitFormatterRegistration(SourceProductionContext spc, CustomFormatterInfo info)
    {
        var registration = info.IsOpenGeneric
            ? $"global::DivisionEngine.FormatterRegistry.RegisterFactory(typeof({info.TargetTypeOfExpr}), typeof({info.FormatterTypeOfExpr}));"
            : $"global::DivisionEngine.FormatterRegistry.Register<{info.TargetTypeRef}>(new {info.FormatterTypeRef}());";

        var source = $$"""
                       // <auto-generated/>
                       #nullable enable

                       [assembly: global::DivisionEngine.FormatterRegistration(typeof({{info.TargetTypeOfExpr}}), typeof({{info.FormatterTypeOfExpr}}))]

                       namespace DivisionEngine.Generated
                       {
                           internal static class {{info.RegistrationClassName}}_Registration
                           {
                               [global::System.Runtime.CompilerServices.ModuleInitializer]
                               internal static void Register()
                               {
                                   {{registration}}
                               }
                           }
                       }
                       """;

        spc.AddSource($"{info.RegistrationClassName}.Registration.g.cs", source);
    }
}
