namespace DivisionEngine;

/// <summary>
///     Handle to an entity: a slot index plus a version that changes every time the slot is
///     reused, so stale handles to destroyed entities can be detected.
///     Handles with a negative <see cref="Index" /> are placeholders created by an
///     <see cref="EntityCommandBuffer" /> and are resolved to real entities at playback.
/// </summary>
public readonly record struct Entity(int Index, int Version)
{
    public static readonly Entity Null = default;

    public bool IsNull => this == default;

    /// <summary>True for placeholders recorded by an <see cref="EntityCommandBuffer" />.</summary>
    public bool IsDeferred => Index < 0;

    public override string ToString()
    {
        return IsNull ? "Entity.Null" : IsDeferred ? $"Entity(deferred #{-1 - Index})" : $"Entity({Index}:{Version})";
    }
}
