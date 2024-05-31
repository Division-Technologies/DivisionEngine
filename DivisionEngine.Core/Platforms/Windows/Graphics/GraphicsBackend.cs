#if WINDOWS

namespace DivisionEngine.Graphics
{
    partial class GraphicsBackend
    {
        public static partial GraphicsBackend CreateBackend()
        {
            return new D3D12Backend();
        }
    }
}

#endif