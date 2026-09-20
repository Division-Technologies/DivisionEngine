namespace DivisionEngine;

/// <summary>What a job system sees when it is asked to schedule its work for the current step.</summary>
public readonly struct JobSchedulingContext
{
    internal JobSchedulingContext(ref readonly FrameContext frame)
    {
        Time = frame.ActiveTime;
        Realtime = frame.Realtime;
        Engine = frame.Engine;
        World = frame.World;
        Graph = frame.Graph;
    }

    public Time Time { get; }
    public Realtime Realtime { get; }
    public Engine Engine { get; }
    public World World { get; }
    public JobGraph Graph { get; }
}

/// <summary>
///     A system that schedules jobs instead of running immediately. Called on the main thread each
///     step; its jobs run on the pool ordered by their declared access. Plain <see cref="ISystem" />s
///     in the same group act as barriers: they run on the main thread after everything scheduled
///     before them has completed.
/// </summary>
public interface IJobSystem : ISystem
{
    void Schedule(in JobSchedulingContext context);

    void ISystem.Execute(ref FrameContext ctx)
    {
        Schedule(new JobSchedulingContext(in ctx));
    }
}
