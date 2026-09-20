namespace DivisionEngine;

/// <summary>One entity-level access declaration: a component of one entity.</summary>
public readonly record struct EntityComponentAccess(Entity Entity, ComponentTypeId Type)
{
    public override string ToString()
    {
        return $"{Entity}.{ComponentTypeRegistry.GetInfo(Type).Type.Name}";
    }
}

/// <summary>
///     The resources a job reads and writes, declared before it runs. The scheduler derives job
///     dependencies from these sets (shared reads, exclusive writes) and the debug safety checks
///     verify that the job touches nothing else.
///     Type-level entries cover a whole component type (systems); entity-level entries cover one
///     component of one entity (behaviour segments). Any component access implies a read of
///     <see cref="ResourceId.Structure" />, so a job that writes the structure (structural changes)
///     is ordered against every other entity access.
/// </summary>
public sealed class AccessSet
{
    private readonly EntityComponentAccess[] _entityReads;
    private readonly EntityComponentAccess[] _entityWrites;
    private readonly ResourceId[] _reads;
    private readonly ResourceId[] _writes;

    internal AccessSet(ResourceId[] reads, ResourceId[] writes)
        : this(reads, writes, [], [])
    {
    }

    internal AccessSet(ResourceId[] reads, ResourceId[] writes, EntityComponentAccess[] entityReads, EntityComponentAccess[] entityWrites)
    {
        _reads = reads;
        _writes = writes;
        _entityReads = entityReads;
        _entityWrites = entityWrites;
        WritesStructure = Array.IndexOf(writes, ResourceId.Structure) >= 0;
    }

    /// <summary>Touches nothing. A job with this set may only use its own captured state.</summary>
    public static AccessSet None { get; } = new([], []);

    /// <summary>
    ///     Writes the structure, i.e. conflicts with every entity access (structural changes, legacy
    ///     main-thread systems). Named / unique resources are outside the entity storage and must
    ///     still be declared explicitly.
    /// </summary>
    public static AccessSet Exclusive { get; } = new([], [ResourceId.Structure]);

    /// <summary>Sorted resources read (shared). Does not include resources also written.</summary>
    public ReadOnlySpan<ResourceId> Reads => _reads;

    /// <summary>Sorted resources written (exclusive).</summary>
    public ReadOnlySpan<ResourceId> Writes => _writes;

    public ReadOnlySpan<EntityComponentAccess> EntityReads => _entityReads;

    public ReadOnlySpan<EntityComponentAccess> EntityWrites => _entityWrites;

    public bool WritesStructure { get; }

    public bool HasEntityAccess => _entityReads.Length > 0 || _entityWrites.Length > 0;

    public bool CanRead(ResourceId resource)
    {
        return WritesStructure || Contains(_writes, resource) || Contains(_reads, resource);
    }

    public bool CanWrite(ResourceId resource)
    {
        return WritesStructure || Contains(_writes, resource);
    }

    public bool CanReadEntity(Entity entity, ComponentTypeId type)
    {
        return CanRead(ResourceId.Component(type))
               || Contains(_entityWrites, new EntityComponentAccess(entity, type))
               || Contains(_entityReads, new EntityComponentAccess(entity, type));
    }

    public bool CanWriteEntity(Entity entity, ComponentTypeId type)
    {
        return CanWrite(ResourceId.Component(type)) || Contains(_entityWrites, new EntityComponentAccess(entity, type));
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (_reads.Length > 0)
        {
            parts.Add($"reads[{string.Join(", ", _reads)}]");
        }

        if (_writes.Length > 0)
        {
            parts.Add($"writes[{string.Join(", ", _writes)}]");
        }

        if (_entityReads.Length > 0)
        {
            parts.Add($"entity-reads[{string.Join(", ", _entityReads)}]");
        }

        if (_entityWrites.Length > 0)
        {
            parts.Add($"entity-writes[{string.Join(", ", _entityWrites)}]");
        }

        return parts.Count == 0 ? "none" : string.Join(" ", parts);
    }

    private static bool Contains(ResourceId[] sorted, ResourceId resource)
    {
        return Array.BinarySearch(sorted, resource, ResourceComparer.Instance) >= 0;
    }

    private static bool Contains(EntityComponentAccess[] sorted, EntityComponentAccess access)
    {
        return Array.BinarySearch(sorted, access, EntityAccessComparer.Instance) >= 0;
    }

    internal sealed class ResourceComparer : IComparer<ResourceId>
    {
        public static readonly ResourceComparer Instance = new();

        public int Compare(ResourceId x, ResourceId y)
        {
            return x.Value.CompareTo(y.Value);
        }
    }

    internal sealed class EntityAccessComparer : IComparer<EntityComponentAccess>
    {
        public static readonly EntityAccessComparer Instance = new();

        public int Compare(EntityComponentAccess x, EntityComponentAccess y)
        {
            var byEntity = x.Entity.Index.CompareTo(y.Entity.Index);
            return byEntity != 0 ? byEntity : x.Type.Value.CompareTo(y.Type.Value);
        }
    }
}

/// <summary>Fluent construction of an <see cref="AccessSet" />: <c>Access.Read&lt;A&gt;().Write&lt;B&gt;()</c>.</summary>
public struct AccessSetBuilder
{
    private List<EntityComponentAccess>? _entityReads;
    private List<EntityComponentAccess>? _entityWrites;
    private List<ResourceId>? _reads;
    private List<ResourceId>? _writes;

    public AccessSetBuilder Read<T>()
    {
        return Read(ResourceId.Component<T>());
    }

    public AccessSetBuilder Write<T>()
    {
        return Write(ResourceId.Component<T>());
    }

    public AccessSetBuilder Read(ResourceId resource)
    {
        (_reads ??= new List<ResourceId>()).Add(resource);
        return this;
    }

    public AccessSetBuilder Write(ResourceId resource)
    {
        (_writes ??= new List<ResourceId>()).Add(resource);
        return this;
    }

    /// <summary>Declares structural changes (entity creation/destruction, add/remove component). Conflicts with everything.</summary>
    public AccessSetBuilder WriteStructure()
    {
        return Write(ResourceId.Structure);
    }

    public AccessSetBuilder ReadEntity<T>(Entity entity)
    {
        (_entityReads ??= new List<EntityComponentAccess>()).Add(new EntityComponentAccess(entity, ComponentType<T>.Id));
        return this;
    }

    public AccessSetBuilder WriteEntity<T>(Entity entity)
    {
        (_entityWrites ??= new List<EntityComponentAccess>()).Add(new EntityComponentAccess(entity, ComponentType<T>.Id));
        return this;
    }

    private static readonly ResourceId[] StructureOnly = [ResourceId.Structure];

    /// <summary>Builds the set. Kept allocation-light: behaviour segments build one per await.</summary>
    public AccessSet Build()
    {
        var writes = Normalize(_writes, AccessSet.ResourceComparer.Instance);
        var reads = Normalize(_reads, AccessSet.ResourceComparer.Instance);
        var entityWrites = Normalize(_entityWrites, AccessSet.EntityAccessComparer.Instance);
        var entityReads = Normalize(_entityReads, AccessSet.EntityAccessComparer.Instance);

        var touchesComponents = entityWrites.Length > 0 || entityReads.Length > 0
                                || ContainsComponent(writes) || ContainsComponent(reads);
        if (touchesComponents
            && Array.IndexOf(writes, ResourceId.Structure) < 0
            && Array.IndexOf(reads, ResourceId.Structure) < 0)
        {
            // Component access implies reading the structure (the archetype/chunk layout being iterated).
            // Structure has the smallest id, so prepending keeps the array sorted.
            if (reads.Length == 0)
            {
                reads = StructureOnly;
            }
            else
            {
                var withStructure = new ResourceId[reads.Length + 1];
                withStructure[0] = ResourceId.Structure;
                reads.CopyTo(withStructure, 1);
                reads = withStructure;
            }
        }

        if (writes.Length > 0)
        {
            reads = Except(reads, writes, AccessSet.ResourceComparer.Instance);
        }

        if (entityWrites.Length > 0)
        {
            entityReads = Except(entityReads, entityWrites, AccessSet.EntityAccessComparer.Instance);
        }

        return new AccessSet(reads, writes, entityReads, entityWrites);
    }

    public static implicit operator AccessSet(AccessSetBuilder builder)
    {
        return builder.Build();
    }

    private static bool ContainsComponent(ResourceId[] resources)
    {
        foreach (var resource in resources)
        {
            if (resource.IsComponent)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Sorted, distinct copy without LINQ.</summary>
    private static T[] Normalize<T>(List<T>? items, IComparer<T> comparer)
    {
        if (items is null || items.Count == 0)
        {
            return [];
        }

        var array = items.ToArray();
        if (array.Length == 1)
        {
            return array;
        }

        Array.Sort(array, comparer);
        var write = 1;
        for (var read = 1; read < array.Length; read++)
        {
            if (comparer.Compare(array[read], array[write - 1]) != 0)
            {
                array[write++] = array[read];
            }
        }

        if (write != array.Length)
        {
            Array.Resize(ref array, write);
        }

        return array;
    }

    /// <summary>Elements of <paramref name="items" /> not present in the sorted <paramref name="excluded" />.</summary>
    private static T[] Except<T>(T[] items, T[] excluded, IComparer<T> comparer)
    {
        var kept = 0;
        foreach (var item in items)
        {
            if (Array.BinarySearch(excluded, item, comparer) < 0)
            {
                kept++;
            }
        }

        if (kept == items.Length)
        {
            return items;
        }

        var result = new T[kept];
        var write = 0;
        foreach (var item in items)
        {
            if (Array.BinarySearch(excluded, item, comparer) < 0)
            {
                result[write++] = item;
            }
        }

        return result;
    }
}

/// <summary>Entry points for building access sets.</summary>
public static class Access
{
    public static AccessSetBuilder Read<T>()
    {
        return new AccessSetBuilder().Read<T>();
    }

    public static AccessSetBuilder Write<T>()
    {
        return new AccessSetBuilder().Write<T>();
    }

    public static AccessSetBuilder WriteStructure()
    {
        return new AccessSetBuilder().WriteStructure();
    }

    public static AccessSetBuilder ReadEntity<T>(Entity entity)
    {
        return new AccessSetBuilder().ReadEntity<T>(entity);
    }

    public static AccessSetBuilder WriteEntity<T>(Entity entity)
    {
        return new AccessSetBuilder().WriteEntity<T>(entity);
    }
}
