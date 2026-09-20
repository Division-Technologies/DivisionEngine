namespace DivisionEngine;

/// <summary>
///     Starts a turn for every <see cref="Behaviour" /> component that does not have one running.
///     <para>
///         This is what makes a behaviour survive things that its own suspended state cannot. A turn
///         lives in a compiler-generated state machine, which no scene file can hold and no assembly
///         swap can carry; what persists instead is the behaviour <em>component</em>, and this system
///         turns that back into a running turn. Loading a scene, instantiating a prefab, reloading
///         user scripts and simply adding a behaviour to an entity all arrive here by the same path.
///     </para>
///     <para>
///         Idempotent by construction: a behaviour whose turn is still alive is skipped, so running it
///         every frame costs one pass over the archetypes that hold behaviours.
///     </para>
/// </summary>
public sealed class BehaviourStartSystem : ISystem
{
    private readonly List<(Entity Entity, Behaviour Behaviour)> _pending = new();

    public void Execute(ref FrameContext ctx)
    {
        var world = ctx.World;
        _pending.Clear();

        foreach (var archetype in world.Archetypes)
        {
            foreach (var type in archetype.Types)
            {
                if (!ComponentTypeRegistry.GetInfo(type).IsBehaviour)
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
                        if (instances[i] is Behaviour { IsRunning: false } behaviour)
                        {
                            _pending.Add((entities[i], behaviour));
                        }
                    }
                }
            }
        }

        // Turn ids are assigned in start order and decide who commits first, so the order has to come
        // from the world's contents rather than from how its chunks happen to be laid out.
        _pending.Sort(static (a, b) => a.Entity.Index.CompareTo(b.Entity.Index));
        foreach (var (entity, behaviour) in _pending)
        {
            ctx.Graph.Start(entity, behaviour);
        }

        _pending.Clear();
    }
}
