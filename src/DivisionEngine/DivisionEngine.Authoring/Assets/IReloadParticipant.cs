namespace DivisionEngine.Authoring.Assets;

/// <summary>
///     State that lives outside the asset graph but still has to cross a user-assembly swap.
///     <para>
///         <see cref="AssetDatabase" /> knows how to carry its own scopes across a reload, but not the
///         entity world; and the world cannot simply be saved before the reload and restored after,
///         because the two halves have to straddle the moment the assemblies change. Implementations
///         hold whatever they wrote between the calls.
///     </para>
///     <para>
///         The three steps are split by what a failure costs. <see cref="Capture" /> runs before
///         anything is torn down, so if it throws the reload is abandoned with nothing changed.
///         <see cref="Release" /> is past the point of no return and must not fail.
///         <see cref="Restore" /> runs even when the swap failed and the previous assemblies were put
///         back, so the participant always gets its state back.
///     </para>
/// </summary>
public interface IReloadParticipant
{
    /// <summary>
    ///     Called with the old assemblies loaded and nothing torn down yet. Write yourself out, but
    ///     change nothing: throwing here abandons the reload.
    /// </summary>
    void Capture();

    /// <summary>Release every reference to user types, or the old context will not unload. Must not fail.</summary>
    void Release();

    /// <summary>
    ///     Called once assemblies are loaded again — the new ones, or the previous ones if loading the
    ///     new ones failed — and the assets have been rebuilt against them.
    /// </summary>
    /// <param name="references">Resolves references to assets, which have just been rebuilt.</param>
    void Restore(ISerializedObjectResolver references);
}