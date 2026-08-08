namespace DivisionEngine;

public class SystemGroup : ISystem
{
    protected readonly List<ISystem> Systems = new();

    public virtual void Execute(ref FrameContext ctx)
    {
        foreach (var system in Systems)
        {
            system.Execute(ref ctx);
        }
    }

    /// <summary>Adds a child system, executed after the systems already in the group.</summary>
    public void Add(ISystem system)
    {
        Systems.Add(system);
    }
}