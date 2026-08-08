using System.Buffers;
using VYaml.Emitter;

namespace DivisionEngine.Authoring.Assets;

/// <summary>
///     Central entry point for the authoring asset layer. A
///     database is scoped to a project: a set of asset root folders plus a persistent cache directory
///     (the project's "Library"). It scans the roots, imports assets through the importer pipeline,
///     persists imported data to the cache, and (optionally) watches the roots to re-import changed
///     assets automatically.
///     <para>
///         An asset's GUID is its <see cref="ScopeId" /> and an asset file corresponds to one
///         <see cref="SerializationScope" />. GUIDs are persisted in <c>.meta</c> sidecars next to each
///         source file, so they remain stable across sessions and machines. Cross-asset references
///         resolve lazily through <see cref="AssetResolver" />.
///     </para>
/// </summary>
public sealed class AssetDatabase : IDisposable
{
    // Input source files (e.g. .cs) an asset consumed, and the reverse map file -> dependent assets.
    // A change to such a file re-imports its dependents (the .csproj asset).
    private readonly Dictionary<ScopeId, string[]> _assetInputs = new();
    private readonly ImportCache _cache;
    private readonly AssetDependencyGraph _dependencies = new();
    private readonly Dictionary<string, HashSet<ScopeId>> _fileDependents = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<ScopeId, string> _guidToPath = new();
    private readonly Dictionary<ScopeId, ISerializationScopeLoader> _loaders = new();
    private readonly Dictionary<ScopeId, LocalId> _mainIds = new();
    private readonly Dictionary<string, ScopeId> _pathToGuid = new();

    // Pending file-system changes accumulated by the watcher. They are NOT acted on when the event
    // fires; reimport happens only when Refresh() is called explicitly.
    private readonly HashSet<string> _pendingChanged = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingDeleted = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pendingGate = new();
    private readonly List<string> _roots = new();

    // The YAML scope file a GUID loads from: the asset file itself for direct assets, or the import
    // cache file for imported assets. Distinct from _guidToPath, which is the source asset path.
    private readonly Dictionary<ScopeId, string> _scopeFile = new();
    private readonly Dictionary<ScopeId, SerializationScope> _scopes = new();

    // Compiled user assemblies (guid -> DLL path) and whether a script (re)load is pending.
    private readonly Dictionary<ScopeId, string> _scriptDlls = new();

    // Set during script reload so every scope load resolves user types against the active user ALC.
    private ITypeResolver? _typeResolver;

    private AssetWatcher? _watcher;

    /// <param name="cacheDirectory">Where imported asset caches are stored. Defaults to a temp folder.</param>
    public AssetDatabase(string? cacheDirectory = null)
    {
        // Resolve to an absolute path: artifact paths derived from it (e.g. the compiler's obj/bin
        // dirs and the compiled DLL path) must be absolute, since MSBuild resolves relative paths
        // against the project directory rather than the app's working directory.
        _cache = new ImportCache(
            Path.GetFullPath(cacheDirectory ?? Path.Combine(Path.GetTempPath(), "DivisionEngine", "Cache")));
    }

    /// <summary>Opens a project: the given asset root folders, with imports cached in the project cache dir.</summary>
    public AssetDatabase(IReadOnlyList<string> roots, string cacheDirectory) : this(cacheDirectory)
    {
        foreach (var root in roots)
        {
            _roots.Add(Normalize(root));
        }
    }

    /// <summary>The asset root folders this database scans.</summary>
    public IReadOnlyList<string> Roots => _roots;

    /// <summary>Every asset GUID currently known to the database.</summary>
    public IReadOnlyCollection<ScopeId> AllAssets => _guidToPath.Keys;

    /// <summary>
    ///     Reloads user (script) assemblies, migrating all live object state across the swap. Serializes
    ///     every materialized scope, drops the live instances (so the old assemblies can be unloaded),
    ///     swaps the user assembly context, then re-creates and re-deserializes the objects against the
    ///     new types. Cross-object references are re-resolved by GlobalId, so they survive the reload.
    ///     <para>External holders of references must re-acquire from the database after a reload.</para>
    /// </summary>
    /// <summary>Whether a compiled user assembly changed since the last script (re)load.</summary>
    public bool ScriptsDirty { get; private set; }

    public void Dispose()
    {
        StopWatching();
        foreach (var scope in _scopes.Values)
        {
            scope.Dispose();
        }

        _scopes.Clear();
    }

    /// <summary>Returns the GUID registered for an asset path, or null if unknown.</summary>
    public ScopeId? GetGuid(string path)
    {
        return _pathToGuid.TryGetValue(Normalize(path), out var guid) ? guid : null;
    }

    /// <summary>Returns the source file path registered for a GUID, or null if unknown.</summary>
    public string? GetPath(ScopeId guid)
    {
        return _guidToPath.GetValueOrDefault(guid);
    }

    /// <summary>Associates an asset GUID with a file path (both directions).</summary>
    public void Register(ScopeId guid, string path)
    {
        path = Normalize(path);
        _guidToPath[guid] = path;
        _pathToGuid[path] = guid;
        // By default the registered path is also the scope file (direct asset). Importing overrides
        // this to point at the import cache for assets that go through an importer.
        _scopeFile.TryAdd(guid, path);
    }

    /// <summary>
    ///     Applies pending changes to the database. While watching, this processes the file-system
    ///     events the watcher has queued since the last refresh (re-importing changed assets and their
    ///     dependents, and dropping deleted ones) — the watcher itself never re-imports. When not
    ///     watching, this performs a full rescan via <see cref="Rescan" />. Imports hit the persistent
    ///     cache, so unchanged assets are not re-imported.
    /// </summary>
    public void Refresh()
    {
        if (_watcher is null)
        {
            Rescan();
            return;
        }

        string[] changed, deleted;
        lock (_pendingGate)
        {
            changed = _pendingChanged.ToArray();
            deleted = _pendingDeleted.ToArray();
            _pendingChanged.Clear();
            _pendingDeleted.Clear();
        }

        foreach (var path in deleted)
        {
            RemoveAsset(path);
        }

        foreach (var path in changed)
        {
            ProcessChange(path);
        }
    }

    /// <summary>
    ///     Performs a full scan of every root folder, synchronizing the database with disk: new and
    ///     changed assets are (re-)imported and assets whose source file disappeared are dropped. Any
    ///     queued watcher changes are discarded (the scan supersedes them).
    /// </summary>
    public void Rescan()
    {
        lock (_pendingGate)
        {
            _pendingChanged.Clear();
            _pendingDeleted.Clear();
        }

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in _roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var path = Normalize(file);
                present.Add(path);
                SyncAsset(path);
            }
        }

        foreach (var path in _pathToGuid.Keys.Where(p => !present.Contains(p)).ToArray())
        {
            RemoveAsset(path);
        }
    }

    /// <summary>
    ///     Begins watching the root folders. File-system events are only queued; call
    ///     <see cref="Refresh" /> to apply them.
    /// </summary>
    public void StartWatching()
    {
        _watcher?.Dispose();
        _watcher = new AssetWatcher(_roots, EnqueueChanged, EnqueueDeleted);
    }

    /// <summary>Queues a changed/created source path for the next <see cref="Refresh" />.</summary>
    internal void EnqueueChanged(string path)
    {
        path = Normalize(path);
        lock (_pendingGate)
        {
            _pendingDeleted.Remove(path);
            _pendingChanged.Add(path);
        }
    }

    /// <summary>Queues a deleted source path for the next <see cref="Refresh" />.</summary>
    internal void EnqueueDeleted(string path)
    {
        path = Normalize(path);
        lock (_pendingGate)
        {
            _pendingChanged.Remove(path);
            _pendingDeleted.Add(path);
        }
    }

    /// <summary>Stops watching the root folders.</summary>
    public void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    /// <summary>
    ///     Creates a new, empty in-memory scope (a new asset). Add objects to it with
    ///     <see cref="AddObject" />, then persist with <see cref="SaveAsset" />.
    /// </summary>
    public SerializationScope CreateScope()
    {
        var guid = ScopeId.New();
        var scope = new SerializationScope(guid, NullScopeLoader.Instance);
        _scopes[guid] = scope;
        _loaders[guid] = NullScopeLoader.Instance;
        _mainIds[guid] = new LocalId(0);
        return scope;
    }

    /// <summary>Registers <paramref name="obj" /> in <paramref name="scope" /> and returns its assigned id.</summary>
    public LocalId AddObject(SerializationScope scope, ISerializableObject obj)
    {
        return scope.Add(obj);
    }

    /// <summary>
    ///     Serializes a direct (serialization-native) asset to <paramref name="path" />, writes its
    ///     <c>.meta</c> sidecar, and registers it.
    /// </summary>
    public void SaveAsset(SerializationScope scope, string path)
    {
        path = Normalize(path);
        File.WriteAllBytes(path, SerializeScope(scope));
        AssetMeta.Write(path + ".meta", new AssetMeta { Guid = scope.Id.Value });
        Register(scope.Id, path);
        _scopeFile[scope.Id] = path;
    }

    /// <summary>Loads an asset's main object by GUID, resolving its (possibly cross-asset) references.</summary>
    public T? LoadAsset<T>(ScopeId guid) where T : class, ISerializableObject
    {
        if (GetOrLoadScope(guid) is null)
        {
            return null;
        }

        // Index the transitive closure of referenced scopes first. Once every needed scope is
        // indexed, materializing a referenced object is just Activator.CreateInstance — no file is
        // parsed during the deserialize phase, which VYaml's non-re-entrant parser requires.
        PreloadClosure(guid);

        var resolver = new AssetResolver(this);
        var main = resolver.Resolve(new GlobalId(guid, _mainIds.GetValueOrDefault(guid)));
        resolver.DrainPending();
        return main as T;
    }

    /// <summary>Loads an asset's main object by path.</summary>
    public T? LoadAsset<T>(string path) where T : class, ISerializableObject
    {
        return GetGuid(path) is { } guid ? LoadAsset<T>(guid) : null;
    }

    /// <summary>
    ///     Imports a source asset file through its registered importer (or the importer recorded in its
    ///     <c>.meta</c>), hitting the cache when the source and importer are unchanged. Returns the
    ///     asset's main object, or null if the file has no importer.
    /// </summary>
    public T? ImportAsset<T>(string path) where T : class, ISerializableObject
    {
        path = Normalize(path);
        return ImportInternal(path) is { } guid ? LoadAsset<T>(guid) : null;
    }

    /// <summary>
    ///     Re-imports a source asset and every asset that transitively depends on it. Already-loaded
    ///     scopes are updated in place (existing object instances keep their identity) so that
    ///     references held by other assets remain valid after the re-import.
    /// </summary>
    public void Reimport(string path)
    {
        Reimport(Normalize(path), new HashSet<ScopeId>());
    }

    /// <summary>Reloads user assemblies if any changed since the last reload; otherwise a no-op.</summary>
    public void ReloadScriptsIfDirty(ScriptHost host)
    {
        if (!ScriptsDirty)
        {
            return;
        }

        ScriptsDirty = false;
        ReloadScripts(host, _scriptDlls.Values.ToArray());
    }

    public void ReloadScripts(ScriptHost host, IReadOnlyList<string> dllPaths)
    {
        // 1. Serialize every materialized scope's current runtime state.
        var saved = new Dictionary<ScopeId, byte[]>();
        foreach (var (guid, scope) in _scopes)
        {
            if (scope.Objects.Count > 0)
            {
                saved[guid] = SerializeScope(scope);
            }
        }

        // 2. Drop the live object graph so no engine->user references keep the old context alive.
        foreach (var scope in _scopes.Values)
        {
            scope.Dispose();
        }

        _scopes.Clear();
        _loaders.Clear();

        // 3. Swap the user assembly context; route all subsequent type resolution through it.
        host.Swap(dllPaths);
        _typeResolver = host.TypeResolver;

        // 4. Rebuild the saved scopes from their serialized state, materializing with the new types.
        foreach (var (guid, bytes) in saved)
        {
            var loader = new FileScopeLoader(bytes, _typeResolver);
            _scopes[guid] = new SerializationScope(guid, loader);
            _loaders[guid] = loader;
            _mainIds[guid] = loader.MainId;
        }

        var resolver = new AssetResolver(this);
        foreach (var guid in saved.Keys)
        {
            if (_loaders[guid] is FileScopeLoader loader)
            {
                foreach (var localId in loader.ObjectIds)
                {
                    resolver.Resolve(new GlobalId(guid, localId));
                }
            }
        }

        resolver.DrainPending();
    }

    private void Reimport(string path, HashSet<ScopeId> visited)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var metaPath = path + ".meta";
        var meta = File.Exists(metaPath) ? AssetMeta.Read(metaPath, _typeResolver) : null;
        var importer = ResolveImporter(meta, path);
        if (importer is null)
        {
            return;
        }

        var guid = meta is not null ? new ScopeId(meta.Guid) : ScopeId.New();
        if (!visited.Add(guid))
        {
            return;
        }

        var run = RunImporter(path, importer, guid);
        if (run is null)
        {
            return; // import failed; already logged, skip this asset
        }

        if (_scopes.TryGetValue(guid, out var scope) && _scopeFile.TryGetValue(guid, out var file))
        {
            ReloadInPlace(guid, scope, file);
        }
        else
        {
            RegisterImportedScope(guid, run.Value);
        }

        TrackScript(guid, run.Value.Main);

        foreach (var dependent in _dependencies.GetDependents(guid).ToArray())
        {
            if (GetPath(dependent) is { } dependentPath)
            {
                Reimport(dependentPath, visited);
            }
        }
    }

    /// <summary>Imports the asset if it has an importer; returns its GUID, or null when it is not importable.</summary>
    private ScopeId? ImportInternal(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var metaPath = path + ".meta";
        var meta = File.Exists(metaPath) ? AssetMeta.Read(metaPath, _typeResolver) : null;
        var importer = ResolveImporter(meta, path);
        if (importer is null)
        {
            return null;
        }

        var guid = meta is not null ? new ScopeId(meta.Guid) : ScopeId.New();
        var cacheFile = _cache.PathFor(guid);
        var inputFiles = meta is not null ? ToAbsoluteInputs(path, meta.InputFiles) : [];
        var sourceHash = ImportCache.CombinedHash(path, inputFiles);

        // Cache hit: source (and all declared inputs) and importer unchanged, and the cache exists.
        if (meta?.Importer is not null && meta.SourceHash == sourceHash &&
            meta.ImporterVersion == importer.Version && File.Exists(cacheFile))
        {
            Register(guid, path);
            _scopeFile[guid] = cacheFile;
            _mainIds[guid] = new LocalId(meta.MainLocalId);
            _dependencies.SetDependencies(guid, meta.Dependencies.Select(g => new ScopeId(g)));
            SetFileDependents(guid, inputFiles);
            // Cache hit has no produced main object in hand; load it only for the script importer.
            if (importer is CSharpProjectImporter)
            {
                TrackScript(guid, LoadAsset<CompiledAssembly>(guid));
            }

            return guid;
        }

        var run = RunImporter(path, importer, guid);
        if (run is null)
        {
            return null; // import failed; already logged, skip this asset
        }

        if (_scopes.TryGetValue(guid, out var scope) && _scopeFile.TryGetValue(guid, out var file))
        {
            ReloadInPlace(guid, scope, file);
        }
        else
        {
            RegisterImportedScope(guid, run.Value);
        }

        TrackScript(guid, run.Value.Main);
        return guid;
    }

    /// <summary>
    ///     If the produced main object is a successfully compiled assembly, records its DLL and flags
    ///     that a script (re)load is pending. Called on both first import and re-import.
    /// </summary>
    private void TrackScript(ScopeId guid, ISerializableObject? main)
    {
        if (main is not CompiledAssembly { Success: true } compiled || compiled.DllPath.Length == 0)
        {
            return;
        }

        _scriptDlls[guid] = compiled.DllPath;
        ScriptsDirty = true;
    }

    /// <summary>Synchronizes one file found during <see cref="Refresh" />: import it, or register a direct asset.</summary>
    private void SyncAsset(string path)
    {
        if (ImportInternal(path) is not null)
        {
            return;
        }

        if (File.Exists(path + ".meta"))
        {
            RegisterDirect(path);
        }
    }

    /// <summary>Registers an existing direct (importer-less) asset from its <c>.meta</c> sidecar.</summary>
    private void RegisterDirect(string path)
    {
        var meta = AssetMeta.Read(path + ".meta", _typeResolver);
        var guid = new ScopeId(meta.Guid);
        Register(guid, path);
        _scopeFile[guid] = path;
    }

    /// <summary>Applies a single queued change: re-import an asset (or reload a direct one) and any dependents.</summary>
    private void ProcessChange(string path)
    {
        path = Normalize(path);
        if (!File.Exists(path))
        {
            return;
        }

        // Re-import assets that consume this file as an input (e.g. a .cs behind a .csproj). This runs
        // independently of whether the file is itself an asset, so a script edit still rebuilds its
        // project even though the .cs is also imported (as a binary asset) below.
        if (_fileDependents.TryGetValue(path, out var dependents))
        {
            foreach (var dependent in dependents.ToArray())
            {
                if (GetPath(dependent) is { } dependentPath)
                {
                    Reimport(dependentPath);
                }
            }
        }

        var metaPath = path + ".meta";
        var meta = File.Exists(metaPath) ? AssetMeta.Read(metaPath, _typeResolver) : null;

        if (ResolveImporter(meta, path) is not null)
        {
            Reimport(path);
        }
        else if (meta is not null)
        {
            RegisterDirect(path);
            if (GetGuid(path) is { } guid && _scopes.TryGetValue(guid, out var scope))
            {
                ReloadInPlace(guid, scope, path);
            }
        }
    }

    /// <summary>
    ///     Chooses the importer for a source file: the one recorded in its <c>.meta</c>, else the one
    ///     registered for its extension, else — for an uncovered extension with no <c>.meta</c> — the
    ///     raw binary fallback. Returns null only for a direct asset (a <c>.meta</c> with no importer).
    /// </summary>
    private static IAssetImporter? ResolveImporter(AssetMeta? meta, string path)
    {
        if (meta?.Importer is not null)
        {
            return meta.Importer;
        }

        if (AssetImporterRegistry.Resolve(Path.GetExtension(path)) is { } specific)
        {
            return specific;
        }

        if (meta is not null)
        {
            return null;
        }

        return RawBinaryImporter.Instance;
    }

    /// <summary>Re-deserializes a scope's existing instances from <paramref name="file" />, preserving identity.</summary>
    private void ReloadInPlace(ScopeId guid, SerializationScope scope, string file)
    {
        PreloadClosure(guid);
        var resolver = new AssetResolver(this);
        var loader = new FileScopeLoader(File.ReadAllBytes(file), _typeResolver);
        scope.Reload(loader, resolver);
        resolver.DrainPending();
        _loaders[guid] = loader;
        _mainIds[guid] = loader.MainId;
    }

    private void RegisterImportedScope(ScopeId guid, ImportRun run)
    {
        _scopes[guid] = run.Scope;
        _loaders[guid] = NullScopeLoader.Instance;
        _mainIds[guid] = run.MainId;
    }

    private void RemoveAsset(string path)
    {
        if (!_pathToGuid.Remove(path, out var guid))
        {
            return;
        }

        if (_scopes.Remove(guid, out var scope))
        {
            scope.Dispose();
        }

        _loaders.Remove(guid);
        _mainIds.Remove(guid);
        _scopeFile.Remove(guid);
        _guidToPath.Remove(guid);
        _dependencies.Remove(guid);
        SetFileDependents(guid, []);
        _assetInputs.Remove(guid);
        if (_scriptDlls.Remove(guid))
        {
            ScriptsDirty = true;
        }
    }

    /// <summary>Replaces the file -> dependent-asset edges for <paramref name="guid" />.</summary>
    private void SetFileDependents(ScopeId guid, IReadOnlyList<string> inputFiles)
    {
        if (_assetInputs.TryGetValue(guid, out var previous))
        {
            foreach (var file in previous)
            {
                if (_fileDependents.TryGetValue(file, out var set))
                {
                    set.Remove(guid);
                }
            }
        }

        var inputs = inputFiles.Select(Normalize).ToArray();
        _assetInputs[guid] = inputs;
        foreach (var file in inputs)
        {
            if (!_fileDependents.TryGetValue(file, out var set))
            {
                _fileDependents[file] = set = new HashSet<ScopeId>();
            }

            set.Add(guid);
        }
    }

    private static List<string> ToRelativeInputs(string sourcePath, IReadOnlyList<string> inputs)
    {
        var dir = Path.GetDirectoryName(sourcePath)!;
        return inputs.Select(i => Path.GetRelativePath(dir, i)).ToList();
    }

    private static string[] ToAbsoluteInputs(string sourcePath, IReadOnlyList<string> relativeInputs)
    {
        var dir = Path.GetDirectoryName(sourcePath)!;
        return relativeInputs.Select(r => Path.GetFullPath(Path.Combine(dir, r))).ToArray();
    }

    /// <summary>
    ///     Runs the importer, writes the import cache and the <c>.meta</c> sidecar, and records the
    ///     asset's dependency edges. Does not register the produced scope in <see cref="_scopes" />.
    ///     Returns null (and logs) if the importer throws, so a single bad asset is skipped rather than
    ///     failing the whole refresh.
    /// </summary>
    private ImportRun? RunImporter(string path, IAssetImporter importer, ScopeId guid)
    {
        Console.WriteLine($"[AssetDatabase] Importing {path} ");
        var scope = new SerializationScope(guid, NullScopeLoader.Instance);
        var context = new AssetImportContext(path, scope, _cache.ArtifactDirectory(guid));
        try
        {
            importer.Import(context);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[AssetDatabase] Import failed for '{path}', skipping: {exception}");
            return null;
        }

        var mainId = context.MainObject?.Id ?? new LocalId(0);
        var cacheFile = _cache.PathFor(guid);
        var inputFiles = context.InputFiles;

        _cache.Write(guid, SerializeScope(scope));
        AssetMeta.Write(path + ".meta", new AssetMeta
        {
            Guid = guid.Value,
            Importer = importer,
            SourceHash = ImportCache.CombinedHash(path, inputFiles),
            ImporterVersion = importer.Version,
            MainLocalId = mainId.Value,
            Dependencies = context.Dependencies.Select(d => d.Value).ToList(),
            InputFiles = ToRelativeInputs(path, inputFiles)
        });

        _dependencies.SetDependencies(guid, context.Dependencies);
        SetFileDependents(guid, inputFiles);
        Register(guid, path);
        _scopeFile[guid] = cacheFile;

        return new ImportRun(scope, context.MainObject, mainId);
    }

    /// <summary>
    ///     Returns the loaded scope for a GUID, loading it from its scope file on first access.
    ///     Returns null if the GUID maps to neither a loaded scope nor a known scope file.
    /// </summary>
    internal SerializationScope? GetOrLoadScope(ScopeId guid)
    {
        if (_scopes.TryGetValue(guid, out var scope))
        {
            return scope;
        }

        if (!_scopeFile.TryGetValue(guid, out var path))
        {
            return null;
        }

        var loader = new FileScopeLoader(File.ReadAllBytes(path), _typeResolver);
        scope = new SerializationScope(guid, loader);
        _scopes[guid] = scope;
        _loaders[guid] = loader;
        _mainIds[guid] = loader.MainId;
        return scope;
    }

    /// <summary>
    ///     Breadth-first indexes <paramref name="root" /> and every scope it transitively references.
    ///     Each scope is parsed sequentially (never while another parse is in progress).
    /// </summary>
    private void PreloadClosure(ScopeId root)
    {
        var visited = new HashSet<ScopeId>();
        var queue = new Queue<ScopeId>();
        queue.Enqueue(root);

        while (queue.TryDequeue(out var guid))
        {
            if (!visited.Add(guid))
            {
                continue;
            }

            if (GetOrLoadScope(guid) is null)
            {
                continue;
            }

            if (_loaders.GetValueOrDefault(guid) is FileScopeLoader loader)
            {
                foreach (var dependency in loader.CollectReferencedScopes())
                {
                    queue.Enqueue(dependency);
                }
            }
        }
    }

    private static byte[] SerializeScope(SerializationScope scope)
    {
        var writer = new ArrayBufferWriter<byte>();
        var emitter = new Utf8YamlEmitter(writer);
        var serializer = new YamlSerializer(emitter);

        // Emit objects ordered by id so the main/root object (id 0) is the first document.
        foreach (var obj in scope.Objects.OrderBy(pair => pair.Key.Value).Select(pair => pair.Value))
        {
            serializer.BeginObject(obj.Id, obj.GetType());
            obj.Serialize(ref serializer);
            serializer.EndObject();
        }

        return writer.WrittenSpan.ToArray();
    }

    private static string Normalize(string path)
    {
        return Path.GetFullPath(path);
    }

    private readonly record struct ImportRun(SerializationScope Scope, ISerializableObject? Main, LocalId MainId);
}