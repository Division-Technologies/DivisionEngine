namespace DivisionEngine;

/// <summary>
///     Observes structural changes so that invariants spanning several entities can be maintained
///     without the world knowing about them. Hooks keep features like the parent/child links
///     (<see cref="Hierarchy" />) out of <see cref="World" /> itself.
///     Hooks run on the thread performing the change, inside whatever job declared it, and may make
///     further structural changes — including recursive ones.
/// </summary>
public interface IStructuralHook
{
    /// <summary>
    ///     Called before <paramref name="entity" /> is removed from <paramref name="world" />, while it
    ///     is still alive and its components are still readable. A hook may destroy other entities; if
    ///     it destroys <paramref name="entity" /> itself, the pending destruction is dropped.
    /// </summary>
    void OnBeforeDestroy(World world, Entity entity);
}