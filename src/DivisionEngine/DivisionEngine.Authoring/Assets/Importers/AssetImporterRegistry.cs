using System.Reflection;

namespace DivisionEngine.Authoring.Assets;

/// <summary>
///     Maps file extensions to <see cref="IAssetImporter" /> implementations. Importer types are
///     discovered by scanning loaded assemblies for <see cref="AssetImporterAttribute" /> on first
///     use (the same assembly-scanning approach as the YAML type resolver); explicit registration is
///     also supported. A future iteration may replace scanning with source-generated registration.
/// </summary>
public static class AssetImporterRegistry
{
    private static readonly Dictionary<string, Type> ByExtension = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    private static bool _scanned;

    /// <summary>Registers <paramref name="importerType" /> as the importer for <paramref name="extension" />.</summary>
    public static void Register(string extension, Type importerType)
    {
        lock (Gate)
        {
            ByExtension[Normalize(extension)] = importerType;
        }
    }

    /// <summary>Creates a fresh importer instance for an extension, or null if none is registered.</summary>
    public static IAssetImporter? Resolve(string extension)
    {
        EnsureScanned();
        Type? type;
        lock (Gate)
        {
            if (!ByExtension.TryGetValue(Normalize(extension), out type)) return null;
        }

        return (IAssetImporter?)Activator.CreateInstance(type);
    }

    private static void EnsureScanned()
    {
        lock (Gate)
        {
            if (_scanned) return;
            _scanned = true;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t is not null).ToArray()!;
                }

                foreach (var type in types)
                {
                    var attribute = type.GetCustomAttribute<AssetImporterAttribute>();
                    if (attribute is null) continue;
                    if (!typeof(IAssetImporter).IsAssignableFrom(type)) continue;
                    foreach (var extension in attribute.Extensions) ByExtension[Normalize(extension)] = type;
                }
            }
        }
    }

    private static string Normalize(string extension)
    {
        extension = extension.Trim();
        return extension.StartsWith('.') ? extension : "." + extension;
    }
}