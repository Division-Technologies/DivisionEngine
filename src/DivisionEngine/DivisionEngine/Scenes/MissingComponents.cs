namespace DivisionEngine;

/// <summary>
///     One component that could not be loaded, kept exactly as it was written.
/// </summary>
/// <param name="TypeId">The persisted type id it was saved under.</param>
/// <param name="Value">The value as written, or null if it had none (a tag).</param>
/// <param name="Reason">Why it could not be loaded.</param>
public sealed record MissingComponent(string TypeId, byte[]? Value, string Reason);

/// <summary>
///     The components of an entity that a scene or a reload could not load: a type that no longer
///     exists (a deleted or renamed script), or a value that no longer reads as its type (a field
///     whose type changed).
///     <para>
///         They are kept rather than dropped, and written back unchanged the next time the entity is
///         saved or carried across a reload. An edit that is undone — a script deleted and restored, a
///         type renamed and renamed back — therefore costs no data, and once the type loads again the
///         component comes back by itself.
///     </para>
///     <para>
///         The one thing that does not survive is a reference to another entity inside a missing
///         component: its schema is unknown, so the reference cannot be rewritten. It stays correct
///         across a script reload, where every entity keeps its handle, but not when the entity is
///         loaded into another world.
///     </para>
/// </summary>
[Component]
[TypeId("b3c1f6a2-8d47-4e19-9f05-6a2e7c4d1b90")]
public sealed class MissingComponents : ISerializable
{
    private readonly List<MissingComponent> _entries = new();

    public IReadOnlyList<MissingComponent> Entries => _entries;

    // Written and read by EntityScene, which expands the entries back into the components they
    // were; nothing else serializes this type.
    void ISerializable.Serialize<T>(ref T serializer)
    {
    }

    void ISerializable.Deserialize<T>(ref T deserializer)
    {
    }

    internal void Add(MissingComponent entry)
    {
        _entries.RemoveAll(e => e.TypeId == entry.TypeId);
        _entries.Add(entry);
    }
}