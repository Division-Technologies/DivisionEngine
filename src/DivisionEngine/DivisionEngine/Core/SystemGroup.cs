namespace DivisionEngine;

/// <summary>
///     An ordered list of systems. <see cref="IJobSystem" /> children schedule jobs onto the frame's
///     graph; any other <see cref="ISystem" /> is a barrier: everything scheduled before it completes,
///     then it runs on the main thread. Phase groups and the frame end wait for the scheduled jobs.
/// </summary>
public class SystemGroup : ISystem
{
    protected readonly List<ISystem> Systems = new();

    public virtual void Execute(ref FrameContext ctx)
    {
        foreach (var system in Systems)
        {
            if (system is IJobSystem)
            {
                ctx.Graph.Time = ctx.ActiveTime;
                ctx.Graph.Realtime = ctx.Realtime;
                system.Execute(ref ctx);
            }
            else
            {
                ctx.Graph.WaitAll();
                system.Execute(ref ctx);
            }
        }
    }

    /// <summary>Adds a child system, executed after the systems already in the group.</summary>
    public void Add(ISystem system)
    {
        Systems.Add(system);
    }
}