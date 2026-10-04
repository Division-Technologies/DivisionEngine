namespace DivisionEngine;

/// <summary>
///     The engine-side half of a script reload: getting a world's contents safely across an assembly
///     swap (Notes/Core/Scripting.md).
///     <para>
///         The order matters and is easy to get wrong, which is why it lives here rather than in the
///         caller. A snapshot has to be taken <em>before</em> the old types go away, because taking it
///         is what turns raw chunk bytes into fields; the world has to be emptied before any
///         component type is dropped, because archetypes are keyed on component type ids; and the
///         types have to be dropped before the swap, because a live <see cref="Type" /> keeps its
///         whole load context alive and the unload would never complete.
///     </para>
///     <para>
///         Capturing and releasing are separate steps so that a failure costs nothing:
///         <see cref="Capture" /> only reads, and whatever makes it throw leaves the world untouched.
///         <see cref="Release" /> is the point of no return, and it cannot fail.
///     </para>
///     <para>
///         <see cref="AfterSwap" /> rebuilds each entity under the handle it had, so nothing that
///         holds one — the editor's selection, a system's cached target — notices the reload. It
///         resolves component types by their persisted ids, so a component the user edited still
///         loads, with its fields landing where they now belong; one that no longer loads is kept
///         aside on its entity (<see cref="MissingComponents" />) rather than stopping the reload.
///         What cannot survive is anything that was never in a component: <see cref="Behavior" />
///         continuations in particular, which is why the standing rule is to keep durable state in
///         components and let turns restart.
///     </para>
/// </summary>
public static class WorldReload
{
    /// <summary>
    ///     Writes the world's contents to a snapshot that holds nothing of the component types, so it
    ///     can be kept across the swap. Does not change the world.
    /// </summary>
    public static EntityScene Capture(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return EntityScene.CaptureFrom(world);
    }

    /// <summary>
    ///     Empties the world and drops the component types belonging to collectible load contexts.
    ///     Call after <see cref="Capture" /> and immediately before swapping the user assemblies.
    /// </summary>
    public static void Release(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        world.Clear();
        ComponentTypeRegistry.UnregisterUnloadable();
    }

    /// <summary><see cref="Capture" /> then <see cref="Release" />.</summary>
    public static EntityScene BeforeSwap(World world)
    {
        var snapshot = Capture(world);
        Release(world);
        return snapshot;
    }

    /// <summary>
    ///     Rebuilds the world from a snapshot taken by <see cref="Capture" />, against the component
    ///     types registered now, with every entity under its old handle. Call after the new assemblies
    ///     are loaded and their module initializers have run.
    /// </summary>
    /// <param name="references">Resolves asset references held by managed components.</param>
    /// <param name="warnings">Receives what could not be loaded and was kept aside.</param>
    /// <returns>The rebuilt entities, in the snapshot's order.</returns>
    public static Entity[] AfterSwap(World world, EntityScene snapshot, ISerializedObjectResolver? references = null,
        ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.ApplyTo(world, references, warnings, true);
    }
}