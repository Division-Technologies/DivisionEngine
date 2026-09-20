namespace DivisionEngine;

/// <summary>
///     Parent/child links expressed as ordinary components rather than a privileged structure
///     (Notes/Core/SceneManagement.md). A child carries <see cref="Parent" /> and <see cref="Sibling" />;
///     a parent carries <see cref="Child" />, holding the ends of an intrusive doubly-linked list of
///     its children. Attaching, detaching and reordering are therefore O(1), children enumerate in
///     the order they were attached, and every link lives in chunk storage like any other component.
///     Destroying an entity destroys its whole subtree; see <see cref="HierarchyIntegrity" />.
///     These operations are structural changes: call them on the main thread or from a job that
///     declared <see cref="Access.WriteStructure" />.
/// </summary>
public static class Hierarchy
{
    /// <summary>Creates an entity with an identity <see cref="LocalTransform" /> and a <see cref="WorldTransform" />.</summary>
    public static Entity CreateTransform(this World world)
    {
        return world.CreateTransform(LocalTransform.Identity);
    }

    /// <summary>Creates an entity carrying both transform components, in one structural change.</summary>
    public static Entity CreateTransform(this World world, in LocalTransform local)
    {
        var entity = world.CreateEntity(ComponentType<LocalTransform>.Id, ComponentType<WorldTransform>.Id);
        world.GetComponent<LocalTransform>(entity) = local;
        world.GetComponent<WorldTransform>(entity).Value = local.ToMatrix();
        return entity;
    }

    /// <summary>Gives an existing entity both transform components.</summary>
    public static void AddTransform(this World world, Entity entity, in LocalTransform local)
    {
        world.AddComponent(entity, local);
        world.AddComponent(entity, new WorldTransform { Value = local.ToMatrix() });
    }

    /// <summary>The entity's parent, or <see cref="Entity.Null" /> if it is a root.</summary>
    public static Entity GetParent(this World world, Entity entity)
    {
        return world.HasComponent<Parent>(entity) ? world.GetComponentReadOnly<Parent>(entity).Value : Entity.Null;
    }

    public static bool HasChildren(this World world, Entity entity)
    {
        return world.HasComponent<Child>(entity);
    }

    /// <summary>
    ///     The entity's children in attachment order. The sequence walks live component data, so do
    ///     not change the hierarchy while enumerating it.
    /// </summary>
    public static ChildList GetChildren(this World world, Entity entity)
    {
        var first = world.HasComponent<Child>(entity) ? world.GetComponentReadOnly<Child>(entity).First : Entity.Null;
        return new ChildList(world, first);
    }

    /// <summary>
    ///     Attaches <paramref name="entity" /> to the end of <paramref name="parent" />'s children,
    ///     detaching it from its current parent first. A null <paramref name="parent" /> is the same as
    ///     <see cref="ClearParent" />. Cycles are rejected.
    ///     The local transform is kept as-is, so the entity's world position follows its new parent.
    /// </summary>
    public static void SetParent(this World world, Entity entity, Entity parent)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (parent.IsNull)
        {
            world.ClearParent(entity);
            return;
        }

        if (!world.IsAlive(entity))
        {
            throw new EntityNotAliveException(entity);
        }

        if (!world.IsAlive(parent))
        {
            throw new EntityNotAliveException(parent);
        }

        if (entity == parent)
        {
            throw new InvalidOperationException($"{entity} cannot be its own parent.");
        }

        for (var ancestor = world.GetParent(parent); !ancestor.IsNull; ancestor = world.GetParent(ancestor))
        {
            if (ancestor == entity)
            {
                throw new InvalidOperationException($"Parenting {entity} to {parent} would close a cycle.");
            }
        }

        world.AddStructuralHook(HierarchyIntegrity.Instance);

        var current = world.GetParent(entity);
        if (current == parent)
        {
            return;
        }

        if (current.IsNull)
        {
            world.AddComponent(entity, new Parent { Value = parent });
            world.AddComponent<Sibling>(entity);
        }
        else
        {
            Unlink(world, entity, current);
            world.GetComponent<Parent>(entity).Value = parent;
        }

        Link(world, entity, parent);
    }

    /// <summary>Detaches the entity from its parent, making it a root. A no-op for entities that are already roots.</summary>
    public static void ClearParent(this World world, Entity entity)
    {
        if (!world.HasComponent<Parent>(entity))
        {
            return;
        }

        var parent = world.GetComponentReadOnly<Parent>(entity).Value;
        if (world.IsAlive(parent))
        {
            Unlink(world, entity, parent);
        }

        world.RemoveComponent<Parent>(entity);
        world.RemoveComponent<Sibling>(entity);
    }

    /// <summary>Appends an already-<see cref="Parent" />ed entity to the end of its parent's list.</summary>
    private static void Link(World world, Entity entity, Entity parent)
    {
        if (!world.HasComponent<Child>(parent))
        {
            // First child: one structural change on the parent, then the (empty) links on the child.
            world.AddComponent(parent, new Child { First = entity, Last = entity });
            world.GetComponent<Sibling>(entity) = default;
            return;
        }

        ref var list = ref world.GetComponent<Child>(parent);
        var last = list.Last;
        list.Last = entity;

        world.GetComponent<Sibling>(entity) = new Sibling { Next = Entity.Null, Previous = last };
        world.GetComponent<Sibling>(last).Next = entity;
    }

    /// <summary>Removes an entity from its parent's list, leaving its <see cref="Parent" /> component in place.</summary>
    private static void Unlink(World world, Entity entity, Entity parent)
    {
        var links = world.GetComponentReadOnly<Sibling>(entity);
        ref var list = ref world.GetComponent<Child>(parent);

        if (links.Previous.IsNull)
        {
            list.First = links.Next;
        }
        else
        {
            world.GetComponent<Sibling>(links.Previous).Next = links.Next;
        }

        if (links.Next.IsNull)
        {
            list.Last = links.Previous;
        }
        else
        {
            world.GetComponent<Sibling>(links.Next).Previous = links.Previous;
        }

        var becameEmpty = list.First.IsNull;
        world.GetComponent<Sibling>(entity) = default;

        // Structural change last: it invalidates the references taken above.
        if (becameEmpty)
        {
            world.RemoveComponent<Child>(parent);
        }
    }

    /// <summary>The children of one entity, in attachment order.</summary>
    public readonly struct ChildList
    {
        private readonly World _world;
        private readonly Entity _first;

        internal ChildList(World world, Entity first)
        {
            _world = world;
            _first = first;
        }

        public Enumerator GetEnumerator()
        {
            return new Enumerator(_world, _first);
        }

        public struct Enumerator
        {
            private readonly World _world;
            private Entity _next;

            internal Enumerator(World world, Entity first)
            {
                _world = world;
                _next = first;
                Current = Entity.Null;
            }

            public Entity Current { get; private set; }

            public bool MoveNext()
            {
                if (_next.IsNull)
                {
                    return false;
                }

                Current = _next;
                _next = _world.GetComponentReadOnly<Sibling>(_next).Next;
                return true;
            }
        }
    }
}

/// <summary>
///     Keeps the hierarchy links consistent when entities disappear: destroying an entity destroys
///     its children (recursively) and detaches it from its parent, so no dangling
///     <see cref="Parent" /> or <see cref="Child" /> handle is ever left behind.
///     Registered automatically the first time <see cref="Hierarchy.SetParent" /> runs on a world.
/// </summary>
public sealed class HierarchyIntegrity : IStructuralHook
{
    public static readonly HierarchyIntegrity Instance = new();

    private HierarchyIntegrity()
    {
    }

    public void OnBeforeDestroy(World world, Entity entity)
    {
        // Each child detaches itself as it is destroyed, shortening the list, so taking the head
        // again every round both terminates and avoids walking freed links.
        while (world.HasComponent<Child>(entity))
        {
            var first = world.GetComponentReadOnly<Child>(entity).First;
            if (first.IsNull)
            {
                world.RemoveComponent<Child>(entity);
                break;
            }

            world.DestroyEntity(first);
        }

        world.ClearParent(entity);
    }
}
