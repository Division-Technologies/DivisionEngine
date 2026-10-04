using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace DivisionEngine.Generators.Entities;

/// <summary>Why an <c>Entity</c> held by a component cannot be rewritten by generated code.</summary>
public enum EntityFieldProblem
{
    /// <summary>Generated code cannot name it or write through it.</summary>
    Unreachable,

    /// <summary>It is <c>readonly</c>, get-only or init-only, so it cannot be assigned after construction.</summary>
    ReadOnly
}

/// <param name="Path">The member path below the component, such as <c>Link.Target</c>.</param>
/// <param name="Reason">For <see cref="EntityFieldProblem.Unreachable" />, why, phrased to follow "because".</param>
/// <param name="Location">The member's own declaration when it is on the component itself, otherwise null.</param>
public readonly record struct SkippedEntityField(
    string Path,
    EntityFieldProblem Problem,
    string Reason,
    Location? Location);

/// <summary>
///     Finds the <c>Entity</c> values stored inside an unmanaged component, shared by the registration
///     generator (which remaps the ones it can reach) and the convention analyzer (which reports the
///     ones it cannot), so the two never disagree about which is which.
/// </summary>
internal static class EntityFields
{
    private const string EntityFullName = "DivisionEngine.Entity";

    /// <summary>Depth limit for the walk into nested structs looking for entity fields.</summary>
    private const int MaxNesting = 8;

    /// <summary>
    ///     Adds an assignable access path (rooted at <paramref name="root" />) per reachable entity to
    ///     <paramref name="remappable" />, and each entity-holding member that cannot be assigned to
    ///     <paramref name="skipped" />, when given.
    /// </summary>
    public static void Collect(INamedTypeSymbol component, string root, List<string> remappable,
        List<SkippedEntityField>? skipped)
    {
        Walk(component.ContainingAssembly, component, root, "", remappable, skipped, 0);
    }

    private static void Walk(IAssemblySymbol assembly, INamedTypeSymbol type, string path, string display,
        List<string> remappable, List<SkippedEntityField>? skipped, int depth)
    {
        if (depth > MaxNesting)
        {
            return;
        }

        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst)
            {
                continue;
            }

            // An auto-property's storage is its backing field; it is judged, and written, through the
            // property. Any other associated symbol (a field-like event) holds no entity.
            ISymbol member = field;
            if (field.AssociatedSymbol is IPropertySymbol property)
            {
                member = property;
            }
            else if (field.AssociatedSymbol is not null)
            {
                continue;
            }

            var isEntity = IsEntity(field.Type);
            if (!isEntity && !ContainsEntity(field.Type, depth + 1))
            {
                continue;
            }

            var memberPath = $"{path}.{member.Name}";
            var memberDisplay = display.Length == 0 ? member.Name : $"{display}.{member.Name}";

            var (problem, reason) = Judge(assembly, field, member, isEntity);
            if (problem is { } p)
            {
                var location = depth == 0 && member.Locations.Length > 0 && member.Locations[0].IsInSource
                    ? member.Locations[0]
                    : null;
                skipped?.Add(new SkippedEntityField(memberDisplay, p, reason, location));
                continue;
            }

            if (isEntity)
            {
                remappable.Add(memberPath);
            }
            else
            {
                Walk(assembly, (INamedTypeSymbol)field.Type, memberPath, memberDisplay, remappable, skipped,
                    depth + 1);
            }
        }
    }

    /// <summary>Whether generated code in <paramref name="assembly" /> can assign the member, and why not.</summary>
    private static (EntityFieldProblem? Problem, string Reason) Judge(IAssemblySymbol assembly, IFieldSymbol field,
        ISymbol member, bool isEntity)
    {
        if (!IsAccessible(assembly, member))
        {
            return (EntityFieldProblem.Unreachable, "it is not public or internal");
        }

        if (member is IPropertySymbol property)
        {
            if (property.SetMethod is null || property.SetMethod.IsInitOnly)
            {
                return (EntityFieldProblem.ReadOnly, "");
            }

            if (!IsAccessible(assembly, property.SetMethod))
            {
                return (EntityFieldProblem.Unreachable, "its setter is not public or internal");
            }

            if (!isEntity)
            {
                // A struct read from a property is a copy, so writing into it would go nowhere.
                return (EntityFieldProblem.Unreachable, "it is a property, and a struct read from one is a copy");
            }

            return (null, "");
        }

        return field.IsReadOnly ? (EntityFieldProblem.ReadOnly, "") : (null, "");
    }

    private static bool IsAccessible(IAssemblySymbol assembly, ISymbol symbol)
    {
        return symbol.DeclaredAccessibility switch
        {
            Accessibility.Public => true,
            Accessibility.Internal or Accessibility.ProtectedOrInternal =>
                SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, assembly),
            _ => false
        };
    }

    private static bool IsEntity(ITypeSymbol type)
    {
        return type.ToDisplayString() == EntityFullName;
    }

    /// <summary>Whether any instance field of <paramref name="type" />, at any depth and accessibility, is an entity.</summary>
    private static bool ContainsEntity(ITypeSymbol type, int depth)
    {
        if (depth > MaxNesting || type is not INamedTypeSymbol
                               {
                                   IsValueType: true, SpecialType: SpecialType.None
                               } named
                               || named.TypeKind == TypeKind.Enum)
        {
            return false;
        }

        foreach (var field in named.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst)
            {
                continue;
            }

            if (IsEntity(field.Type) || ContainsEntity(field.Type, depth + 1))
            {
                return true;
            }
        }

        return false;
    }
}