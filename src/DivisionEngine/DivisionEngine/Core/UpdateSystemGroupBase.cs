namespace DivisionEngine;

public abstract class UpdateSystemGroupBase<T> : SystemGroup where T : struct, ITimeProvider
{
    private T _provider;

    protected UpdateSystemGroupBase()
    {
        _provider = InitializeProvider();
    }

    public abstract T InitializeProvider();

    public sealed override void Execute(ref FrameContext ctx)
    {
        while (_provider.DoUpdate(ctx.Realtime, out var time))
        {
            ref var prevTime = ref ctx.ActiveTimeInternal;
            ctx.ActiveTimeInternal = time;
            base.Execute(ref ctx);
            ctx.ActiveTimeInternal = prevTime;
        }
    }
}