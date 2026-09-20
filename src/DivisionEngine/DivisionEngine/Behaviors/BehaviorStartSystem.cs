namespace DivisionEngine;

/// <summary>
///     Starts a turn for every <see cref="Behavior" /> component that does not have one running.
///     <para>
///         This is what makes a behavior survive things that its own suspended state cannot. A turn
///         lives in a compiler-generated state machine, which no scene file can hold and no assembly
///         swap can carry; what persists instead is the behavior <em>component</em>, and this system
///         turns that back into a running turn. Loading a scene, instantiating a prefab, reloading
///         user scripts and simply adding a behavior to an entity all arrive here by the same path.
///     </para>
///     <para>
///         Idempotent by construction: a behavior whose turn is still alive is skipped, so running it
///         every frame costs one pass over the archetypes that hold behaviors.
///     </para>
/// </summary>
public sealed class BehaviorStartSystem : ISystem
{
    private readonly List<(Entity Entity, Behavior Behavior)> _pending = new();

    public void Execute(ref FrameContext ctx)
    {
        var world = ctx.World;
        _pending.Clear();

        foreach (var archetype in world.Archetypes)
        {
            foreach (var type in archetype.Types)
            {
                if (!ComponentTypeRegistry.GetInfo(type).IsBehavior)
                {
                    continue;
                }

                var slot = archetype.SlotOf(type);
                foreach (var chunk in archetype.Chunks)
                {
                    var instances = chunk.GetManagedArray(slot);
                    var entities = chunk.Entities;
                    for (var i = 0; i < chunk.Count; i++)
                    {
                        if (instances[i] is Behavior { IsRunning: false } behavior)
                        {
                            _pending.Add((entities[i], behavior));
                        }
                    }
                }
            }
        }

        // Turn ids are assigned in start order and decide who commits first, so the order has to come
        // from the world's contents rather than from how its chunks happen to be laid out.
        _pending.Sort(static (a, b) => a.Entity.Index.CompareTo(b.Entity.Index));
        foreach (var (entity, behavior) in _pending)
        {
            ctx.Graph.Start(entity, behavior);
        }

        _pending.Clear();
    }
}
