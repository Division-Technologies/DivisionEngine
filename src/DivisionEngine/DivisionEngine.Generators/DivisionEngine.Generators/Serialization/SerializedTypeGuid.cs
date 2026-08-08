using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;

namespace DivisionEngine.Generators.Serialization;

/// <summary>
///     Compile-time computation of default serialized type IDs. Must match the runtime
///     computation in DivisionEngine.SerializedTypeId (MD5 of Type.FullName).
/// </summary>
public static class SerializedTypeGuid
{
    public static Guid ComputeDefault(string metadataFullName)
    {
        using var md5 = MD5.Create();
        return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(metadataFullName)));
    }

    /// <summary>
    ///     Reflection-style full name ('Ns.Outer+Inner'), matching Type.FullName for
    ///     non-generic types.
    /// </summary>
    public static string MetadataFullName(INamedTypeSymbol symbol)
    {
        var parts = new List<string>();
        for (var t = symbol; t != null; t = t.ContainingType)
        {
            parts.Insert(0, t.MetadataName);
        }

        var name = string.Join("+", parts);
        var ns = symbol.ContainingNamespace;
        return ns is { IsGlobalNamespace: false } ? $"{ns.ToDisplayString()}.{name}" : name;
    }
}