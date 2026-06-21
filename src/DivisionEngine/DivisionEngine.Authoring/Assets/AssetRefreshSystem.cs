using DivisionEngine;

namespace DivisionEngine.Authoring.Assets;

/// <summary>
///     A system that applies pending asset changes once per frame by calling
///     <see cref="AssetDatabase.Refresh" /> on the engine thread. Register it with
///     <see cref="Engine.AddSystem" /> so file changes queued by the watcher are imported on the
///     engine thread rather than from a watcher callback.
/// </summary>
public sealed class AssetRefreshSystem(AssetDatabase database) : ISystem
{
    public void Execute(ref FrameContext ctx) => database.Refresh();
}
