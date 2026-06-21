namespace DivisionEngine;

public sealed class PresentationSystemGroup : UpdateSystemGroupBase<UpdateTimeProvider>
{
    public override UpdateTimeProvider InitializeProvider()
    {
        return default;
    }
}