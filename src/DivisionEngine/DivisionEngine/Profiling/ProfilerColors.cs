namespace DivisionEngine;

/// <summary>
///     Zone colors used by the engine's own instrumentation, as 0xRRGGBB.
/// </summary>
/// <remarks>
///     Kept in one place so that a capture reads consistently: the eye picks out the idle bands
///     before it reads any label, which is the whole point of colouring waits differently from
///     work. Zones left at 0 get the profiler's own per-name colour.
/// </remarks>
public static class ProfilerColors
{
    /// <summary>A thread parked on a semaphore with nothing to run. Muted, so it recedes.</summary>
    public const uint Idle = 0x4A4A55;

    /// <summary>A frame phase opening and closing on the main thread.</summary>
    public const uint Phase = 0x2F6F9F;

    /// <summary>A behavior segment.</summary>
    public const uint Behavior = 0x9F6F2F;

    /// <summary>Committing buffered writes or applying structural changes: the synchronisation points.</summary>
    public const uint Commit = 0x9F2F5F;
}
