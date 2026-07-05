using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace DivisionEngine.Generators.Serialization;

public enum ResolvedTypeKind
{
    Bool,
    I8,
    I16,
    I32,
    I64,
    F32,
    F64,
    String,
    Enum,

    /// <summary>Everything else — dispatched through FormatterStore&lt;T&gt; at run time.</summary>
    Store,

    /// <summary>Never serializable (pointers, multi-dimensional arrays, type parameters, ...).</summary>
    Unsupported
}

/// <summary>
///     Pre-resolved type information extracted from <see cref="ITypeSymbol" />.
///     This is a pure data record with value equality, safe to cache in incremental generator pipelines.
/// </summary>
public sealed record ResolvedTypeInfo(
    ResolvedTypeKind Kind,
    string DisplayName,
    string StoreTypeRef,
    ResolvedTypeKind EnumUnderlyingKind,
    EquatableArray<string> RequiredFormatters)
{
    private const string SerializableObjectInterfaceFullName = "DivisionEngine.ISerializableObject";

    public static ResolvedTypeInfo FromSymbol(ITypeSymbol symbol)
    {
        var kind = ResolveKind(symbol, out var enumUnderlying);

        var required = ImmutableArray<string>.Empty;
        if (kind == ResolvedTypeKind.Store)
        {
            var builder = ImmutableArray.CreateBuilder<string>();
            var unsupported = false;
            CollectRequirements(symbol, builder, ref unsupported);
            if (unsupported) kind = ResolvedTypeKind.Unsupported;
            else required = builder.ToImmutable();
        }

        return new ResolvedTypeInfo(
            kind,
            symbol.ToDisplayString(),
            symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            enumUnderlying,
            new EquatableArray<string>(required));
    }

    private static ResolvedTypeKind ResolveKind(ITypeSymbol symbol, out ResolvedTypeKind enumUnderlying)
    {
        enumUnderlying = ResolvedTypeKind.I32;

        switch (symbol.SpecialType)
        {
            case SpecialType.System_Boolean:
                return ResolvedTypeKind.Bool;
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
                return ResolvedTypeKind.I8;
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Char:
                return ResolvedTypeKind.I16;
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
                return ResolvedTypeKind.I32;
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
                return ResolvedTypeKind.I64;
            case SpecialType.System_Single:
                return ResolvedTypeKind.F32;
            case SpecialType.System_Double:
                return ResolvedTypeKind.F64;
            case SpecialType.System_String:
                return ResolvedTypeKind.String;
        }

        if (symbol.TypeKind == TypeKind.Enum)
        {
            var underlying = (symbol as INamedTypeSymbol)?.EnumUnderlyingType;
            enumUnderlying = underlying is null
                ? ResolvedTypeKind.I32
                : ResolveKind(underlying, out _);
            return ResolvedTypeKind.Enum;
        }

        if (symbol is ITypeParameterSymbol || symbol is IPointerTypeSymbol || symbol.IsRefLikeType)
            return ResolvedTypeKind.Unsupported;

        return ResolvedTypeKind.Store;
    }

    /// <summary>
    ///     Walks the type recursively and collects the fully-qualified original-definition names
    ///     that must have a registered formatter for this type to be resolvable.
    ///     Primitives/string/enums resolve intrinsically; ISerializableObject implementors resolve
    ///     via the reference-formatter fallback; rank-1 arrays recurse into the element type.
    /// </summary>
    private static void CollectRequirements(ITypeSymbol symbol, ImmutableArray<string>.Builder names,
        ref bool unsupported)
    {
        switch (symbol.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Char:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_String:
                return;
        }

        if (symbol.TypeKind == TypeKind.Enum) return;

        if (symbol is IArrayTypeSymbol array)
        {
            if (array.Rank != 1)
            {
                unsupported = true;
                return;
            }

            CollectRequirements(array.ElementType, names, ref unsupported);
            return;
        }

        if (symbol.AllInterfaces.Any(i => i.ToDisplayString() == SerializableObjectInterfaceFullName))
            return;

        if (symbol is INamedTypeSymbol named)
        {
            names.Add(named.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            foreach (var arg in named.TypeArguments)
                CollectRequirements(arg, names, ref unsupported);
            return;
        }

        unsupported = true;
    }
}