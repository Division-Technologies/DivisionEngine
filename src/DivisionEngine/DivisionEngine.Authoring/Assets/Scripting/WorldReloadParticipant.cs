namespace DivisionEngine.Authoring.Assets;

/// <summary>
///     Carries an entity world across a user-assembly swap: the engine-side half of
///     <see cref="WorldReload" /> presented as something <see cref="AssetDatabase.ReloadScripts" />
///     can call at the right moments.
///     <para>
///         Behaviour turns are cancelled rather than carried. Their suspended state lives in a
///         compiler-generated state machine belonging to a type that is about to stop existing, so
///         the rule is that durable state goes in components and turns restart from their entry
///         point. Restarting them is the caller's business — this type only stops them.
///     </para>
/// </summary>
public sealed class WorldReloadParticipant(Engine engine) : IReloadParticipant
{
    private byte[]? _snapshot;

    /// <summary>Turns cancelled by the last reload, for callers that restart behaviours afterwards.</summary>
    public int CancelledTurns { get; private set; }

    public void BeforeSwap()
    {
        // A plain ISystem runs after the group has waited for everything scheduled before it, so the
        // graph is already quiescent here; closing the frame would close the phase we are inside.
        CancelledTurns = engine.Graph.CancelAllTurns();
        _snapshot = WorldReload.BeforeSwap(engine.World);
    }

    public void AfterSwap()
    {
        if (_snapshot is null)
        {
            return;
        }

        WorldReload.AfterSwap(engine.World, _snapshot);
        _snapshot = null;
    }
}
