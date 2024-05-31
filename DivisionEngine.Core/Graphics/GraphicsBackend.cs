namespace DivisionEngine.Graphics
{
    public abstract partial class GraphicsBackend : IDisposable
    {
        public static partial GraphicsBackend CreateBackend();

        public abstract void Dispose();

        public abstract void Initialize();

        public abstract void Render();
    }
}
