using DivisionEngine;

namespace DivisionEngine.Authoring.Assets;

/// <summary>
///     Resolves <see cref="GlobalId" /> references across all assets known to an
///     <see cref="AssetDatabase" />, lazily loading the owning scope (and therefore the referenced
///     asset file) on demand.
///     <para>
///         Mirrors the two-pass strategy of <c>ObjectManager.Resolver</c>: when a reference causes a
///         new object to be materialized, it is queued and deserialized after the current object
///         finishes, so reference cycles resolve without infinite recursion.
///     </para>
/// </summary>
internal sealed class AssetResolver(AssetDatabase database) : ISerializedObjectResolver
{
    private readonly Queue<ISerializableObject> _pendingReload = new();

    public ISerializableObject? Resolve(GlobalId id)
    {
        var scope = database.GetOrLoadScope(id.ScopeId);
        if (scope is null) return null;

        var obj = scope.Resolve(id.LocalId, out var needReload);
        if (obj is not null && needReload) _pendingReload.Enqueue(obj);
        return obj;
    }

    /// <summary>
    ///     Deserializes every object queued during resolution. Deserializing an object may queue
    ///     further objects (its own references), which this loop drains in turn.
    /// </summary>
    public void DrainPending()
    {
        while (_pendingReload.TryDequeue(out var obj)) obj.Scope.Reload(obj, this);
    }
}
