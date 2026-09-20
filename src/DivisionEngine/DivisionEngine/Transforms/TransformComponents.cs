using System.Numerics;
using System.Runtime.CompilerServices;

namespace DivisionEngine;

/// <summary>
///     An entity's transform relative to its <see cref="Parent" />, or to the world when it has
///     none. Note that <c>default</c> is not the identity (it has a zero scale and a zero
///     quaternion); use <see cref="Identity" /> or the <c>From*</c> helpers.
/// </summary>
[Component]
[AutoSerialization]
[TypeId("6f1a2c48-5f6b-4a0e-9a31-0f3d5c7e1b01")]
public partial struct LocalTransform
{
    [Serialize] public Vector3 Position;
    [Serialize] public Quaternion Rotation;
    [Serialize] public Vector3 Scale;

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
[Component]
[AutoSerialization]
[TypeId("6f1a2c48-5f6b-4a0e-9a31-0f3d5c7e1b02")]
public partial struct WorldTransform
{
    [Serialize] public Matrix4x4 Value;

    public readonly Vector3 Position => Value.Translation;

    public static WorldTransform Identity => new() { Value = Matrix4x4.Identity };
}

/// <summary>The entity this one hangs under. Present exactly when the entity is a child.</summary>
[Component]
[AutoSerialization]
[TypeId("6f1a2c48-5f6b-4a0e-9a31-0f3d5c7e1b03")]
public partial struct Parent
{
    [Serialize] public Entity Value;
}

/// <summary>
///     The ends of a parent's intrusive list of children. Present exactly while the entity has at
///     least one child; children themselves are chained through <see cref="Sibling" />, in the order
///     they were attached.
/// </summary>
[Component]
[AutoSerialization]
[TypeId("6f1a2c48-5f6b-4a0e-9a31-0f3d5c7e1b04")]
public partial struct Child
{
    [Serialize] public Entity First;
    [Serialize] public Entity Last;
}

/// <summary>A child's neighbours in its parent's list. Present exactly when <see cref="Parent" /> is.</summary>
[Component]
[AutoSerialization]
[TypeId("6f1a2c48-5f6b-4a0e-9a31-0f3d5c7e1b05")]
public partial struct Sibling
{
    [Serialize] public Entity Next;
    [Serialize] public Entity Previous;
}
