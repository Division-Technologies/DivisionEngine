using System.Collections.Concurrent;

namespace DivisionEngine;

/// <summary>
///     Identifies something a job can declare access to at type level: the world structure
///     (archetype membership, entity existence), each component type, and named resources for
///     things outside the world (a command buffer, an event queue). Entity-level accesses hang
///     under the type-level ones as <see cref="EntityComponentAccess" /> pairs, so they are not
///     ResourceIds themselves (Notes/Core/JobSystem.md).
///     Encoding: 0 = structure, positive = component type id + 1, negative = named resource.
/// </summary>
public readonly record struct ResourceId(int Value)
{
    private static readonly ConcurrentDictionary<string, int> NamedIds = new();
    private static readonly ConcurrentDictionary<int, string> NamedNames = new();
    private static int _negativeIds;

    /// <summary>Entity existence and archetype membership. Writing it conflicts with every other access.</summary>
    public static readonly ResourceId Structure = new(0);

    public static ResourceId Component(ComponentTypeId type)
    {
        return new ResourceId(type.Value + 1);
    }

    public static ResourceId Component<T>()
    {
        return Component(ComponentType<T>.Id);
    }

    /// <summary>A resource outside the entity storage, identified by name (process-wide).</summary>
    public static ResourceId Named(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var value = NamedIds.GetOrAdd(name, static n =>
        {
            var id = -Interlocked.Increment(ref _negativeIds);
            NamedNames[id] = n;
            return id;
        });
        return new ResourceId(value);
    }

    /// <summary>
    ///     A fresh resource id for one object instance (a command buffer, a queue). Ids are never
    ///     reused, so keep such objects long-lived rather than allocating them per frame.
    /// </summary>
    public static ResourceId Unique(string debugName)
    {
        var id = -Interlocked.Increment(ref _negativeIds);
        NamedNames[id] = $"{debugName}#{-id}";
        return new ResourceId(id);
    }

    public bool IsStructure => Value == 0;

    public bool IsComponent => Value > 0;

    public bool IsNamed => Value < 0;

    public ComponentTypeId ComponentType => new(Value - 1);

    public override string ToString()
    {
        if (IsStructure)
        {
            return "Structure";
        }

        if (IsNamed)
        {
            return NamedNames.TryGetValue(Value, out var name) ? name : $"Named({Value})";
        }

        return ComponentTypeRegistry.GetInfo(ComponentType).Type.Name;
    }
}
