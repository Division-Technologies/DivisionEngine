using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace DivisionEngine;

/// <summary>
///     Computes the stable serialized type ID used to frame objects: the
///     <see cref="TypeIdAttribute" /> GUID when present, otherwise an MD5 hash of
///     <see cref="Type.FullName" />. Rendered in "N" format (32 hex digits).
/// </summary>
public static class SerializedTypeId
{
    // Keyed weakly by Type so types from collectible (user script) load contexts can unload.
    private static readonly ConditionalWeakTable<Type, string> Cache = new();

    public static string Get(Type type)
    {
        return Cache.GetValue(type, static t => Compute(t).ToString("N"));
    }

    private static Guid Compute(Type type)
    {
        if (type.GetCustomAttribute<TypeIdAttribute>(false) is { } attribute)
        {
            return attribute.Id;
        }

        var fullName = type.FullName ??
                       throw new InvalidOperationException($"Type {type} does not have a full name.");
        // Must match the compile-time computation in the generator (SerializedTypeGuid.ComputeDefault).
        return new Guid(MD5.HashData(Encoding.UTF8.GetBytes(fullName)));
    }
}