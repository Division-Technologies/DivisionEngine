using System.Reflection;
using DivisionEngine;

namespace DivisionEngine.Authoring.Assets;

/// <summary>
///     Owns the collectible <see cref="UserAssemblyLoadContext" /> holding the user (script)
///     assemblies, and provides the <see cref="ITypeResolver" /> that binds serialized user type
///     names to the currently-loaded user types. Swapping assemblies unloads the old context
///     (best-effort) and loads the new DLLs.
///     <para>
///         The actual state migration (serialize all scopes → swap → deserialize) is driven by
///         <see cref="AssetDatabase.ReloadScripts" />, which owns the live object graph.
///     </para>
/// </summary>
public sealed class ScriptHost
{
    private readonly List<Assembly> _assemblies = new();
    private UserAssemblyLoadContext? _context;

    /// <summary>Resolves serialized user type names against the currently-loaded user assemblies.</summary>
    public ITypeResolver TypeResolver { get; private set; } = EmptyResolver.Instance;

    /// <summary>The user assemblies currently loaded.</summary>
    public IReadOnlyList<Assembly> LoadedAssemblies => _assemblies;

    /// <summary>
    ///     Unloads the current user assemblies (if any) and loads the given DLLs into a fresh
    ///     collectible context. DLLs are loaded from memory so the files stay writable for recompiles.
    /// </summary>
    public void Swap(IReadOnlyList<string> dllPaths)
    {
        Unload();

        _context = new UserAssemblyLoadContext();
        foreach (var path in dllPaths)
            _assemblies.Add(_context.LoadFromStream(new MemoryStream(File.ReadAllBytes(path))));

        TypeResolver = new UserTypeResolver(_assemblies.ToArray());
    }

    private void Unload()
    {
        if (_context is null) return;

        _assemblies.Clear();
        TypeResolver = EmptyResolver.Instance;
        _context.Unload();
        _context = null;

        // Best-effort: encourage collection of the old context. Unload only completes once every
        // reference to its types is gone (the caller drops the old object graph before calling Swap).
        for (var i = 0; i < 2; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private sealed class UserTypeResolver(IReadOnlyList<Assembly> assemblies) : ITypeResolver
    {
        public Type? Resolve(string fullName)
        {
            foreach (var assembly in assemblies)
                if (assembly.GetType(fullName) is { } type)
                    return type;
            return null;
        }
    }

    private sealed class EmptyResolver : ITypeResolver
    {
        public static readonly EmptyResolver Instance = new();
        public Type? Resolve(string fullName) => null;
    }
}
