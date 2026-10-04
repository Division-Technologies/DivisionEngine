namespace DivisionEngine;

/// <summary>
///     What the renderer sees: a retained mirror of render-relevant state, filled during the
///     Extract phase and read during Render. Simulation data is never read by the renderer directly
///     (Notes/Core/SceneManagement.md, "全 Renderer クエリの実装実態"). A placeholder until there
///     is a renderer: it only carries the frame index so the handoff point exists.
/// </summary>
public sealed class RenderWorld
{
    /// <summary>Index of the simulation frame this render state was extracted from.</summary>
    public long FrameIndex { get; internal set; } = -1;
}

/// <summary>
///     Extract-phase system that stamps the render world with the current frame. Real extraction comes with the
///     renderer.
/// </summary>
internal sealed class ExtractFrameSystem : ISystem
{
    public void Execute(ref FrameContext ctx)
    {
        ctx.Engine.RenderWorld.FrameIndex = ctx.Engine.FrameIndex;
    }
}