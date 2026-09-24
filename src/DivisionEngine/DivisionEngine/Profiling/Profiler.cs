using System.Runtime.CompilerServices;
#if DIVISION_PROFILING
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
#endif

namespace DivisionEngine;

/// <summary>
///     Frame and task instrumentation, forwarded to the Tracy profiler.
/// </summary>
/// <remarks>
///     <para>
///         The whole surface compiles to nothing unless the <c>DIVISION_PROFILING</c> constant is
///         defined (<c>dotnet build -p:DivisionProfiling=true</c>), so call sites can be written
///         unconditionally and benchmarks stay comparable with an uninstrumented build. When it is
///         defined, the process additionally needs the DivisionTracy shared library next to the
///         managed output; see Notes/Core/Profiling.md.
///     </para>
///     <para>
///         Every entry point is safe to call before <see cref="Startup" /> or when the native
///         library is missing: it does nothing. That matters because the native build is a
///         separate, optional step, and a missing profiler must never take the engine down.
///     </para>
/// </remarks>
public static class Profiler
{
    /// <summary>
    ///     Upper bound on distinct run-time zone names given their own call site by
    ///     <see cref="ZoneNamed" />. A guard, not a tuning knob: job names come from system and
    ///     behavior type names and are a small fixed set, but <c>JobGraph.Start</c> lets a caller
    ///     pass any string, and a name built per entity would otherwise grow this without end.
    /// </summary>
    private const int MaxInternedZoneSources = 1024;

#if DIVISION_PROFILING
    private static readonly Lock Gate = new();
    private static readonly ConcurrentDictionary<string, nint> Names = new();
    private static readonly ConcurrentDictionary<(nint Site, string Name), nint> ZoneSources = new();
    private static bool _started;
    private static bool _startedHere;
#endif

    /// <summary>Whether instrumentation is compiled in at all.</summary>
    public static bool IsCompiledIn =>
#if DIVISION_PROFILING
        true;
#else
        false;
#endif

    /// <summary>Whether <see cref="Startup" /> succeeded and zones are being emitted.</summary>
    public static bool IsRunning
    {
        get
        {
#if DIVISION_PROFILING
            return Volatile.Read(ref _started);
#else
            return false;
#endif
        }
    }

    /// <summary>
    ///     Whether the Tracy UI is attached. The native library is built with TRACY_ON_DEMAND, so
    ///     until something connects, the emit calls return almost immediately and record nothing.
    /// </summary>
    public static bool IsConnected
    {
        get
        {
#if DIVISION_PROFILING
            return IsRunning && TracyNative.Connected() != 0;
#else
            return false;
#endif
        }
    }

    /// <summary>Why <see cref="Startup" /> did not take effect, or null if it did (or if profiling is compiled out).</summary>
    public static string? UnavailableReason { get; private set; }

    /// <summary>
    ///     Whether <see cref="ProfiledLock" /> reports its acquisitions. On by default in a
    ///     profiling build; set DIVISION_PROFILING_LOCKS=0 to turn it off.
    /// </summary>
    /// <remarks>
    ///     Unlike a zone, a lock costs on every acquisition rather than once per unit of work, and
    ///     the issue lock is taken thousands of times a frame. That is precisely what makes it worth
    ///     looking at, and also what makes it worth being able to switch off: if the instrumentation
    ///     is suspected of creating the contention it measures, the way to find out is to run both.
    /// </remarks>
    public static bool AreLocksVisible { get; private set; }

    /// <summary>
    ///     Starts the profiler. Idempotent. Returns false if profiling is compiled out or the
    ///     native library could not be loaded, leaving <see cref="UnavailableReason" /> set.
    /// </summary>
    /// <remarks>
    ///     The native library is built with TRACY_MANUAL_LIFETIME, which means no static
    ///     initializer brings the profiler up and no zone may be emitted before this call.
    /// </remarks>
    public static bool Startup()
    {
#if DIVISION_PROFILING
        lock (Gate)
        {
            if (_started)
            {
                return true;
            }

            try
            {
                // Someone else may already have brought the client up - the CLR profiler does, when
                // it is asked to record a build that has this constant off. Starting it twice
                // constructs a second client over the first.
                if (TracyNative.ProfilerStarted() == 0)
                {
                    TracyNative.StartupProfiler();
                    _startedHere = true;
                }
            }
            catch (DllNotFoundException ex)
            {
                UnavailableReason =
                    $"The DivisionTracy native library was not found ({ex.Message}). " +
                    "Build it with: cmake --build --preset <preset> --target tracy_client";
                return false;
            }
            catch (EntryPointNotFoundException ex)
            {
                UnavailableReason =
                    $"The DivisionTracy native library is missing an entry point ({ex.Message}). " +
                    "It was most likely built without TRACY_ENABLE or TRACY_MANUAL_LIFETIME.";
                return false;
            }

            UnavailableReason = null;
            AreLocksVisible = Environment.GetEnvironmentVariable("DIVISION_PROFILING_LOCKS") != "0";
            Volatile.Write(ref _started, true);
            return true;
        }
#else
        UnavailableReason = "Profiling is compiled out; build with -p:DivisionProfiling=true.";
        return false;
#endif
    }

    /// <summary>Stops the profiler and flushes pending data. Idempotent.</summary>
    public static void Shutdown()
    {
#if DIVISION_PROFILING
        lock (Gate)
        {
            if (!_started)
            {
                return;
            }

            Volatile.Write(ref _started, false);
            AreLocksVisible = false;
            if (_startedHere)
            {
                // Only what this process started here is stopped here: shutting down a client the
                // CLR profiler brought up would close a capture the engine does not own.
                _startedHere = false;
                TracyNative.ShutdownProfiler();
            }
        }
#endif
    }

    /// <summary>
    ///     Declares a zone call site. Call once and keep the result in a <c>static readonly</c>
    ///     field: the strings behind it are allocated for the lifetime of the process, because
    ///     the profiler resolves the pointers long after the zone has ended.
    /// </summary>
    /// <param name="name">Name shown in the UI. Use <see cref="ProfilerZone.Name" /> for a per-instance name.</param>
    /// <param name="color">0xRRGGBB, or 0 for the profiler's default.</param>
    public static ProfilerZoneSource DeclareZone(
        string name,
        uint color = 0,
        [CallerMemberName] string function = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
#if DIVISION_PROFILING
        unsafe
        {
            var location = (TracyNative.SourceLocation*)NativeMemory.Alloc((nuint)sizeof(TracyNative.SourceLocation));
            location->Name = AllocUtf8(name);
            location->Function = AllocUtf8(function);
            location->File = AllocUtf8(file);
            location->Line = (uint)line;
            location->Color = color;
            return new ProfilerZoneSource((nint)location);
        }
#else
        _ = name;
        _ = color;
        _ = function;
        _ = file;
        _ = line;
        return default;
#endif
    }

    /// <summary>
    ///     Declares a frame or plot name. Like <see cref="DeclareZone" />, the result is permanent
    ///     and belongs in a <c>static readonly</c> field: the profiler uses the pointer itself as
    ///     the identity of the series.
    /// </summary>
    public static ProfilerName DeclareName(string name)
    {
#if DIVISION_PROFILING
        // Interned, not merely cached: the profiler identifies a frame or plot series by the
        // pointer, so handing out two pointers for one name would split it into two series.
        return new ProfilerName(Names.GetOrAdd(name, static key =>
        {
            unsafe
            {
                return (nint)AllocUtf8(key);
            }
        }));
#else
        _ = name;
        return default;
#endif
    }

    /// <summary>
    ///     Declares a lock's call site. Permanent, like <see cref="DeclareZone" />; build one into a
    ///     field and hand it to <see cref="ProfiledLock" />.
    /// </summary>
    public static ProfilerLockSource DeclareLock(
        string name,
        [CallerMemberName] string function = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
#if DIVISION_PROFILING
        unsafe
        {
            var location = (TracyNative.SourceLocation*)NativeMemory.Alloc((nuint)sizeof(TracyNative.SourceLocation));
            location->Name = AllocUtf8(name);
            location->Function = AllocUtf8(function);
            location->File = AllocUtf8(file);
            location->Line = (uint)line;
            location->Color = 0;
            return new ProfilerLockSource((nint)location);
        }
#else
        _ = name;
        _ = function;
        _ = file;
        _ = line;
        return default;
#endif
    }

    /// <summary>
    ///     Announces a lock to the profiler, returning the context its events are reported against,
    ///     or 0 if nothing is being recorded. Announcements are deferred by the client, so a lock
    ///     announced before the UI connects still appears once it does.
    /// </summary>
    internal static nint AnnounceLock(in ProfilerLockSource source)
    {
#if DIVISION_PROFILING
        if (!IsRunning || !AreLocksVisible)
        {
            return 0;
        }

        unsafe
        {
            return TracyNative.AnnounceLockable((TracyNative.SourceLocation*)source.Handle);
        }
#else
        _ = source;
        return 0;
#endif
    }

    /// <summary>Sets how a plot is drawn. Call once per series, with the name it is emitted with.</summary>
    public static void ConfigurePlot(in ProfilerName name, PlotFormat format, bool step = false, bool fill = true,
        uint color = 0)
    {
#if DIVISION_PROFILING
        if (IsRunning)
        {
            unsafe
            {
                TracyNative.PlotConfig((byte*)name.Handle, (int)format, step ? 1 : 0, fill ? 1 : 0, color);
            }
        }
#else
        _ = name;
        _ = format;
        _ = step;
        _ = fill;
        _ = color;
#endif
    }

    /// <summary>Opens a zone that ends when the returned value is disposed.</summary>
    public static ProfilerZone Zone(in ProfilerZoneSource source)
    {
#if DIVISION_PROFILING
        if (!IsRunning)
        {
            return default;
        }

        unsafe
        {
            return new ProfilerZone(TracyNative.ZoneBegin((TracyNative.SourceLocation*)source.Handle, 1));
        }
#else
        _ = source;
        return default;
#endif
    }

    /// <summary>
    ///     Opens a zone for a name only known at run time (a job's or behavior's name), reusing
    ///     <paramref name="shared" />'s call site but giving each distinct name its own source
    ///     location.
    /// </summary>
    /// <remarks>
    ///     Naming the zone after the fact (<see cref="ProfilerZone.Name" />) displays correctly but
    ///     leaves every job sharing one call site, and the profiler aggregates its statistics per
    ///     call site - so "which system costs the most" collapses into a single row labelled after
    ///     the shared site. Interning a source location per name keeps those statistics, and costs
    ///     a dictionary lookup instead of a UTF-8 encode per zone. Past
    ///     <see cref="MaxInternedZoneSources" /> distinct names it falls back to the shared site
    ///     with the name attached, which still reads correctly on the timeline.
    /// </remarks>
    public static ProfilerZone ZoneNamed(string name, in ProfilerZoneSource shared)
    {
#if DIVISION_PROFILING
        if (!IsRunning)
        {
            return default;
        }

        // Keyed by call site as well as name: a job and a phase can carry the same name
        // (TransformPropagation is both), and keying on the name alone would collapse the two
        // into whichever source location happened to be interned first.
        var key = (shared.Handle, name);
        if (!ZoneSources.TryGetValue(key, out var handle))
        {
            if (ZoneSources.Count >= MaxInternedZoneSources)
            {
                var fallback = Zone(shared);
                fallback.Name(name);
                return fallback;
            }

            // The factory form, so a race adds one source location rather than allocating one
            // per racing thread and dropping all but the winner.
            var origin = shared;
            handle = ZoneSources.GetOrAdd(key, k => Derive(k.Name, origin));
        }

        unsafe
        {
            return new ProfilerZone(TracyNative.ZoneBegin((TracyNative.SourceLocation*)handle, 1));
        }
#else
        _ = name;
        _ = shared;
        return default;
#endif
    }

    /// <summary>Names the calling thread in the capture. Call once per thread, as early as possible.</summary>
    public static void SetThreadName(string name)
    {
#if DIVISION_PROFILING
        if (!IsRunning)
        {
            return;
        }

        unsafe
        {
            // Tracy keeps this pointer for the lifetime of the thread's entry, so it cannot be a
            // temporary buffer. Interned, so calling this twice for one name costs nothing.
            TracyNative.SetThreadName((byte*)DeclareName(name).Handle);
        }
#else
        _ = name;
#endif
    }

    /// <summary>Marks the end of a frame on the unnamed, primary frame series.</summary>
    public static void FrameMark()
    {
#if DIVISION_PROFILING
        if (IsRunning)
        {
            unsafe
            {
                TracyNative.FrameMark(null);
            }
        }
#endif
    }

    /// <summary>Opens a discontinuous frame, closed by <see cref="FrameMarkEnd" /> with the same name.</summary>
    public static void FrameMarkStart(in ProfilerName name)
    {
#if DIVISION_PROFILING
        if (IsRunning)
        {
            unsafe
            {
                TracyNative.FrameMarkStart((byte*)name.Handle);
            }
        }
#else
        _ = name;
#endif
    }

    /// <summary>Closes a frame opened by <see cref="FrameMarkStart" />.</summary>
    public static void FrameMarkEnd(in ProfilerName name)
    {
#if DIVISION_PROFILING
        if (IsRunning)
        {
            unsafe
            {
                TracyNative.FrameMarkEnd((byte*)name.Handle);
            }
        }
#else
        _ = name;
#endif
    }

    /// <summary>Records a point on a named numeric series, drawn under the timeline.</summary>
    public static void Plot(in ProfilerName name, double value)
    {
#if DIVISION_PROFILING
        if (IsRunning)
        {
            unsafe
            {
                TracyNative.Plot((byte*)name.Handle, value);
            }
        }
#else
        _ = name;
        _ = value;
#endif
    }

    /// <summary>Records a one-off message on the timeline.</summary>
    public static void Message(ReadOnlySpan<char> text)
    {
#if DIVISION_PROFILING
        if (!IsRunning)
        {
            return;
        }

        unsafe
        {
            Span<byte> buffer = stackalloc byte[Encoding.UTF8.GetMaxByteCount(Math.Min(text.Length, 256))];
            var written = Encoding.UTF8.GetBytes(text[..Math.Min(text.Length, 256)], buffer);
            fixed (byte* pointer = buffer)
            {
                TracyNative.LogString(TracyNative.SeverityInfo, 0, 0, (nuint)written, pointer);
            }
        }
#else
        _ = text;
#endif
    }

#if DIVISION_PROFILING
    /// <summary>
    ///     A source location carrying <paramref name="name" /> but the file, function and line of
    ///     <paramref name="shared" />, so that the zone still points at the code that opened it.
    /// </summary>
    private static unsafe nint Derive(string name, in ProfilerZoneSource shared)
    {
        var origin = (TracyNative.SourceLocation*)shared.Handle;
        var location = (TracyNative.SourceLocation*)NativeMemory.Alloc((nuint)sizeof(TracyNative.SourceLocation));
        location->Name = AllocUtf8(name);
        location->Function = origin->Function;
        location->File = origin->File;
        location->Line = origin->Line;
        location->Color = origin->Color;
        return (nint)location;
    }

    /// <summary>
    ///     Copies a string into unmanaged memory as NUL-terminated UTF-8. Never freed: every
    ///     caller here is declaring something the profiler dereferences at an arbitrary later
    ///     point, and the number of declarations is bounded by the number of call sites.
    /// </summary>
    private static unsafe byte* AllocUtf8(string value)
    {
        var count = Encoding.UTF8.GetByteCount(value);
        var buffer = (byte*)NativeMemory.Alloc((nuint)count + 1);
        Encoding.UTF8.GetBytes(value, new Span<byte>(buffer, count));
        buffer[count] = 0;
        return buffer;
    }
#endif
}