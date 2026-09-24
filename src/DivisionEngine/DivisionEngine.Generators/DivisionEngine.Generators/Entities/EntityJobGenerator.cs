using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DivisionEngine.Generators.Entities;

/// <summary>How one <c>Execute</c> parameter is fed each iteration.</summary>
public enum EntityJobParameterKind
{
    WriteComponent,
    ReadComponent,
    Entity,
    JobContext
}

/// <param name="Modifier">What the argument is passed with: <c>"ref "</c>, <c>"in "</c> or nothing.</param>
public readonly record struct EntityJobParameter(EntityJobParameterKind Kind, string TypeRef, string Modifier);

public readonly record struct EntityJobInfo(
    string TypeName,
    string SafeName,
    string TypeKeyword,
    string FullTypeRef,
    string Namespace,
    string ContainingOpen,
    string ContainingClose,
    EquatableArray<EntityJobParameter> Parameters,
    EquatableArray<string> All,
    EquatableArray<string> Any,
    EquatableArray<string> None);

/// <summary>
///     Turns a <c>[EntityJob]</c> struct's <c>Execute</c> signature into the query, the access set and
///     the chunk loop that go with it — the three restatements of the same component list collapsed
///     into one.
/// </summary>
[Generator]
public sealed class EntityJobGenerator : IIncrementalGenerator
{
    private const string EntityJobAttributeFullName = "DivisionEngine.EntityJobAttribute";
    private const string EntityFullName = "DivisionEngine.Entity";
    private const string JobContextFullName = "DivisionEngine.JobContext";

    private static readonly DiagnosticDescriptor MissingExecute = new(
        "DIVENT005",
        "Entity job has no Execute method",
        "'{0}' is marked [EntityJob] but declares no Execute method; add one taking the components it works on as 'ref' (write) or 'in' (read) parameters",
        "DivisionEngine.Entities",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor UnsupportedParameter = new(
        "DIVENT006",
        "Entity job parameter is not supported",
        "Parameter '{0}' of '{1}.Execute' is not supported; use 'ref T' or 'in T' for components, 'Entity' or 'in Entity' for the entity, or 'JobContext' or 'in JobContext' for the frame",
        "DivisionEngine.Entities",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor MatchesEverything = new(
        "DIVENT007",
        "Entity job would match every entity",
        "'{0}' names no components, so it would run on every entity; take at least one component parameter or add [WithAll]",
        "DivisionEngine.Entities",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor NotPartial = new(
        "DIVENT008",
        "Entity job must be partial",
        "'{0}' is marked [EntityJob] but is not declared partial, so its Schedule method cannot be generated",
        "DivisionEngine.Entities",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor UnsupportedShape = new(
        "DIVENT009",
        "Entity job shape is not supported",
        "'{0}' cannot be an entity job: {1}",
        "DivisionEngine.Entities",
        DiagnosticSeverity.Error,
        true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var jobs = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                EntityJobAttributeFullName,
                static (node, _) => node is StructDeclarationSyntax
                    or RecordDeclarationSyntax { ClassOrStructKeyword.RawKind: (int)SyntaxKind.StructKeyword },
                static (ctx, _) => Describe(ctx));

        context.RegisterSourceOutput(jobs, static (spc, result) =>
        {
            foreach (var diagnostic in result.Diagnostics.AsImmutableArray())
            {
                spc.ReportDiagnostic(Diagnostic.Create(diagnostic.Descriptor, null,
                    diagnostic.Arguments.AsImmutableArray().ToArray()));
            }

            if (result.Info is { } info)
            {
                spc.AddSource($"{info.SafeName}.EntityJob.g.cs", Emit(info));
            }
        });
    }

    private static DescribeResult Describe(GeneratorAttributeSyntaxContext ctx)
    {
        var diagnostics = new List<PendingDiagnostic>();

        if (ctx.TargetSymbol is not INamedTypeSymbol symbol || ctx.TargetNode is not TypeDeclarationSyntax syntax)
        {
            return new DescribeResult(null, Pack(diagnostics));
        }

        var name = symbol.Name;

        if (!syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
        {
            diagnostics.Add(new PendingDiagnostic(NotPartial, Pack([name])));
            return new DescribeResult(null, Pack(diagnostics));
        }

        if (UnsupportedShapeReason(symbol) is { } reason)
        {
            diagnostics.Add(new PendingDiagnostic(UnsupportedShape, Pack([name, reason])));
            return new DescribeResult(null, Pack(diagnostics));
        }

        var execute = symbol.GetMembers("Execute")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m => !m.IsStatic);

        if (execute is null)
        {
            diagnostics.Add(new PendingDiagnostic(MissingExecute, Pack([name])));
            return new DescribeResult(null, Pack(diagnostics));
        }

        if (execute.IsGenericMethod)
        {
            diagnostics.Add(new PendingDiagnostic(UnsupportedShape,
                Pack([name, "its Execute method is generic, so the components it works on are not known"])));
            return new DescribeResult(null, Pack(diagnostics));
        }

        var parameters = new List<EntityJobParameter>();
        var failed = false;
        foreach (var parameter in execute.Parameters)
        {
            var typeRef = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var typeName = parameter.Type.ToDisplayString();

            // The entity and the frame are handed over read-only, so they can be taken by value or
            // by readonly reference, but never as something Execute could write back to.
            if (typeName is EntityFullName or JobContextFullName)
            {
                var kind = typeName == EntityFullName
                    ? EntityJobParameterKind.Entity
                    : EntityJobParameterKind.JobContext;
                switch (parameter.RefKind)
                {
                    case RefKind.None:
                        parameters.Add(new EntityJobParameter(kind, typeRef, ""));
                        break;
                    case RefKind.In or RefKind.RefReadOnlyParameter:
                        parameters.Add(new EntityJobParameter(kind, typeRef, "in "));
                        break;
                    default:
                        diagnostics.Add(new PendingDiagnostic(UnsupportedParameter, Pack([parameter.Name, name])));
                        failed = true;
                        break;
                }

                continue;
            }

            switch (parameter.RefKind)
            {
                case RefKind.Ref:
                    parameters.Add(new EntityJobParameter(EntityJobParameterKind.WriteComponent, typeRef, "ref "));
                    break;
                case RefKind.In or RefKind.RefReadOnlyParameter:
                    parameters.Add(new EntityJobParameter(EntityJobParameterKind.ReadComponent, typeRef, "in "));
                    break;
                default:
                    diagnostics.Add(new PendingDiagnostic(UnsupportedParameter, Pack([parameter.Name, name])));
                    failed = true;
                    break;
            }
        }

        if (failed)
        {
            return new DescribeResult(null, Pack(diagnostics));
        }

        var all = TypesFrom(symbol, "DivisionEngine.WithAllAttribute");
        var any = TypesFrom(symbol, "DivisionEngine.WithAnyAttribute");
        var none = TypesFrom(symbol, "DivisionEngine.WithNoneAttribute");

        var componentCount = parameters.Count(p =>
            p.Kind is EntityJobParameterKind.ReadComponent or EntityJobParameterKind.WriteComponent);
        if (componentCount == 0 && all.Count == 0)
        {
            diagnostics.Add(new PendingDiagnostic(MatchesEverything, Pack([name])));
            return new DescribeResult(null, Pack(diagnostics));
        }

        var (open, close) = Containing(symbol);
        var info = new EntityJobInfo(
            name,
            SafeName(symbol),
            symbol.IsRecord ? "record struct" : "struct",
            symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            symbol.ContainingNamespace.IsGlobalNamespace ? "" : symbol.ContainingNamespace.ToDisplayString(),
            open,
            close,
            Pack(parameters),
            Pack(all),
            Pack(any),
            Pack(none));

        return new DescribeResult(info, Pack(diagnostics));
    }

    private static List<string> TypesFrom(INamedTypeSymbol symbol, string attributeFullName)
    {
        var result = new List<string>();
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != attributeFullName)
            {
                continue;
            }

            foreach (var argument in attribute.ConstructorArguments)
            {
                foreach (var value in argument.Kind == TypedConstantKind.Array ? argument.Values : [argument])
                {
                    if (value.Value is ITypeSymbol type)
                    {
                        result.Add(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    ///     Why the generated partial could not compile for this type, or null when it can. Each of these
    ///     is caught here so the error names the declaration rather than surfacing in generated code.
    /// </summary>
    private static string? UnsupportedShapeReason(INamedTypeSymbol symbol)
    {
        if (symbol.Arity > 0)
        {
            // The query and access set are static fields per closed type, built from component types
            // the generator would have to name without knowing the type arguments.
            return "generic job structs are not supported";
        }

        if (symbol.IsRefLikeType)
        {
            return "a ref struct cannot be captured by the scheduled chunk job";
        }

        for (var t = symbol.ContainingType; t != null; t = t.ContainingType)
        {
            if (t.Arity > 0)
            {
                return $"it is nested in the generic type '{t.Name}', which is not supported";
            }

            var isPartial = t.DeclaringSyntaxReferences.All(r =>
                r.GetSyntax() is TypeDeclarationSyntax declaration
                && declaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)));
            if (!isPartial)
            {
                return
                    $"its containing type '{t.Name}' is not declared partial, so the generated code cannot be placed in it";
            }
        }

        return null;
    }

    /// <summary>The full name made file-name safe, so same-named jobs in different scopes do not collide.</summary>
    private static string SafeName(INamedTypeSymbol symbol)
    {
        var builder = new StringBuilder();
        foreach (var c in symbol.ToDisplayString())
        {
            builder.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        return builder.ToString();
    }

    private static string Keyword(INamedTypeSymbol type)
    {
        return type.TypeKind switch
        {
            TypeKind.Struct => type.IsRecord ? "record struct" : "struct",
            TypeKind.Interface => "interface",
            _ => type.IsRecord ? "record" : "class"
        };
    }

    /// <summary>Reopens the type's containing types so the generated partial lands in the same place.</summary>
    private static (string Open, string Close) Containing(INamedTypeSymbol symbol)
    {
        var parents = new List<string>();
        for (var t = symbol.ContainingType; t != null; t = t.ContainingType)
        {
            parents.Insert(0, $"partial {Keyword(t)} {t.Name}");
        }

        if (parents.Count == 0)
        {
            return ("", "");
        }

        return (string.Join("\n", parents.Select(p => p + "\n{")) + "\n",
            "\n" + string.Join("\n", parents.Select(_ => "}")));
    }

    private static EquatableArray<T> Pack<T>(IEnumerable<T> items) where T : IEquatable<T>
    {
        return new EquatableArray<T>(items.ToImmutableArray());
    }

    private static string Emit(EntityJobInfo info)
    {
        var parameters = info.Parameters.AsImmutableArray();

        var queryTypes = new List<string>();
        var accessCalls = new List<string>();
        var spanDeclarations = new List<string>();
        var arguments = new List<string>();
        var needsEntities = false;

        var spanIndex = 0;
        foreach (var parameter in parameters)
        {
            switch (parameter.Kind)
            {
                case EntityJobParameterKind.WriteComponent:
                    queryTypes.Add(parameter.TypeRef);
                    accessCalls.Add($".Write<{parameter.TypeRef}>()");
                    spanDeclarations.Add($"var __s{spanIndex} = __chunk.GetSpan<{parameter.TypeRef}>();");
                    arguments.Add($"{parameter.Modifier}__s{spanIndex}[__i]");
                    spanIndex++;
                    break;
                case EntityJobParameterKind.ReadComponent:
                    queryTypes.Add(parameter.TypeRef);
                    accessCalls.Add($".Read<{parameter.TypeRef}>()");
                    spanDeclarations.Add($"var __s{spanIndex} = __chunk.GetReadOnlySpan<{parameter.TypeRef}>();");
                    arguments.Add($"{parameter.Modifier}__s{spanIndex}[__i]");
                    spanIndex++;
                    break;
                case EntityJobParameterKind.Entity:
                    needsEntities = true;
                    arguments.Add($"{parameter.Modifier}__entities[__i]");
                    break;
                case EntityJobParameterKind.JobContext:
                    arguments.Add($"{parameter.Modifier}__job");
                    break;
            }
        }

        foreach (var type in info.All.AsImmutableArray())
        {
            queryTypes.Add(type);
        }

        var all = string.Join(", ", queryTypes.Distinct().Select(t => $"global::DivisionEngine.ComponentType<{t}>.Id"));
        var any = string.Join(", ",
            info.Any.AsImmutableArray().Select(t => $"global::DivisionEngine.ComponentType<{t}>.Id"));
        var none = string.Join(", ",
            info.None.AsImmutableArray().Select(t => $"global::DivisionEngine.ComponentType<{t}>.Id"));

        // A job that declares no component access still reads the structure it iterates. The builder
        // only adds that read once a component is named, so a tag-only job states it explicitly.
        var access = accessCalls.Count > 0
            ? "global::DivisionEngine.Access" + string.Join("", accessCalls)
            : "global::DivisionEngine.Access.Read(global::DivisionEngine.ResourceId.Structure)";

        var body = new StringBuilder();
        foreach (var declaration in spanDeclarations)
        {
            body.AppendLine($"                {declaration}");
        }

        if (needsEntities)
        {
            body.AppendLine("                var __entities = __chunk.Entities;");
        }

        var namespaceOpen = info.Namespace.Length > 0 ? $"namespace {info.Namespace}\n{{\n" : "";
        var namespaceClose = info.Namespace.Length > 0 ? "\n}" : "";

        return $$"""
                 // <auto-generated/>
                 #nullable enable

                 {{namespaceOpen}}{{info.ContainingOpen}}partial {{info.TypeKeyword}} {{info.TypeName}}
                 {
                     private static readonly global::DivisionEngine.QueryDescription __query =
                         global::DivisionEngine.QueryDescription.Create(
                             new global::DivisionEngine.ComponentTypeId[] { {{all}} },
                             new global::DivisionEngine.ComponentTypeId[] { {{any}} },
                             new global::DivisionEngine.ComponentTypeId[] { {{none}} });

                     private static readonly global::DivisionEngine.AccessSet __access = {{access}};

                     /// <summary>
                     ///     Schedules this job over every matching chunk. Field values are captured as they are now,
                     ///     and each chunk runs on its own copy, so writes to fields do not carry between chunks.
                     /// </summary>
                     public global::DivisionEngine.JobHandle Schedule(
                         in global::DivisionEngine.JobSchedulingContext __context, string? __name = null)
                     {
                         var __self = this;
                         var __q = __context.World.GetQuery(__query);
                         return __context.Graph.ScheduleChunks(__name ?? "{{info.TypeName}}", __q, __access,
                             (in global::DivisionEngine.JobContext __job, global::DivisionEngine.ArchetypeChunk __chunk) =>
                             {
                                 // Chunks run concurrently; a copy each keeps a mutating Execute from racing.
                                 var __local = __self;
                 {{body.ToString().TrimEnd()}}
                                 var __count = __chunk.Count;
                                 for (var __i = 0; __i < __count; __i++)
                                 {
                                     __local.Execute({{string.Join(", ", arguments)}});
                                 }
                             });
                     }
                 }{{info.ContainingClose}}{{namespaceClose}}
                 """;
    }

    private readonly record struct PendingDiagnostic(DiagnosticDescriptor Descriptor, EquatableArray<string> Arguments);

    private readonly record struct DescribeResult(EntityJobInfo? Info, EquatableArray<PendingDiagnostic> Diagnostics);
}