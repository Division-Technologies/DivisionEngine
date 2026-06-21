namespace DivisionEngine;

public sealed class RootSystemGroup : SystemGroup
{
    internal RootSystemGroup()
    {
        Systems.Add(new FixedUpdateSystemGroup());
        Systems.Add(new UpdateSystemGroup());
        Systems.Add(new PresentationSystemGroup());
    }
}