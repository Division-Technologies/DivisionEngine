namespace DivisionEngine;

internal sealed class ObjectManager
{
    private Dictionary<ScopeId, SerializationScope> Scopes { get; } = new();

    internal void AddScope(SerializationScope scope)
    {
        Scopes.Add(scope.Id, scope);
    }

    internal ObjectManager ReloadClasses()
    {
        var newManager = new ObjectManager();

        foreach (var (scopeId, scope) in Scopes)
        {
            var newScope = new SerializationScope(scope);
            newManager.AddScope(newScope);
        }

        var resolver = new Resolver(newManager, false);

        foreach (var (scopeId, scope) in Scopes) newManager.Scopes[scopeId].Transfer(scope, resolver);
        

        return newManager;
    }

    private class Resolver(ObjectManager objectManager, bool allowReload) : ISerializedObjectResolver
    {
        private readonly Queue<ISerializableObject> _pendingReload = new();

        public ISerializableObject? Resolve(GlobalId id)
        {
            if (objectManager.Scopes.TryGetValue(id.ScopeId, out var scope) &&
                scope.Resolve(id.LocalId, out var needReload) is { } result)
            {
                if (needReload)
                {
                    if (allowReload) throw new InvalidOperationException();
                    _pendingReload.Enqueue(result);
                }

                return result;
            }

            return null;
        }

        public void ReloadAll()
        {
            while (_pendingReload.TryDequeue(out var result)) result.Scope.Reload(result, this);
        }
    }
}