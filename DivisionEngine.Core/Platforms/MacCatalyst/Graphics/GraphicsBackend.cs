#if MACCATALYST


namespace DivisionEngine.Graphics
{
    partial class GraphicsBackend
    {
        public static partial GraphicsBackend CreateBackend()
        {
            return new HeadlessGraphicsBackend();
        }
    }
}

#endif