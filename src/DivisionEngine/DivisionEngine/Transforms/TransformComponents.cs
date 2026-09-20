using System.Numerics;
using System.Runtime.CompilerServices;

namespace DivisionEngine;

/// <summary>
///     An entity's transform relative to its <see cref="Parent" />, or to the world when it has
///     none. Note that <c>default</c> is not the identity (it has a zero scale and a zero
///     quaternion); use <see cref="Identity" /> or the <c>From*</c> helpers.
/// </summary>
public struct LocalTransform
{
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Scale;

    public static LocalTransform Identity => new()
    {
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        Scale = Vector3.One
    };

    public static LocalTransform FromPosition(Vector3 position)
    {
        return Identity with { Position = position };
    }

    public static LocalTransform FromRotation(Quaternion rotation)
    {
        return Identity with { Rotation = rotation };
    }

    public static LocalTransform FromPositionRotation(Vector3 position, Quaternion rotation)
    {
        return Identity with { Position = position, Rotation = rotation };
    }

    /// <summary>The scale-rotate-translate matrix, in the row-vector convention of <see cref="Matrix4x4" />.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Matrix4x4 ToMatrix()
    {
        return Matrix4x4.CreateScale(Scale)
               * Matrix4x4.CreateFromQuaternion(Rotation)
               * Matrix4x4.CreateTranslation(Position);
    }
}

/// <summary>
///     The transform of an entity in world space, produced by <see cref="TransformPropagationSystem" />
///     during <see cref="PhaseId.TransformPropagation" />. Frozen from <see cref="PhaseId.LateUpdate" />
///     through <see cref="PhaseId.Render" />, so readers downstream see a settled value.
/// </summary>
public struct WorldTransform
{
    public Matrix4x4 Value;

    public readonly Vector3 Position => Value.Translation;

    public static WorldTransform Identity => new() { Value = Matrix4x4.Identity };
}

/// <summary>The entity this one hangs under. Present exactly when the entity is a child.</summary>
public struct Parent
{
    public Entity Value;
}

/// <summary>
///     The ends of a parent's intrusive list of children. Present exactly while the entity has at
///     least one child; children themselves are chained through <see cref="Sibling" />, in the order
///     they were attached.
/// </summary>
public struct Child
{
    public Entity First;
    public Entity Last;
}

/// <summary>A child's neighbours in its parent's list. Present exactly when <see cref="Parent" /> is.</summary>
public struct Sibling
{
    public Entity Next;
    public Entity Previous;
}

/// <summary>
///     Teaches command-buffer playback how to rewrite the entity handles the hierarchy components
///     hold, so a subtree can be built from placeholders in a single buffer. The generator emits the
///     equivalent registration for user components (milestone M8).
/// </summary>
internal static class HierarchyComponentRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        ComponentTypeRegistry.RegisterEntityFields<Parent>(static (ref Parent value, DeferredEntityMap map) =>
        {
            value.Value = map.Resolve(value.Value);
        });

        ComponentTypeRegistry.RegisterEntityFields<Child>(static (ref Child value, DeferredEntityMap map) =>
        {
            value.First = map.Resolve(value.First);
            value.Last = map.Resolve(value.Last);
        });

        ComponentTypeRegistry.RegisterEntityFields<Sibling>(static (ref Sibling value, DeferredEntityMap map) =>
        {
            value.Next = map.Resolve(value.Next);
            value.Previous = map.Resolve(value.Previous);
        });
    }
}
