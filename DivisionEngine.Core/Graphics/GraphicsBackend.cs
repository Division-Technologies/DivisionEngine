namespace DivisionEngine.Graphics;

public abstract partial class GraphicsBackend : IDisposable
{
    public abstract void Dispose();
    public static partial GraphicsBackend CreateBackend();

    public abstract void Initialize();

    public abstract void Render();
}