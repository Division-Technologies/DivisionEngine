#if DIVISION_PROFILING
using System.Runtime.InteropServices;

namespace DivisionEngine;

/// <summary>
///     The Tracy C API (public/tracy/TracyC.h) as exported by the DivisionTracy shared library,
///     built from src/Native/packages/tracy. Nothing outside <see cref="Profiler" /> should call
///     these: they have no guard against being used before the profiler is started, which the
///     TRACY_MANUAL_LIFETIME build requires.
/// </summary>
/// <remarks>
///     <para>
///         The layout of <see cref="ZoneContext" /> depends on the defines the native library was
///         built with: TRACY_ON_DEMAND adds ConnectionId. There is no compiler or loader check for
///         this, so a mismatch between this file and packages/tracy/CMakeLists.txt does not fail to
///         build or to load — it silently corrupts the capture.
///     </para>
///     <para>
///         None of these carry <c>[SuppressGCTransition]</c>. The emit functions are short and
///         lock-free in the steady state, so suppressing the transition would save a few ns per
///         zone, but the first call on a thread lazily initialises thread-local state under
///         TRACY_DELAYED_INIT and can block, and a blocked thread in suppressed mode stalls the
///         GC. Worth measuring before trading that away.
///     </para>
/// </remarks>
internal static unsafe partial class TracyNative
{
    private const string Library = "DivisionTracy";

    /// <summary>Handle to a running zone. Opaque; pass it back to end/annotate the zone.</summary>
    /// <remarks>Mirrors <c>___tracy_c_zone_context</c> as built with TRACY_ON_DEMAND.</remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ZoneContext
    {
        public uint Id;
        public int Active;
        public ulong ConnectionId;
    }

    /// <summary>
    ///     A zone's call site. Tracy keeps the pointer and reads the strings later, so every
    ///     instance must live for the lifetime of the process (see <see cref="Profiler.DeclareZone" />).
    /// </summary>
    /// <remarks>Mirrors <c>___tracy_source_location_data</c>.</remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SourceLocation
    {
        public byte* Name;
        public byte* Function;
        public byte* File;
        public uint Line;
        public uint Color;
    }

    // ----- lifetime -----
    // TRACY_MANUAL_LIFETIME: the profiler thread is not started by a static initializer, so the
    // engine decides when it comes up. Calling any emit function before startup is undefined.

    [LibraryImport(Library, EntryPoint = "___tracy_startup_profiler")]
    internal static partial void StartupProfiler();

    [LibraryImport(Library, EntryPoint = "___tracy_shutdown_profiler")]
    internal static partial void ShutdownProfiler();

    [LibraryImport(Library, EntryPoint = "___tracy_profiler_started")]
    internal static partial int ProfilerStarted();

    /// <summary>Non-zero once the Tracy UI has attached. Under TRACY_ON_DEMAND nothing is recorded until then.</summary>
    [LibraryImport(Library, EntryPoint = "___tracy_connected")]
    internal static partial int Connected();

    // ----- threads -----

    [LibraryImport(Library, EntryPoint = "___tracy_set_thread_name")]
    internal static partial void SetThreadName(byte* name);

    // ----- zones -----

    [LibraryImport(Library, EntryPoint = "___tracy_emit_zone_begin")]
    internal static partial ZoneContext ZoneBegin(SourceLocation* sourceLocation, int active);

    [LibraryImport(Library, EntryPoint = "___tracy_emit_zone_end")]
    internal static partial void ZoneEnd(ZoneContext context);

    /// <summary>Overrides the zone's displayed name. Tracy copies the text, so the pointer need not outlive the call.</summary>
    [LibraryImport(Library, EntryPoint = "___tracy_emit_zone_name")]
    internal static partial void ZoneName(ZoneContext context, byte* text, nuint size);

    /// <summary>Attaches free-form text to the zone. Copied, like <see cref="ZoneName" />.</summary>
    [LibraryImport(Library, EntryPoint = "___tracy_emit_zone_text")]
    internal static partial void ZoneText(ZoneContext context, byte* text, nuint size);

    [LibraryImport(Library, EntryPoint = "___tracy_emit_zone_value")]
    internal static partial void ZoneValue(ZoneContext context, ulong value);

    [LibraryImport(Library, EntryPoint = "___tracy_emit_zone_color")]
    internal static partial void ZoneColor(ZoneContext context, uint color);

    // ----- frames -----
    // Frame names are identities: Tracy keeps the pointer, so they must be persistent.

    [LibraryImport(Library, EntryPoint = "___tracy_emit_frame_mark")]
    internal static partial void FrameMark(byte* name);

    [LibraryImport(Library, EntryPoint = "___tracy_emit_frame_mark_start")]
    internal static partial void FrameMarkStart(byte* name);

    [LibraryImport(Library, EntryPoint = "___tracy_emit_frame_mark_end")]
    internal static partial void FrameMarkEnd(byte* name);

    // ----- locks -----
    // A lockable is announced once and then reports every acquisition and release, so the profiler
    // can draw who held it and who was waiting. The context is opaque and lives until terminated.

    [LibraryImport(Library, EntryPoint = "___tracy_announce_lockable_ctx")]
    internal static partial nint AnnounceLockable(SourceLocation* sourceLocation);

    [LibraryImport(Library, EntryPoint = "___tracy_terminate_lockable_ctx")]
    internal static partial void TerminateLockable(nint context);

    /// <summary>
    ///     Called before blocking on the lock. Non-zero means the acquisition must be reported with
    ///     <see cref="AfterLock" />; zero means nothing is being recorded right now (TRACY_ON_DEMAND)
    ///     and the matching call must be skipped.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "___tracy_before_lock_lockable_ctx")]
    internal static partial int BeforeLock(nint context);

    [LibraryImport(Library, EntryPoint = "___tracy_after_lock_lockable_ctx")]
    internal static partial void AfterLock(nint context);

    /// <summary>Called after every release, whether or not the acquisition was reported: it also keeps the held count.</summary>
    [LibraryImport(Library, EntryPoint = "___tracy_after_unlock_lockable_ctx")]
    internal static partial void AfterUnlock(nint context);

    [LibraryImport(Library, EntryPoint = "___tracy_after_try_lock_lockable_ctx")]
    internal static partial void AfterTryLock(nint context, int acquired);

    // ----- plots and messages -----

    [LibraryImport(Library, EntryPoint = "___tracy_emit_plot")]
    internal static partial void Plot(byte* name, double value);

    /// <summary>Sets how a plot is drawn. The name must be the same pointer the plot is emitted with.</summary>
    [LibraryImport(Library, EntryPoint = "___tracy_emit_plot_config")]
    internal static partial void PlotConfig(byte* name, int type, int step, int fill, uint color);

    /// <summary>
    ///     A message on the timeline. Named "logString" since 0.14, which replaced the older
    ///     <c>___tracy_emit_message</c> with a severity-carrying form.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "___tracy_emit_logString")]
    internal static partial void LogString(sbyte severity, int color, int callstackDepth, nuint size, byte* text);

    /// <summary>Mirrors <c>TracyMessageSeverity</c>; the engine only emits Info.</summary>
    internal const sbyte SeverityInfo = 2;
}
#endif