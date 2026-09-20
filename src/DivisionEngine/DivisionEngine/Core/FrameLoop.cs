namespace DivisionEngine;

/// <summary>
///     The frame's phases in order (Notes/Core/LoopSystem.md): FrameBegin, the fixed-step loop
///     (FixedPre, FixedUpdate, Physics, FixedPost, zero or more times), Update, PostUpdate,
///     TransformPropagation, LateUpdate, Extract, Render, FrameEnd. Systems are added to a phase
///     through <see cref="this[PhaseId]" />; <see cref="Freeze" /> declares which phases may not
///     write a component type.
/// </summary>
public sealed class FrameLoop : SystemGroup
{
    private readonly Dictionary<PhaseId, PhaseGroup> _phases = new();

    public FrameLoop(double fixedDeltaTime = 0.02, int maxFixedStepsPerFrame = FixedUpdateTimeProvider.DefaultMaxStepsPerFrame)
    {
        FrameBegin = AddPhase(new PhaseGroup(PhaseId.FrameBegin, false, true));

        FixedLoop = new TimeSteppedGroup<FixedUpdateTimeProvider>(new FixedUpdateTimeProvider(fixedDeltaTime, maxFixedStepsPerFrame));
        FixedPre = AddPhase(new PhaseGroup(PhaseId.FixedPre, false), FixedLoop);
        FixedUpdate = AddPhase(new PhaseGroup(PhaseId.FixedUpdate, true), FixedLoop);
        Physics = AddPhase(new PhaseGroup(PhaseId.Physics, false), FixedLoop);
        FixedPost = AddPhase(new PhaseGroup(PhaseId.FixedPost, true), FixedLoop);
        Systems.Add(FixedLoop);

        Update = AddPhase(new PhaseGroup(PhaseId.Update, true));
        PostUpdate = AddPhase(new PhaseGroup(PhaseId.PostUpdate, true));
        TransformPropagation = AddPhase(new PhaseGroup(PhaseId.TransformPropagation, false));
        LateUpdate = AddPhase(new PhaseGroup(PhaseId.LateUpdate, true));
        Extract = AddPhase(new PhaseGroup(PhaseId.Extract, false));
        Render = AddPhase(new PhaseGroup(PhaseId.Render, false, true));
        FrameEnd = AddPhase(new PhaseGroup(PhaseId.FrameEnd, true, true));

        TransformPropagation.Add(new TransformPropagationSystem());

        // "The world transform is settled from LateUpdate on" as an enforced contract
        // (Notes/Core/LoopSystem.md): local transforms stop changing once propagation starts, and the
        // world transforms it produces stay put for the rest of the frame.
        Freeze<LocalTransform>(PhaseId.TransformPropagation, PhaseId.LateUpdate, PhaseId.Extract, PhaseId.Render);
        Freeze<WorldTransform>(PhaseId.LateUpdate, PhaseId.Extract, PhaseId.Render);
    }

    public PhaseGroup FrameBegin { get; }
    public TimeSteppedGroup<FixedUpdateTimeProvider> FixedLoop { get; }
    public PhaseGroup FixedPre { get; }
    public PhaseGroup FixedUpdate { get; }
    public PhaseGroup Physics { get; }
    public PhaseGroup FixedPost { get; }
    public PhaseGroup Update { get; }
    public PhaseGroup PostUpdate { get; }
    public PhaseGroup TransformPropagation { get; }
    public PhaseGroup LateUpdate { get; }
    public PhaseGroup Extract { get; }
    public PhaseGroup Render { get; }
    public PhaseGroup FrameEnd { get; }

    public IReadOnlyCollection<PhaseGroup> Phases => _phases.Values;

    public PhaseGroup this[PhaseId phase] =>
        _phases.TryGetValue(phase, out var group) ? group : throw new KeyNotFoundException($"Phase {phase} is not part of the frame loop.");

    /// <summary>Declares that <paramref name="type" /> may not be written during <paramref name="phases" />.</summary>
    public void Freeze(ComponentTypeId type, params ReadOnlySpan<PhaseId> phases)
    {
        foreach (var phase in phases)
        {
            this[phase].FrozenTypes.Add(type);
        }
    }

    public void Freeze<T>(params ReadOnlySpan<PhaseId> phases)
    {
        Freeze(ComponentType<T>.Id, phases);
    }

    private PhaseGroup AddPhase(PhaseGroup group, SystemGroup? parent = null)
    {
        (parent ?? this).Add(group);
        _phases.Add(group.Phase, group);
        return group;
    }
}
