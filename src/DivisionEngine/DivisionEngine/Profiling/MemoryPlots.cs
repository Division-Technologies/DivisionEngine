namespace DivisionEngine;

/// <summary>
///     Per-frame plots of the process's and the runtime's memory, drawn under the timeline.
/// </summary>
/// <remarks>
///     <para>
///         These answer a different question from the zones: a zone says where a frame went, a plot
///         says what was accumulating while it went there. A leak, a heap that grows until it
///         collects, and an allocation rate that jumped on a particular frame are all invisible in
///         zones and obvious in a line.
///     </para>
///     <para>
///         The whole class is inert unless profiling is compiled in. Sampling costs roughly 950 ns
///         per frame when it runs, nearly all of it in <see cref="Environment.WorkingSet" /> (587 ns)
///         and <see cref="GC.GetTotalMemory" /> (289 ns); the two counters that matter most per
///         frame, allocation and committed bytes, are 8 ns and 59 ns.
///     </para>
/// </remarks>
internal static class MemoryPlots
{
#if DIVISION_PROFILING
    private static readonly ProfilerName ManagedHeap = Profiler.DeclareName("Managed heap");
    private static readonly ProfilerName ManagedCommitted = Profiler.DeclareName("Managed committed");
    private static readonly ProfilerName ProcessMemory = Profiler.DeclareName("Process memory");
    private static readonly ProfilerName AllocatedPerFrame = Profiler.DeclareName("Allocated per frame");

    private static long _lastAllocated;
    private static bool _configured;
#endif

    /// <summary>Records one sample of every series. Call once per frame.</summary>
    internal static void Sample()
    {
#if DIVISION_PROFILING
        if (!Profiler.IsRunning)
        {
            return;
        }

        if (!_configured)
        {
            // Deferred to here rather than done at startup: the configuration is sent to the UI,
            // so it has to happen while the profiler is running, and the first frame is the first
            // moment that is guaranteed.
            Profiler.ConfigurePlot(ManagedHeap, PlotFormat.Memory);
            Profiler.ConfigurePlot(ManagedCommitted, PlotFormat.Memory);
            Profiler.ConfigurePlot(ProcessMemory, PlotFormat.Memory);
            Profiler.ConfigurePlot(AllocatedPerFrame, PlotFormat.Memory, step: true);
            _configured = true;
        }

        // Bytes allocated since the process started, which only ever grows; the interesting figure
        // is how much this frame added, and a frame that suddenly allocates ten times its usual
        // share is the kind of thing a total hides completely.
        var allocated = GC.GetTotalAllocatedBytes(false);
        var delta = allocated - _lastAllocated;
        _lastAllocated = allocated;

        Profiler.Plot(AllocatedPerFrame, delta);
        Profiler.Plot(ManagedHeap, GC.GetTotalMemory(false));
        Profiler.Plot(ManagedCommitted, GC.GetGCMemoryInfo().TotalCommittedBytes);

        // Resident set, so it includes everything the managed heap does not: the component chunks,
        // the native libraries, the runtime itself.
        Profiler.Plot(ProcessMemory, Environment.WorkingSet);
#endif
    }
}