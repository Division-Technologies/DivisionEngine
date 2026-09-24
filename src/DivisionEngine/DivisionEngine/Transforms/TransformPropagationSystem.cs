using System.Collections.Concurrent;
using System.Numerics;

namespace DivisionEngine;

/// <summary>
///     Turns <see cref="LocalTransform" /> into <see cref="WorldTransform" /> for every entity in a
///     hierarchy, during <see cref="PhaseId.TransformPropagation" />.
///     An entity in the hierarchy that has no <see cref="LocalTransform" /> contributes no transform
///     of its own: it is passed through, and its children are placed relative to the nearest ancestor
///     that does have one, or to the world if none does. That keeps the parent/child links usable for
///     grouping entities without giving the group a transform.
/// </summary>
/// <remarks>
///     <para>
///         Work is split so that no single item can hold the frame. Roots (transform entities with no
///         <see cref="Parent" />) are walked chunk-parallel and each batch descends its own subtrees,
///         but a walk stops after <see cref="WorkBudget" /> entities and hands whatever it has not
///         reached to the next pass, one entity per item. Passes are ordered against each other by
///         their shared write to <see cref="WorldTransform" />; within a pass the items run in
///         parallel.
///     </para>
///     <para>
///         Without that, the unit of parallelism was a chunk of roots
///         <em>
///             plus everything underneath
///             it
///         </em>
///         , so a scene with one heavy root ran almost serially no matter how many workers
///         were free - 100 roots with one of them holding 80% of the entities measured 1.26x on eight
///         threads, against the 1.25x that Amdahl allows for an indivisible 80%. Deferring by
///         individual child is what makes that subtree divisible. See Notes/Core/SceneManagement.md.
///     </para>
///     <para>
///         Subtrees are disjoint because an entity has at most one parent, so no two workers ever
///         write the same <see cref="WorldTransform" /> and the result does not depend on how the
///         work was split. Deferred items are collected in whatever order the workers produce them;
///         that order reaches no result, only the batching.
///     </para>
///     <para>
///         The walk itself never tests for <see cref="Static" />. It does not have to: the hierarchy
///         API keeps a static entity's parent static too, so a static subtree is only ever reachable
///         through a static root, and the query drops those. Testing it per entity would mean one
///         more of the random lookups that already dominate the walk - it measured around 7% - to
///         rule out a case that cannot arise. Adding the tag with <c>AddComponent&lt;Static&gt;</c>
///         instead of <c>MakeStatic</c> breaks that reasoning.
///     </para>
/// </remarks>
public sealed class TransformPropagationSystem : IJobSystem
{
    /// <summary>
    ///     Entities one item may walk before it starts deferring. Small enough that a heavy subtree
    ///     is broken up early, large enough that ordinary subtrees are walked in one go, depth-first,
    ///     without touching the deferral path at all.
    /// </summary>
    private const int WorkBudget = 1024;

    /// <summary>
    ///     Budgeted passes after the roots. Each one can break the previous one's leftovers up
    ///     further; the pass after them walks whatever is left with no budget, so the number of
    ///     passes bounds how finely work is divided, never whether it finishes. Each costs a node
    ///     every frame whether or not anything was deferred, which is why there are not more.
    /// </summary>
    private const int DeferredPasses = 1;

    private readonly Frontier[] _frontiers = CreateFrontiers();

    private EntityQuery? _branchingRoots;
    private RangeJob[]? _deferredJobs;
    private EntityQuery? _roots;

    /// <summary>Cached so that issuing costs no closure allocation per frame.</summary>
    private ChunkJob? _rootsJob;

    private World? _world;

    public void Schedule(in JobSchedulingContext context)
    {
        Schedule(context.Graph, context.World);
    }

    /// <summary>Issues the propagation jobs directly, for callers that drive a <see cref="JobGraph" /> without a frame loop.</summary>
    /// <returns>A handle to the last pass, which completes once every world transform is written.</returns>
    public JobHandle Schedule(JobGraph graph, World world)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(world);

        if (_world != world)
        {
            // The queries belong to the world they were built from.
            _world = world;
            _roots = null;
            _branchingRoots = null;
        }

        // Without<Static> costs nothing per entity: query matching is per archetype, so static
        // roots are excluded by never looking at their chunks. A root without a LocalTransform is
        // still a root when it has children: it is passed through like any other entity without
        // one, so its children are placed relative to the world.
        var roots = _roots ??= world.Query()
            .WithAny<LocalTransform>()
            .WithAny<Child>()
            .Without<Parent>()
            .Without<Static>()
            .Build();

        var branching = _branchingRoots ??= world.Query()
            .With<Child>()
            .Without<Parent>()
            .Without<Static>()
            .Build();

        var access = Access.Read<LocalTransform>().Write<WorldTransform>().Read<Child>().Read<Sibling>();
        var handle = graph.ScheduleChunks("TransformPropagation", roots, access, RootsJob());

        // Nothing can be deferred when no root has a child, because the subtree walk never runs.
        // Worth testing: each pass is a node and a barrier whether or not it has work, and a world
        // with no hierarchy at all would otherwise pay for both every frame.
        if (branching.CalculateChunkCount() == 0)
        {
            return handle;
        }

        var jobs = DeferredJobs();
        for (var pass = 0; pass < _frontiers.Length; pass++)
        {
            handle = graph.ScheduleBatches($"TransformPropagation.Deferred{pass}", _frontiers[pass].Take, access,
                jobs[pass]);
        }

        return handle;
    }

    private ChunkJob RootsJob()
    {
        var first = _frontiers[0];
        return _rootsJob ??= (in job, chunk) => PropagateRoots(job.World, chunk, first);
    }

    private RangeJob[] DeferredJobs()
    {
        if (_deferredJobs is not null)
        {
            return _deferredJobs;
        }

        var jobs = new RangeJob[_frontiers.Length];
        for (var pass = 0; pass < jobs.Length; pass++)
        {
            var input = _frontiers[pass];
            // The last pass has nowhere to defer to, so it runs to completion.
            var overflow = pass + 1 < _frontiers.Length ? _frontiers[pass + 1] : null;
            var itemBudget = overflow is null ? int.MaxValue : WorkBudget;

            jobs[pass] = (in job, start, end) =>
            {
                for (var i = start; i < end; i++)
                {
                    ref readonly var item = ref input[i];
                    var budget = itemBudget;
                    PropagateOne(job.World, item.Entity, item.Parent, overflow, ref budget);
                }
            };
        }

        return _deferredJobs = jobs;
    }

    private static Frontier[] CreateFrontiers()
    {
        // One per budgeted pass, plus the unbudgeted pass that drains whatever they defer.
        var frontiers = new Frontier[DeferredPasses + 1];
        for (var i = 0; i < frontiers.Length; i++)
        {
            frontiers[i] = new Frontier();
        }

        return frontiers;
    }

    private static void PropagateRoots(World world, ArchetypeChunk chunk, Frontier overflow)
    {
        var hasLocal = chunk.Has<LocalTransform>();
        var hasWorld = hasLocal && chunk.Has<WorldTransform>();
        var locals = hasLocal ? chunk.GetReadOnlySpan<LocalTransform>() : default;
        var worlds = hasWorld ? chunk.GetSpan<WorldTransform>() : default;
        var hasChildren = chunk.Has<Child>();
        var children = hasChildren ? chunk.GetReadOnlySpan<Child>() : default;

        for (var i = 0; i < chunk.Count; i++)
        {
            var matrix = hasLocal ? locals[i].ToMatrix() : Matrix4x4.Identity;
            if (hasWorld)
            {
                worlds[i].Value = matrix;
            }

            if (!hasChildren)
            {
                continue;
            }

            // A budget per root, not per batch: a batch of light roots should not be pushed into
            // the deferral path just because it is long.
            var budget = WorkBudget;
            PropagateSubtree(world, children[i].First, matrix, overflow, ref budget);
        }
    }

    /// <summary>
    ///     Settles one subtree immediately, outside the frame loop. Used when marking a subtree
    ///     <see cref="Static" />, which is the last chance to compute those world transforms.
    /// </summary>
    internal static void PropagateFrom(World world, Entity entity, in Matrix4x4 parent)
    {
        var budget = int.MaxValue;
        PropagateOne(world, entity, parent, null, ref budget);
    }

    /// <summary>Writes one entity's world transform and descends into it.</summary>
    private static void PropagateOne(World world, Entity entity, in Matrix4x4 parent, Frontier? overflow,
        ref int budget)
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
            PropagateSubtree(world, world.GetComponentReadOnly<Child>(entity).First, matrix, overflow, ref budget);
        }
    }

    /// <summary>
    ///     Walks a sibling chain iteratively and descends one level per recursion, so the stack grows
    ///     with the depth of the hierarchy rather than with the number of entities in it.
    /// </summary>
    private static void PropagateSubtree(World world, Entity entity, in Matrix4x4 parent, Frontier? overflow,
        ref int budget)
    {
        while (!entity.IsNull)
        {
            if (overflow is not null && budget <= 0)
            {
                // Hand the rest of this chain over one entity at a time, rather than as "the
                // remainder of the list". A list passed whole would be a single work item again,
                // and a wide chain of leaves - one root with tens of thousands of children - is
                // exactly the shape that made propagation serial.
                for (var rest = entity; !rest.IsNull; rest = world.GetComponentReadOnly<Sibling>(rest).Next)
                {
                    overflow.Add(rest, parent);
                }

                return;
            }

            var matrix = parent;
            if (world.HasComponent<LocalTransform>(entity))
            {
                matrix = world.GetComponentReadOnly<LocalTransform>(entity).ToMatrix() * parent;
                if (world.HasComponent<WorldTransform>(entity))
                {
                    world.GetComponent<WorldTransform>(entity).Value = matrix;
                }
            }

            budget--;
            if (world.HasComponent<Child>(entity))
            {
                PropagateSubtree(world, world.GetComponentReadOnly<Child>(entity).First, matrix, overflow, ref budget);
            }

            entity = world.GetComponentReadOnly<Sibling>(entity).Next;
        }
    }

    /// <summary>Work one pass deferred for the next. Filled from several workers, drained by one.</summary>
    private sealed class Frontier
    {
        private readonly ConcurrentQueue<Deferred> _deferred = new();
        private Deferred[] _items = [];

        public ref readonly Deferred this[int index] => ref _items[index];

        public void Add(Entity entity, in Matrix4x4 parent)
        {
            _deferred.Enqueue(new Deferred(entity, parent));
        }

        /// <summary>
        ///     Materialises what the previous pass deferred, and returns how many items there are.
        ///     Safe to read without synchronisation because the pass that fills this frontier is
        ///     ordered strictly before the pass that drains it, by their shared write to
        ///     <see cref="WorldTransform" />.
        /// </summary>
        public int Take()
        {
            if (_deferred.IsEmpty)
            {
                _items = [];
                return 0;
            }

            _items = _deferred.ToArray();
            _deferred.Clear();
            return _items.Length;
        }

        internal readonly record struct Deferred(Entity Entity, Matrix4x4 Parent);
    }
}