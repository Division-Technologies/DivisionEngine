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

    /// <summary>The images currently loaded, kept so that a failed swap can load them again.</summary>
    private byte[][] _images = [];

    /// <summary>Resolves serialized user type IDs against the currently-loaded user assemblies.</summary>
    public ITypeResolver TypeResolver { get; private set; } = EmptyResolver.Instance;

    /// <summary>The user assemblies currently loaded.</summary>
    public IReadOnlyList<Assembly> LoadedAssemblies => _assemblies;

    /// <summary>
    ///     Unloads the current user assemblies (if any) and loads the given DLLs into a fresh
    ///     collectible context. DLLs are loaded from memory so the files stay writable for recompiles.
    ///     <para>
    ///         If the new DLLs cannot be loaded — one is not an assembly, a module initializer throws —
    ///         whatever part of them did load is unloaded again, the previous assemblies are loaded in
    ///         their place, and the failure is rethrown. The host is never left with nothing loaded
    ///         because of a bad build.
    ///     </para>
    /// </summary>
    public void Swap(IReadOnlyList<string> dllPaths)
    {
        // Read before unloading anything: a missing file is a failure that needs no rollback.
        var images = dllPaths.Select(File.ReadAllBytes).ToArray();
        var previous = _images;

        Unload();
        try
        {
            Load(images);
        }
        catch
        {
            Unload();
            if (previous.Length > 0)
            {
                Load(previous);
            }

            throw;
        }
    }

    private void Load(byte[][] images)
    {
        _context = new UserAssemblyLoadContext();
        _images = images;
        foreach (var image in images)
        {
            _assemblies.Add(_context.LoadFromStream(new MemoryStream(image)));
        }

        foreach (var asm in _assemblies)
        foreach (var module in asm.GetModules())
        {
            RuntimeHelpers.RunModuleConstructor(module.ModuleHandle);
        }

        TypeResolver = new UserTypeResolver(_assemblies.ToArray());
    }

    private void Unload()
    {
        if (_context is null)
        {
            return;
        }

        _assemblies.Clear();
        _images = [];
        TypeResolver = EmptyResolver.Instance;

        // The component registry holds a Type for every user component, and one strong reference is
        // enough to keep the context alive. Whoever carries an entity world across the swap has
        // already emptied it (see WorldReload.Release), which the registry requires; with no world to
        // carry this is the only place the types are dropped, and without it the new assembly's
        // components would collide with the old ones' persisted ids.
        ComponentTypeRegistry.UnregisterUnloadable();
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
            {
                if (Guid.TryParse(registration.Id, out var id))
                {
                    _registered.TryAdd(id.ToString("N"), registration.Type);
                }
            }
        }

        public Type? Resolve(string typeId)
        {
            if (_registered.TryGetValue(typeId, out var registered))
            {
                return registered;
            }

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
            {
                if (assembly.GetType(typeId) is { } type)
                {
                    return type;
                }
            }

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
                {
                    if (type is { IsClass: true, IsAbstract: false } &&
                        typeof(ISerializable).IsAssignableFrom(type))
                    {
                        index.TryAdd(SerializedTypeId.Get(type), type);
                    }
                }
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

/// <summary>
///     A script reload that could not load the new assemblies. The previous ones were loaded again and
///     the state restored against them, so the editor carries on as it was before the build.
/// </summary>
public sealed class ScriptReloadException(string message, Exception inner) : Exception(message, inner);