using DivisionEngine;

namespace DivisionEngine.Authoring.Assets;

/// <summary>
///     Applies pending user-assembly reloads once per frame, on the engine thread. Register it after
///     <c>AssetRefreshSystem</c>: the refresh system processes file changes (recompiling changed
///     scripts and flagging the database), and this system then performs the reload — serializing the
///     live object graph, swapping the user assembly context, and restoring state against the new
///     types. Keeping the reload on the engine thread avoids mutating the object graph concurrently.
/// </summary>
public sealed class ScriptReloadSystem(AssetDatabase database, ScriptHost host) : ISystem
{
    public void Execute(ref FrameContext ctx) => database.ReloadScriptsIfDirty(host);
}
