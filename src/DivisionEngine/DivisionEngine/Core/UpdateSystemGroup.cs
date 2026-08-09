namespace DivisionEngine;

public sealed class UpdateSystemGroup : UpdateSystemGroupBase<UpdateTimeProvider>
{
    public override UpdateTimeProvider InitializeProvider()
    {
        return default;
    }
}