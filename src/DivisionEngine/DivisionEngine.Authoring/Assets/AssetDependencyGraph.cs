namespace DivisionEngine.Authoring.Assets;

/// <summary>
///     Tracks dependency edges between assets (by GUID), recorded during import via
///     <see cref="AssetImportContext.DependsOnAsset" />. The reverse edges drive partial re-import:
///     when an asset changes, every asset that transitively depends on it is re-imported.
/// </summary>
internal sealed class AssetDependencyGraph
{
    private readonly Dictionary<ScopeId, HashSet<ScopeId>> _forward = new();
    private readonly Dictionary<ScopeId, HashSet<ScopeId>> _reverse = new();

    /// <summary>Replaces the dependency edges of <paramref name="asset" />.</summary>
    public void SetDependencies(ScopeId asset, IEnumerable<ScopeId> dependencies)
    {
        // Remove the asset's previous forward edges and their reverse counterparts.
        if (_forward.TryGetValue(asset, out var previous))
            foreach (var dependency in previous)
                if (_reverse.TryGetValue(dependency, out var dependents))
                    dependents.Remove(asset);

        var edges = _forward[asset] = new HashSet<ScopeId>();
        foreach (var dependency in dependencies)
        {
            if (dependency == asset) continue;
            edges.Add(dependency);
            if (!_reverse.TryGetValue(dependency, out var dependents))
                _reverse[dependency] = dependents = new HashSet<ScopeId>();
            dependents.Add(asset);
        }
    }

    /// <summary>The assets that directly depend on <paramref name="asset" />.</summary>
    public IReadOnlyCollection<ScopeId> GetDependents(ScopeId asset)
    {
        return _reverse.TryGetValue(asset, out var dependents) ? dependents : Array.Empty<ScopeId>();
    }

    /// <summary>Removes an asset and all edges touching it (used when an asset is deleted).</summary>
    public void Remove(ScopeId asset)
    {
        if (_forward.Remove(asset, out var dependencies))
            foreach (var dependency in dependencies)
                if (_reverse.TryGetValue(dependency, out var dependents))
                    dependents.Remove(asset);

        if (_reverse.Remove(asset, out var reverseDependents))
            foreach (var dependent in reverseDependents)
                if (_forward.TryGetValue(dependent, out var forwardEdges))
                    forwardEdges.Remove(asset);
    }
}