namespace DivisionEngine;

/// <summary>
///     The engine-side half of a script reload: getting a world's contents safely across an assembly
///     swap (Notes/Core/Scripting.md).
///     <para>
///         The order matters and is easy to get wrong, which is why it lives here rather than in the
///         caller. A snapshot has to be written <em>before</em> the old types go away, because writing
///         it is what turns raw chunk bytes into fields; the world has to be emptied before any
///         component type is dropped, because archetypes are keyed on component type ids; and the
///         types have to be dropped before the swap, because a live <see cref="Type" /> keeps its
///         whole load context alive and the unload would never complete.
///     </para>
///     <para>
///         Reading the snapshot back after the swap resolves component types by their persisted ids,
///         so a component the user edited — a field added, removed or reordered — still loads, with
///         the fields landing where they now belong. What cannot survive is anything that was never
///         in a component: <see cref="Behaviour" /> continuations in particular, which is why the
///         standing rule is to keep durable state in components and let turns restart.
///     </para>
/// </summary>
public static class WorldReload
{
    /// <summary>
    ///     Writes the world to a snapshot, empties it, and drops the component types belonging to
    ///     collectible load contexts. Call immediately before swapping the user assemblies.
    /// </summary>
    public static byte[] BeforeSwap(World world)
    {
        ArgumentNullException.ThrowIfNull(world);

        var snapshot = EntityScene.CaptureFrom(world).ToBytes();
        world.Clear();
        ComponentTypeRegistry.UnregisterUnloadable();
        return snapshot;
    }

    /// <summary>
    ///     Rebuilds the world from a snapshot taken by <see cref="BeforeSwap" />, against the component
    ///     types registered now. Call after the new assemblies are loaded and their module
    ///     initializers have run.
    /// </summary>
    public static Entity[] AfterSwap(World world, ReadOnlyMemory<byte> snapshot)
    {
        ArgumentNullException.ThrowIfNull(world);
        return EntityScene.FromBytes(snapshot).ApplyTo(world);
    }
}
