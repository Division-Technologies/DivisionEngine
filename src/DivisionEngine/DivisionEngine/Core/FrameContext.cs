namespace DivisionEngine;

public ref struct FrameContext
{
    internal ref Time ActiveTimeInternal;
    public ref readonly Time ActiveTime => ref ActiveTimeInternal;
    public readonly Realtime Realtime;
    public readonly Engine Engine;
    public readonly World World;
    public readonly JobGraph Graph;

    public FrameContext(ref Time activeTimeInternal, Realtime realtime, Engine engine)
    {
        ActiveTimeInternal = ref activeTimeInternal;
        Realtime = realtime;
        Engine = engine;
        World = engine.World;
        Graph = engine.Graph;
    }
}