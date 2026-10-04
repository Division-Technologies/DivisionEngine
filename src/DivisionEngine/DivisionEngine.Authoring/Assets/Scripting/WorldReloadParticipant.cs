using Microsoft.Extensions.Logging;

namespace DivisionEngine.Authoring.Assets;

/// <summary>
///     Carries an entity world across a user-assembly swap: the engine-side half of
///     <see cref="WorldReload" /> presented as something <see cref="AssetDatabase.ReloadScripts" />
///     can call at the right moments.
///     <para>
///         Behavior turns are cancelled rather than carried. Their suspended state lives in a
///         compiler-generated state machine belonging to a type that is about to stop existing, so
///         the rule is that durable state goes in components and turns restart from their entry
///         point, which <see cref="BehaviorStartSystem" /> does on the next frame.
///     </para>
///     <para>
///         Whatever could not be carried is logged as a warning: components set aside because their
///         type is gone or no longer reads, and references that could not be saved or resolved.
///     </para>
/// </summary>
public sealed class WorldReloadParticipant(Engine engine) : IReloadParticipant
{
    private EntityScene? _snapshot;

    /// <summary>Turns cancelled by the last reload.</summary>
    public int CancelledTurns { get; private set; }

    /// <summary>What the last reload could not carry, as logged.</summary>
    public IReadOnlyList<string> Warnings { get; private set; } = [];

    public void Capture()
    {
        // Snapshot first: it only reads, so if it throws, nothing — not even the turns — has been
        // touched. A plain ISystem runs after its group has waited for everything scheduled before
        // it, so the graph is quiescent here.
        _snapshot = WorldReload.Capture(engine.World);
        CancelledTurns = engine.Graph.CancelAllTurns();
    }

    public void Release()
    {
        WorldReload.Release(engine.World);
    }

    public void Restore(ISerializedObjectResolver references)
    {
        if (_snapshot is not { } snapshot)
        {
            return;
        }

        _snapshot = null;
        var warnings = new List<string>(snapshot.Warnings);
        WorldReload.AfterSwap(engine.World, snapshot, references, warnings);
        Warnings = warnings;
        foreach (var warning in warnings)
        {
            engine.Logger.LogWarning("Script reload: {Warning}", warning);
        }
    }
}