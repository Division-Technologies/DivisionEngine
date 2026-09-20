using System.Numerics;

namespace DivisionEngine;

/// <summary>
///     Turns <see cref="LocalTransform" /> into <see cref="WorldTransform" /> for every entity in a
///     hierarchy, during <see cref="PhaseId.TransformPropagation" />.
///     Roots (transform entities with no <see cref="Parent" />) are walked chunk-parallel; each batch
///     then descends its own subtrees, which are disjoint because an entity has at most one parent,
///     so no two workers ever write the same <see cref="WorldTransform" /> and the result does not
///     depend on how the chunks were split.
///     An entity in the hierarchy that has no <see cref="LocalTransform" /> contributes no transform
///     of its own: it is passed through, and its children are placed relative to the nearest ancestor
///     that does have one. That keeps the parent/child links usable for grouping entities that are
///     not in the transform hierarchy at all.
/// </summary>
public sealed class TransformPropagationSystem : IJobSystem
{
    private EntityQuery? _roots;

    public void Schedule(in JobSchedulingContext context)
    {
        Schedule(context.Graph, context.World);
    }

    /// <summary>Issues the propagation job directly, for callers that drive a <see cref="JobGraph" /> without a frame loop.</summary>
    public JobHandle Schedule(JobGraph graph, World world)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(world);

        var roots = _roots ??= world.Query()
            .With<LocalTransform>()
            .With<WorldTransform>()
            .Without<Parent>()
            .Build();

        return graph.ScheduleChunks(
            "TransformPropagation",
            roots,
            Access.Read<LocalTransform>().Write<WorldTransform>().Read<Child>().Read<Sibling>(),
            static (in JobContext job, ArchetypeChunk chunk) => PropagateRoots(job.World, chunk));
    }

    private static void PropagateRoots(World world, ArchetypeChunk chunk)
    {
        var locals = chunk.GetReadOnlySpan<LocalTransform>();
        var worlds = chunk.GetSpan<WorldTransform>();
        var hasChildren = chunk.Has<Child>();
        var children = hasChildren ? chunk.GetReadOnlySpan<Child>() : default;

        for (var i = 0; i < chunk.Count; i++)
        {
            var matrix = locals[i].ToMatrix();
            worlds[i].Value = matrix;
            if (hasChildren)
            {
                PropagateSubtree(world, children[i].First, matrix);
            }
        }
    }

    /// <summary>
    ///     Walks a sibling chain iteratively and descends one level per recursion, so the stack grows
    ///     with the depth of the hierarchy rather than with the number of entities in it.
    /// </summary>
    private static void PropagateSubtree(World world, Entity entity, in Matrix4x4 parent)
    {
        while (!entity.IsNull)
        {
            var matrix = parent;
            if (world.HasComponent<LocalTransform>(entity))
            {
                matrix = world.GetComponentReadOnly<LocalTransform>(entity).ToMatrix() * parent;
                if (world.HasComponent<WorldTransform>(entity))
                {
                    world.GetComponent<WorldTransform>(entity).Value = matrix;
                }
            }

            if (world.HasComponent<Child>(entity))
            {
                PropagateSubtree(world, world.GetComponentReadOnly<Child>(entity).First, matrix);
            }

            entity = world.GetComponentReadOnly<Sibling>(entity).Next;
        }
    }
}
