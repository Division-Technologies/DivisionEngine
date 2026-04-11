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
    Array,
    Class,
    Struct,
    Unsupported
}

/// <summary>
/// Pre-resolved type information extracted from <see cref="ITypeSymbol"/>.
/// This is a pure data record with value equality, safe to cache in incremental generator pipelines.
/// </summary>
public sealed record ResolvedTypeInfo(
    ResolvedTypeKind Kind,
    string FullyQualifiedName,
    string DisplayName,
    string Name,
    ResolvedTypeInfo? ArrayElementType)
{
    public static ResolvedTypeInfo FromSymbol(ITypeSymbol symbol)
    {
        var kind = ResolveKind(symbol);
        return new ResolvedTypeInfo(
            Kind: kind,
            FullyQualifiedName: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            DisplayName: symbol.ToDisplayString(),
            Name: symbol.Name,
            ArrayElementType: symbol is IArrayTypeSymbol arr ? FromSymbol(arr.ElementType) : null);
    }

    private static ResolvedTypeKind ResolveKind(ITypeSymbol symbol)
    {
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

        if (symbol is IArrayTypeSymbol)
            return ResolvedTypeKind.Array;

        if (symbol is ITypeParameterSymbol typeParam)
        {
            if (typeParam.HasReferenceTypeConstraint) return ResolvedTypeKind.Class;
            if (typeParam.HasValueTypeConstraint) return ResolvedTypeKind.Struct;
            return ResolvedTypeKind.Unsupported;
        }

        if (symbol is INamedTypeSymbol named)
        {
            switch (named.TypeKind)
            {
                case TypeKind.Class:
                case TypeKind.Interface:
                    return ResolvedTypeKind.Class;
                case TypeKind.Struct:
                    return ResolvedTypeKind.Struct;
            }
        }

        return ResolvedTypeKind.Unsupported;
    }
}
