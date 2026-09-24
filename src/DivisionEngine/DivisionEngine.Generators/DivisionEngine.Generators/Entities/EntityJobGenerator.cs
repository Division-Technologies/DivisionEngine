using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
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

public readonly record struct EntityJobParameter(EntityJobParameterKind Kind, string TypeRef);

public readonly record struct EntityJobInfo(
    string TypeName,
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
        "Parameter '{0}' of '{1}.Execute' is not supported; use 'ref T' or 'in T' for components, 'Entity' for the entity, or 'in JobContext' for the frame",
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

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var jobs = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                EntityJobAttributeFullName,
                static (node, _) => node is StructDeclarationSyntax,
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
                spc.AddSource($"{info.TypeName}.EntityJob.g.cs", Emit(info));
            }
        });
    }

    private static DescribeResult Describe(GeneratorAttributeSyntaxContext ctx)
    {
        var diagnostics = new List<PendingDiagnostic>();

        if (ctx.TargetSymbol is not INamedTypeSymbol symbol || ctx.TargetNode is not StructDeclarationSyntax syntax)
        {
            return new DescribeResult(null, Pack(diagnostics));
        }

        var name = symbol.Name;

        if (!syntax.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword)))
        {
            diagnostics.Add(new PendingDiagnostic(NotPartial, Pack([name])));
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

        var parameters = new List<EntityJobParameter>();
        var failed = false;
        foreach (var parameter in execute.Parameters)
        {
            var typeRef = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var typeName = parameter.Type.ToDisplayString();

            if (typeName == EntityFullName)
            {
                parameters.Add(new EntityJobParameter(EntityJobParameterKind.Entity, typeRef));
                continue;
            }

            if (typeName == JobContextFullName)
            {
                parameters.Add(new EntityJobParameter(EntityJobParameterKind.JobContext, typeRef));
                continue;
            }

            switch (parameter.RefKind)
            {
                case RefKind.Ref:
                    parameters.Add(new EntityJobParameter(EntityJobParameterKind.WriteComponent, typeRef));
                    break;
                case RefKind.In or RefKind.RefReadOnlyParameter:
                    parameters.Add(new EntityJobParameter(EntityJobParameterKind.ReadComponent, typeRef));
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

    /// <summary>Reopens the type's containing types so the generated partial lands in the same place.</summary>
    private static (string Open, string Close) Containing(INamedTypeSymbol symbol)
    {
        var parents = new List<string>();
        for (var t = symbol.ContainingType; t != null; t = t.ContainingType)
        {
            parents.Insert(0, $"partial {(t.TypeKind == TypeKind.Struct ? "struct" : "class")} {t.Name}");
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
                    arguments.Add($"ref __s{spanIndex}[__i]");
                    spanIndex++;
                    break;
                case EntityJobParameterKind.ReadComponent:
                    queryTypes.Add(parameter.TypeRef);
                    accessCalls.Add($".Read<{parameter.TypeRef}>()");
                    spanDeclarations.Add($"var __s{spanIndex} = __chunk.GetReadOnlySpan<{parameter.TypeRef}>();");
                    arguments.Add($"in __s{spanIndex}[__i]");
                    spanIndex++;
                    break;
                case EntityJobParameterKind.Entity:
                    needsEntities = true;
                    arguments.Add("__entities[__i]");
                    break;
                case EntityJobParameterKind.JobContext:
                    arguments.Add("in __job");
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

        // A job that declares no component access still reads the structure it iterates.
        var access = accessCalls.Count > 0
            ? "global::DivisionEngine.Access" + string.Join("", accessCalls)
            : "global::DivisionEngine.AccessSet.Exclusive";

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

                 {{namespaceOpen}}{{info.ContainingOpen}}partial struct {{info.TypeName}}
                 {
                     private static readonly global::DivisionEngine.QueryDescription __query =
                         global::DivisionEngine.QueryDescription.Create(
                             new global::DivisionEngine.ComponentTypeId[] { {{all}} },
                             new global::DivisionEngine.ComponentTypeId[] { {{any}} },
                             new global::DivisionEngine.ComponentTypeId[] { {{none}} });

                     private static readonly global::DivisionEngine.AccessSet __access = {{access}};

                     /// <summary>Schedules this job over every matching chunk. Field values are captured as they are now.</summary>
                     public global::DivisionEngine.JobHandle Schedule(
                         in global::DivisionEngine.JobSchedulingContext __context, string? __name = null)
                     {
                         var __self = this;
                         var __q = __context.World.GetQuery(__query);
                         return __context.Graph.ScheduleChunks(__name ?? "{{info.TypeName}}", __q, __access,
                             (in global::DivisionEngine.JobContext __job, global::DivisionEngine.ArchetypeChunk __chunk) =>
                             {
                 {{body.ToString().TrimEnd()}}
                                 var __count = __chunk.Count;
                                 for (var __i = 0; __i < __count; __i++)
                                 {
                                     __self.Execute({{string.Join(", ", arguments)}});
                                 }
                             });
                     }
                 }{{info.ContainingClose}}{{namespaceClose}}
                 """;
    }

    private readonly record struct PendingDiagnostic(DiagnosticDescriptor Descriptor, EquatableArray<string> Arguments);

    private readonly record struct DescribeResult(EntityJobInfo? Info, EquatableArray<PendingDiagnostic> Diagnostics);
}