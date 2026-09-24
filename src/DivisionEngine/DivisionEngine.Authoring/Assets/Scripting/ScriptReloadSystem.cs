namespace DivisionEngine.Authoring.Assets;

/// <summary>
///     Applies pending user-assembly reloads once per frame, on the engine thread. Register it after
///     <c>AssetRefreshSystem</c>: the refresh system processes file changes (recompiling changed
///     scripts and flagging the database), and this system then performs the reload — serializing the
///     live object graph, swapping the user assembly context, and restoring state against the new
///     types. Keeping the reload on the engine thread avoids mutating the object graph concurrently.
///     <para>
///         The entity world rides along through a <see cref="WorldReloadParticipant" />, which the
///         system builds from the frame's engine. Behavior turns do not survive: they are cancelled,
///         and anything that should outlive the reload belongs in a component.
///     </para>
///     Register it in <see cref="PhaseId.FrameBegin" />, where no behavior runs and no job is in
///     flight.
/// </summary>
public sealed class ScriptReloadSystem(AssetDatabase database, ScriptHost host) : ISystem
{
    /// <summary>The participant used by the most recent reload, or null if none has run.</summary>
    public WorldReloadParticipant? LastReload { get; private set; }

    public void Execute(ref FrameContext ctx)
    {
        if (!database.ScriptsDirty)
        {
            return;
        }

        var participant = new WorldReloadParticipant(ctx.Engine);
        LastReload = participant;
        database.ReloadScriptsIfDirty(host, participant);
    }
}