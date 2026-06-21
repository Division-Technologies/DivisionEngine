namespace DivisionEngine;

public class SystemGroup : ISystem
{
    protected readonly List<ISystem> Systems = new();

    public virtual void Execute(ref FrameContext ctx)
    {
        foreach (var system in Systems) system.Execute(ref ctx);
    }
}