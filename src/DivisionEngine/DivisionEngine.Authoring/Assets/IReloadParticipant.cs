namespace DivisionEngine.Authoring.Assets;

/// <summary>
///     State that lives outside the asset graph but still has to cross a user-assembly swap.
///     <para>
///         <see cref="AssetDatabase" /> knows how to carry its own scopes across a reload, but not the
///         entity world; and the world cannot simply be saved before the reload and restored after,
///         because the two halves have to straddle the moment the assemblies change. Implementations
///         hold whatever they wrote between the two calls.
///     </para>
/// </summary>
public interface IReloadParticipant
{
    /// <summary>
    ///     Called with the old assemblies still loaded and the asset graph already dropped. Write
    ///     yourself out and release every reference to user types, or the old context will not unload.
    /// </summary>
    void BeforeSwap();

    /// <summary>Called once the new assemblies are loaded and the assets have been rebuilt against them.</summary>
    void AfterSwap();
}
