using System.Reflection;
using System.Runtime.Loader;

namespace DivisionEngine;

/// <summary>
///     Maps serialized type IDs (GUIDs, in "N" format) to runtime types by reading the
///     generator-emitted <see cref="SerializedTypeRegistrationAttribute" /> assembly attributes of
///     loaded assemblies.
///     <para>
///         Collectible assemblies (user script AssemblyLoadContexts) are deliberately excluded —
///         caching their types here would keep an unloaded context alive. They are covered by the
///         <see cref="ITypeResolver" /> supplied during deserialization instead.
///     </para>
/// </summary>
internal static class SerializedTypeRegistry
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Type> Map = new();
    private static readonly HashSet<Assembly> Scanned = new();

    /// <param name="typeId">The serialized type ID as a canonical "N"-format GUID string.</param>
    internal static Type? Resolve(string typeId)
    {
        lock (Gate)
        {
            if (Map.TryGetValue(typeId, out var cached))
            {
                return cached;
            }

            ScanNewAssemblies();
            return Map.TryGetValue(typeId, out var found) ? found : null;
        }
    }

    private static void ScanNewAssemblies()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
            {
                continue;
            }

            if (AssemblyLoadContext.GetLoadContext(assembly)?.IsCollectible ?? false)
            {
                continue;
            }

            if (!Scanned.Add(assembly))
            {
                continue;
            }

            foreach (var registration in assembly.GetCustomAttributes<SerializedTypeRegistrationAttribute>())
            {
                if (Guid.TryParse(registration.Id, out var id))
                {
                    Map.TryAdd(id.ToString("N"), registration.Type);
                }
            }
        }
    }
}