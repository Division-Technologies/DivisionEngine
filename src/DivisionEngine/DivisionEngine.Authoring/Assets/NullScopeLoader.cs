namespace DivisionEngine.Authoring.Assets;

/// <summary>
///     A no-op loader for scopes that are authored entirely in memory (no backing file yet).
///     Objects are added directly via <c>SerializationScope.Add</c>, so the loader is never asked
///     to materialize or deserialize anything.
/// </summary>
internal sealed class NullScopeLoader : ISerializationScopeLoader
{
    public static readonly NullScopeLoader Instance = new();

    public ISerializableObject? Load(LocalId id)
    {
        return null;
    }

    public void Deserialize(ISerializableObject obj, ISerializedObjectResolver resolver)
    {
    }

    public void Dispose()
    {
    }
}