using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DivisionEngine.Generators.Serialization;

public readonly record struct SerializeFieldInfo(string Name, ResolvedTypeInfo TypeInfo, int Id, bool IsProperty)
{
    private const int IndentSize = 4;

    public void GenerateSerializerLines(StringBuilder builder, int indent)
    {
        GenerateSerializerLinesForField(builder, Id, $"\"{Name}\"u8", TypeInfo, $"obj.{Name}", "serializer", indent);
    }

    private static void GenerateSerializerLinesForField(StringBuilder builder, int id, string hint,
        ResolvedTypeInfo type, string accessor, string serializer, int indent)
    {
        switch (type.Kind)
        {
            case ResolvedTypeKind.Bool:
                builder.Append(' ', indent * IndentSize);
                builder.Append(serializer);
                builder.Append(".Bool(");
                builder.Append(id);
                builder.Append(", ");
                builder.Append(hint);
                builder.Append(", ");
                builder.Append(accessor);
                builder.AppendLine(");");
                return;

            case ResolvedTypeKind.I8:
                builder.Append(' ', indent * IndentSize);
                builder.Append(serializer);
                builder.Append(".I8(");
                builder.Append(id);
                builder.Append(", ");
                builder.Append(hint);
                builder.Append(", unchecked((sbyte)");
                builder.Append(accessor);
                builder.AppendLine("));");
                return;

            case ResolvedTypeKind.I16:
                builder.Append(' ', indent * IndentSize);
                builder.Append(serializer);
                builder.Append(".I16(");
                builder.Append(id);
                builder.Append(", ");
                builder.Append(hint);
                builder.Append(", unchecked((short)");
                builder.Append(accessor);
                builder.AppendLine("));");
                return;

            case ResolvedTypeKind.I32:
                builder.Append(' ', indent * IndentSize);
                builder.Append(serializer);
                builder.Append(".I32(");
                builder.Append(id);
                builder.Append(", ");
                builder.Append(hint);
                builder.Append(", unchecked((int)");
                builder.Append(accessor);
                builder.AppendLine("));");
                return;

            case ResolvedTypeKind.I64:
                builder.Append(' ', indent * IndentSize);
                builder.Append(serializer);
                builder.Append(".I64(");
                builder.Append(id);
                builder.Append(", ");
                builder.Append(hint);
                builder.Append(", unchecked((long)");
                builder.Append(accessor);
                builder.AppendLine("));");
                return;

            case ResolvedTypeKind.F32:
                builder.Append(' ', indent * IndentSize);
                builder.Append(serializer);
                builder.Append(".F32(");
                builder.Append(id);
                builder.Append(", ");
                builder.Append(hint);
                builder.Append(", ");
                builder.Append(accessor);
                builder.AppendLine(");");
                return;

            case ResolvedTypeKind.F64:
                builder.Append(' ', indent * IndentSize);
                builder.Append(serializer);
                builder.Append(".F64(");
                builder.Append(id);
                builder.Append(", ");
                builder.Append(hint);
                builder.Append(", ");
                builder.Append(accessor);
                builder.AppendLine(");");
                return;

            case ResolvedTypeKind.String:
                builder.Append(' ', indent * IndentSize);
                builder.Append("global::DivisionEngine.SerializerExtensions.Utf16(");
                builder.Append(serializer);
                builder.Append(", ");
                builder.Append(id);
                builder.Append(", ");
                builder.Append(hint);
                builder.Append(", ");
                builder.Append(accessor);
                builder.AppendLine(");");
                return;

            case ResolvedTypeKind.Array:
            {
                builder.Append(' ', indent * IndentSize);
                builder.AppendLine("{");

                builder.Append(' ', (indent + 1) * IndentSize);
                builder.Append(serializer);
                builder.Append(".BeginArray(");
                builder.Append(id);
                builder.Append(", ");
                builder.Append(hint);
                builder.AppendLine(");");

                builder.Append(' ', (indent + 1) * IndentSize);
                builder.Append("for (var i = 0; i < ");
                builder.Append(accessor);
                builder.AppendLine(".Length; i++)");

                builder.Append(' ', (indent + 1) * IndentSize);
                builder.AppendLine("{");

                GenerateSerializerLinesForField(builder, 0, "\"\"u8", type.ArrayElementType!, $"{accessor}[i]",
                    serializer, indent + 2);

                builder.Append(' ', (indent + 1) * IndentSize);
                builder.AppendLine("}");

                builder.Append(' ', (indent + 1) * IndentSize);
                builder.Append(serializer);
                builder.AppendLine(".EndArray();");

                builder.Append(' ', indent * IndentSize);
                builder.AppendLine("}");
                return;
            }

            case ResolvedTypeKind.Class:
                builder.Append(' ', indent * IndentSize);
                builder.Append(serializer);
                builder.Append(".ObjectReference(");
                builder.Append(id);
                builder.Append(", ");
                builder.Append(hint);
                builder.Append(", ");
                builder.Append(accessor);
                builder.AppendLine(");");
                return;

            case ResolvedTypeKind.Struct:
            {
                builder.Append(' ', indent * IndentSize);
                builder.AppendLine("{");

                builder.Append(' ', (indent + 1) * IndentSize);
                builder.Append(serializer);
                builder.Append(".BeginStruct(");
                builder.Append(id);
                builder.Append(", ");
                builder.Append(hint);
                builder.AppendLine(");");

                builder.Append(' ', (indent + 1) * IndentSize);
                builder.Append(type.Name);
                builder.Append(".Formatter.Serialize(inner, ref ");
                builder.Append(accessor);
                builder.AppendLine(");");

                builder.Append(' ', (indent + 1) * IndentSize);
                builder.Append(serializer);
                builder.AppendLine(".EndStruct();");

                builder.Append(' ', indent * IndentSize);
                builder.AppendLine("}");
                return;
            }

            default:
                builder.Append(' ', indent * IndentSize);
                builder.Append("// unsupported type: ");
                builder.Append(type.DisplayName);
                builder.Append(" (");
                builder.Append(type.Kind);
                builder.AppendLine(")");
                return;
        }
    }

    public void GenerateDeserializerLines(StringBuilder builder, int indent)
    {
        GenerateDeserializerLinesForField(builder, Id, $"\"{Name}\"u8", TypeInfo, $"obj.{Name}", "deserializer", indent);
    }

    private static void GenerateDeserializerLinesForField(StringBuilder builder, int id, string hint,
        ResolvedTypeInfo type, string accessor, string serializer, int indent)
    {
        switch (type.Kind)
        {
            case ResolvedTypeKind.Bool:
                builder.Append(' ', indent * IndentSize);
                builder.Append(accessor);
                builder.Append(" = ");
                builder.Append(serializer);
                builder.Append(".Bool(");
                builder.Append(id);
                builder.Append(", ");
                builder.Append(hint);
                builder.AppendLine(");");
                return;

            case ResolvedTypeKind.I8:
                builder.Append(' ', indent * IndentSize);
                builder.Append(accessor);
                builder.Append(" = unchecked((");
                builder.Append(type.DisplayName);
                builder.Append(")");
                builder.Append(serializer);
                builder.Append(".I8(");
                builder.Append(id);
                builder.Append(", ");
                builder.Append(hint);
                builder.AppendLine("));");
                return;

            case ResolvedTypeKind.I16:
                builder.Append(' ', indent * IndentSize);
                builder.Append(accessor);
                builder.Append(" = unchecked((");
                builder.Append(type.DisplayName);
                builder.Append(")");
                builder.Append(serializer);
                builder.Append(".I16(");
                builder.Append(id);
                builder.Append(", ");
                builder.Append(hint);
                builder.AppendLine("));");
                return;

            case ResolvedTypeKind.I32:
                builder.Append(' ', indent * IndentSize);
                builder.Append(accessor);
                builder.Append(" = unchecked((");
                builder.Append(type.DisplayName);
                builder.Append(")");
                builder.Append(serializer);
                builder.Append(".I32(");
                builder.Append(id);
                builder.Append(", ");
                builder.Append(hint);
                builder.AppendLine("));");
                return;

            case ResolvedTypeKind.I64:
                builder.Append(' ', indent * IndentSize);
                builder.Append(accessor);
                builder.Append(" = unchecked((");
                builder.Append(type.DisplayName);
                builder.Append(")");
                builder.Append(serializer);
                builder.Append(".I64(");
                builder.Append(id);
                builder.Append(", ");
                builder.Append(hint);
                builder.AppendLine("));");
                return;

            case ResolvedTypeKind.F32:
                builder.Append(' ', indent * IndentSize);
                builder.Append(accessor);
                builder.Append(" = ");
                builder.Append(serializer);
                builder.Append(".F32(");
                builder.Append(id);
                builder.Append(", ");
                builder.Append(hint);
                builder.AppendLine(");");
                return;

            case ResolvedTypeKind.F64:
                builder.Append(' ', indent * IndentSize);
                builder.Append(accessor);
                builder.Append(" = ");
                builder.Append(serializer);
                builder.Append(".F64(");
                builder.Append(id);
                builder.Append(", ");
                builder.Append(hint);
                builder.AppendLine(");");
                return;

            case ResolvedTypeKind.String:
                builder.Append(' ', indent * IndentSize);
                builder.Append(accessor);
                builder.Append(" = global::DivisionEngine.DeserializerExtensions.String(");
                builder.Append(serializer);
                builder.Append(", ");
                builder.Append(id);
                builder.Append(", ");
                builder.Append(hint);
                builder.AppendLine(");");
                return;

            case ResolvedTypeKind.Array:
            {
                builder.Append(' ', indent * IndentSize);
                builder.AppendLine("{");

                builder.Append(' ', (indent + 1) * IndentSize);
                builder.Append("if (");
                builder.Append(serializer);
                builder.Append(".TryBeginArray(");
                builder.Append(id);
                builder.Append(", ");
                builder.Append(hint);
                builder.AppendLine(", out int length))");

                builder.Append(' ', (indent + 1) * IndentSize);
                builder.AppendLine("{");

                builder.Append(' ', (indent + 2) * IndentSize);
                builder.Append(accessor);
                builder.Append(" = new ");
                builder.Append(type.ArrayElementType!.DisplayName);
                builder.AppendLine("[length];");

                builder.Append(' ', (indent + 2) * IndentSize);
                builder.Append("for (var i = 0; i < ");
                builder.Append(accessor);
                builder.AppendLine(".Length; i++)");

                builder.Append(' ', (indent + 2) * IndentSize);
                builder.AppendLine("{");

                GenerateDeserializerLinesForField(builder, 0, "\"\"u8", type.ArrayElementType!, $"{accessor}[i]",
                    serializer, indent + 3);

                builder.Append(' ', (indent + 2) * IndentSize);
                builder.AppendLine("}");

                builder.Append(' ', (indent + 2) * IndentSize);
                builder.Append(serializer);
                builder.AppendLine(".EndArray();");

                builder.Append(' ', (indent + 1) * IndentSize);
                builder.AppendLine("}");

                builder.Append(' ', indent * IndentSize);
                builder.AppendLine("}");
                return;
            }

            case ResolvedTypeKind.Class:
                builder.Append(' ', indent * IndentSize);
                builder.Append(accessor);
                builder.Append(" = ");
                builder.Append(serializer);
                builder.Append(".ObjectReference(");
                builder.Append(id);
                builder.Append(", ");
                builder.Append(hint);
                builder.AppendLine(");");
                return;

            case ResolvedTypeKind.Struct:
            {
                builder.Append(' ', indent * IndentSize);
                builder.AppendLine("{");

                builder.Append(' ', (indent + 1) * IndentSize);
                builder.Append("if (");
                builder.Append(serializer);
                builder.Append(".TryBeginStruct(");
                builder.Append(id);
                builder.Append(", ");
                builder.Append(hint);
                builder.AppendLine("))");

                builder.Append(' ', (indent + 1) * IndentSize);
                builder.AppendLine("{");

                builder.Append(' ', (indent + 2) * IndentSize);
                builder.Append(type.Name);
                builder.Append(".Formatter.Deserialize(inner, ref ");
                builder.Append(accessor);
                builder.AppendLine(");");

                builder.Append(' ', (indent + 2) * IndentSize);
                builder.Append(serializer);
                builder.AppendLine(".EndStruct();");

                builder.Append(' ', (indent + 1) * IndentSize);
                builder.AppendLine("}");

                builder.Append(' ', indent * IndentSize);
                builder.AppendLine("}");
                return;
            }

            default:
                builder.Append(' ', indent * IndentSize);
                builder.Append("// unsupported type: ");
                builder.Append(type.DisplayName);
                builder.Append(" (");
                builder.Append(type.Kind);
                builder.AppendLine(")");
                return;
        }
    }
}

public readonly record struct SerializationTargetInfo(
    string TypeName,
    string NamespaceName,
    bool IsStruct,
    EquatableArray<SerializeFieldInfo> Fields,
    EquatableArray<string> ContainingTypes)
{
    public readonly EquatableArray<string> ContainingTypes = ContainingTypes;
    public readonly EquatableArray<SerializeFieldInfo> Fields = Fields;
    public readonly bool IsStruct = IsStruct;
    public readonly string NamespaceName = NamespaceName;
    public readonly string TypeName = TypeName;
}

[Generator(LanguageNames.CSharp)]
public class AutoSerializationGenerator : IIncrementalGenerator
{
    private const string AutoSerializationAttributeFullName = "DivisionEngine.AutoSerializationAttribute";

    private static readonly DiagnosticDescriptor DuplicateIdDescriptor = new(
        "DIVSER001",
        "Duplicate serialization field ID",
        "Fields '{0}' and '{1}' in type '{2}' have the same serialization ID {3}. Use an explicit ID via [Serialize(id)] to resolve the collision.",
        "DivisionEngine.Serialization",
        DiagnosticSeverity.Error,
        true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var targets = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AutoSerializationAttributeFullName,
                static (node, _) => node is ClassDeclarationSyntax or StructDeclarationSyntax,
                static (ctx, ct) => GetTargetInfo(ctx.SemanticModel, ctx.TargetNode, ct))
            .Where(static info => info.HasValue)
            .Select(static (info, _) => info!.Value);

        context.RegisterSourceOutput(targets, static (spc, info) => Execute(spc, info));
    }

    private static SerializationTargetInfo? GetTargetInfo(
        SemanticModel semanticModel,
        SyntaxNode targetNode,
        CancellationToken ct)
    {
        if (targetNode is not TypeDeclarationSyntax typeDecl)
            return null;

        ct.ThrowIfCancellationRequested();

        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
        if (typeSymbol == null) return null;

        var isStruct = typeDecl is StructDeclarationSyntax;
        var typeName = typeDecl.Identifier.Text;
        var namespaceName = GetNamespace(typeDecl);

        // Collect containing types for nested type support (syntax-based)
        var containingTypes = ImmutableArray.CreateBuilder<string>();
        var parent = typeDecl.Parent;
        while (parent is TypeDeclarationSyntax outerType)
        {
            var keyword = outerType is StructDeclarationSyntax ? "struct" : "class";
            containingTypes.Insert(0, $"partial {keyword} {outerType.Identifier.Text}");
            parent = outerType.Parent;
        }

        // Collect fields with [Serialize] attribute from the full type hierarchy (semantic-based)
        var fields = CollectFieldsFromHierarchy(typeSymbol, ct);

        return new SerializationTargetInfo(
            typeName,
            namespaceName,
            isStruct,
            new EquatableArray<SerializeFieldInfo>(fields),
            new EquatableArray<string>(containingTypes.ToImmutable()));
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

    private const string SerializeAttributeFullName = "DivisionEngine.SerializeAttribute";

    /// <summary>
    /// Walks the type hierarchy from base to derived, collecting all fields marked with [Serialize].
    /// For auto-properties with [field: Serialize], the compiler-generated backing field carries the attribute;
    /// we detect this via <see cref="IFieldSymbol.IsImplicitlyDeclared"/> and use the property name.
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
        {
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

                // Determine ID: use explicitId if provided, otherwise hash the name
                int id;
                if (serializeAttr.ConstructorArguments.Length > 0 &&
                    !serializeAttr.ConstructorArguments[0].IsNull)
                {
                    id = (int)serializeAttr.ConstructorArguments[0].Value!;
                }
                else
                {
                    id = HashFieldName(name);
                }

                fields.Add(new SerializeFieldInfo(name, ResolvedTypeInfo.FromSymbol(field.Type), id, isProperty));
            }
        }

        return fields.ToImmutable();
    }

    /// <summary>
    /// FNV-1a 32-bit hash.
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

    private static void Execute(SourceProductionContext spc, SerializationTargetInfo info)
    {
        var fields = info.Fields.OrderBy(f => f.Id).ToArray();

        // Check for duplicate IDs
        var seenIds = new Dictionary<int, string>(fields.Length);
        var hasDuplicate = false;
        foreach (var field in fields)
            if (seenIds.TryGetValue(field.Id, out var existingName))
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    DuplicateIdDescriptor,
                    Location.None,
                    field.Name, existingName, info.TypeName, field.Id));
                hasDuplicate = true;
            }
            else
            {
                seenIds[field.Id] = field.Name;
            }

        if (hasDuplicate) return;

        var typeKeyword = info.IsStruct ? "struct" : "class";
        var serializableInterfaceName = info.IsStruct ? "IValueSerializable" : "ISerializable";
        var optionalRef = info.IsStruct ? "ref " : "";

        var serializeBodyBuilder = new StringBuilder();
        foreach (var field in fields) field.GenerateSerializerLines(serializeBodyBuilder, 3);
        var serializeBody = serializeBodyBuilder.ToString();

        var deserializeBodyBuilder = new StringBuilder();
        foreach (var field in fields) field.GenerateDeserializerLines(deserializeBodyBuilder, 3);
        var deserializeBody = deserializeBodyBuilder.ToString();

        var namespaceDecl = string.IsNullOrEmpty(info.NamespaceName)
            ? ""
            : $"namespace {info.NamespaceName};\n\n";

        var containingTypes = info.ContainingTypes.AsImmutableArray();
        var containingOpen = string.Join("\n", containingTypes.Select(c => $"{c}\n{{"));
        var containingClose = string.Join("\n", containingTypes.Select(_ => "}"));

        if (containingOpen.Length > 0) containingOpen += "\n";
        if (containingClose.Length > 0) containingClose = "\n" + containingClose;

        // lang=csharp
        var source = $$"""
                       // <auto-generated/>
                       #nullable enable

                       {{namespaceDecl}}{{containingOpen}}partial {{typeKeyword}} {{info.TypeName}} : global::DivisionEngine.{{serializableInterfaceName}}<{{info.TypeName}}>
                       {
                           public static global::DivisionEngine.IFormatter<{{info.TypeName}}> Formatter { get; } = new GeneratedFormatter();
                           private class GeneratedFormatter : global::DivisionEngine.IFormatter<{{info.TypeName}}>
                           {
                               public void Serialize<TSerializer>(TSerializer serializer, {{optionalRef}}{{info.TypeName}} obj) where TSerializer : global::DivisionEngine.ISerializer, allows ref struct
                               {
                       {{serializeBody}}
                               }

                               public void Deserialize<TDeserializer>(TDeserializer deserializer, {{optionalRef}}{{info.TypeName}} obj)where TDeserializer : global::DivisionEngine.IDeserializer, allows ref struct
                               {
                       {{deserializeBody}}
                               }
                           }
                       }{{containingClose}}
                       """;

        var hintName = string.IsNullOrEmpty(info.NamespaceName)
            ? $"{info.TypeName}.g.cs"
            : $"{info.NamespaceName}.{info.TypeName}.g.cs";

        spc.AddSource(hintName, source);
    }
}