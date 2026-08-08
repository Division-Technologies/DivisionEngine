using System.Reflection;
using System.Runtime.CompilerServices;

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

    /// <summary>Resolves serialized user type IDs against the currently-loaded user assemblies.</summary>
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

        foreach (var asm in _assemblies)
        foreach (var module in asm.GetModules())
            RuntimeHelpers.RunModuleConstructor(module.ModuleHandle);

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

    private sealed class UserTypeResolver : ITypeResolver
    {
        private readonly IReadOnlyList<Assembly> _assemblies;
        private readonly Dictionary<string, Type> _registered = new();
        private Dictionary<string, Type>? _hashIndex;

        public UserTypeResolver(IReadOnlyList<Assembly> assemblies)
        {
            _assemblies = assemblies;

            // Serialized type IDs resolve through the generator-emitted assembly attributes;
            // built here (rather than a global registry) so the map dies with this resolver on swap.
            foreach (var assembly in assemblies)
            foreach (var registration in assembly.GetCustomAttributes<SerializedTypeRegistrationAttribute>())
                if (Guid.TryParse(registration.Id, out var id))
                    _registered.TryAdd(id.ToString("N"), registration.Type);
        }

        public Type? Resolve(string typeId)
        {
            if (_registered.TryGetValue(typeId, out var registered)) return registered;

            if (Guid.TryParse(typeId, out _))
            {
                // Assemblies compiled without the source generator carry no registration
                // attributes; index their serializable types by computed ID instead. User
                // assemblies are few and the index dies with this resolver on swap.
                _hashIndex ??= BuildHashIndex();
                return _hashIndex.TryGetValue(typeId, out var hashed) ? hashed : null;
            }

            // Legacy name-based IDs from documents written before GUID type IDs.
            foreach (var assembly in _assemblies)
                if (assembly.GetType(typeId) is { } type)
                    return type;
            return null;
        }

        private Dictionary<string, Type> BuildHashIndex()
        {
            var index = new Dictionary<string, Type>();
            foreach (var assembly in _assemblies)
            {
                Type?[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types;
                }

                foreach (var type in types)
                    if (type is { IsClass: true, IsAbstract: false } &&
                        typeof(ISerializable).IsAssignableFrom(type))
                        index.TryAdd(SerializedTypeId.Get(type), type);
            }

            return index;
        }
    }

    private sealed class EmptyResolver : ITypeResolver
    {
        public static readonly EmptyResolver Instance = new();

        public Type? Resolve(string typeId)
        {
            return null;
        }
    }
}