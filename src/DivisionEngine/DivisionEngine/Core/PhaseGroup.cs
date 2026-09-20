namespace DivisionEngine;

/// <summary>
///     One phase of the frame as a system group. Executing it opens the phase on the graph,
///     schedules the child systems, resumes the behaviors parked on the phase (if the phase
///     dispatches behaviors), and closes the phase (waits, commits buffered behavior writes).
///     <see cref="FrozenTypes" /> are component types no job or behavior may write during the
///     phase: system-lane writes are rejected when scheduled, behavior writes are carried over to
///     the next phase that allows them.
/// </summary>
public sealed class PhaseGroup : SystemGroup
{
    public PhaseGroup(PhaseId phase, bool dispatchesBehaviors, bool mainThreadOnly = false)
    {
        Phase = phase;
        DispatchesBehaviors = dispatchesBehaviors;
        MainThreadOnly = mainThreadOnly;
    }

    public PhaseId Phase { get; }

    /// <summary>Whether behaviors parked on this phase (and pending intake) are issued here.</summary>
    public bool DispatchesBehaviors { get; }

    /// <summary>Documents that the phase's work is thread-affine (OS, present). Plain systems already run on the main thread.</summary>
    public bool MainThreadOnly { get; }

    public HashSet<ComponentTypeId> FrozenTypes { get; } = new();

    public override void Execute(ref FrameContext ctx)
    {
        var graph = ctx.Graph;
        graph.Time = ctx.ActiveTime;
        graph.Realtime = ctx.Realtime;
        graph.BeginPhase(Phase, DispatchesBehaviors, FrozenTypes);
        try
        {
            base.Execute(ref ctx);
            if (DispatchesBehaviors)
            {
                graph.ResumePhase(Phase);
            }
        }
        finally
        {
            graph.EndPhase();
        }
    }

    public override string ToString()
    {
        return $"PhaseGroup({Phase})";
    }
}
