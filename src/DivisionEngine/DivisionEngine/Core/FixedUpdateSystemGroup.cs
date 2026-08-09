namespace DivisionEngine;

public sealed class FixedUpdateSystemGroup : UpdateSystemGroupBase<FixedUpdateTimeProvider>
{
    public override FixedUpdateTimeProvider InitializeProvider()
    {
        return new FixedUpdateTimeProvider(0.02);
    }
}