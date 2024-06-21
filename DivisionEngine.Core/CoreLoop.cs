using DivisionEngine.Graphics;

namespace DivisionEngine;

internal class CoreLoop
{
    private readonly IGraphicsBackend graphicsBackend;

    public CoreLoop(IGraphicsBackend graphicsBackend)
    {
        this.graphicsBackend = graphicsBackend;
    }

    public void Execute()
    {
        // TODO: write main loop

        graphicsBackend.Render();
    }
}