namespace DivisionEngine;

/// <summary>
///     A point in the frame where parked behaviours are resumed (<c>await ctx.Phase(PhaseId.Update)</c>).
///     Phases are registered by name; the built-in ones are the frame loop's phases in order
///     (Notes/Core/LoopSystem.md). Resuming is a timing facility only: the resumed segment has no
///     data access until it declares some. Behaviours can only be resumed in phases whose group
///     dispatches behaviours (<see cref="PhaseGroup.DispatchesBehaviours" />).
/// </summary>
public readonly record struct PhaseId(int Value)
{
    private static readonly Lock RegistrationLock = new();
    private static readonly List<string> Names = new();

    /// <summary>Main thread: OS messages, input snapshot, external-input intake, authoring refresh. No behaviours.</summary>
    public static readonly PhaseId FrameBegin = Register("FrameBegin");

    /// <summary>Fixed step: fixed-step input, previous-transform bookkeeping. No behaviours.</summary>
    public static readonly PhaseId FixedPre = Register("FixedPre");

    /// <summary>Fixed step: fixed-step gameplay systems and behaviours.</summary>
    public static readonly PhaseId FixedUpdate = Register("FixedUpdate");

    /// <summary>Fixed step: physics writes transforms. No behaviours.</summary>
    public static readonly PhaseId Physics = Register("Physics");

    /// <summary>Fixed step: collision events, physics results.</summary>
    public static readonly PhaseId FixedPost = Register("FixedPost");

    /// <summary>Variable step: gameplay systems and behaviours (the default home of behaviours).</summary>
    public static readonly PhaseId Update = Register("Update");

    /// <summary>Variable step: animation evaluation, IK.</summary>
    public static readonly PhaseId PostUpdate = Register("PostUpdate");

    /// <summary>Local-to-world transform propagation. No behaviours.</summary>
    public static readonly PhaseId TransformPropagation = Register("TransformPropagation");

    /// <summary>Camera follow, UI layout: world transforms are final.</summary>
    public static readonly PhaseId LateUpdate = Register("LateUpdate");

    /// <summary>Copies render-relevant data to the render world. No behaviours.</summary>
    public static readonly PhaseId Extract = Register("Extract");

    /// <summary>Main thread: rendering and present. No behaviours.</summary>
    public static readonly PhaseId Render = Register("Render");

    /// <summary>Main thread: statistics, GC hints.</summary>
    public static readonly PhaseId FrameEnd = Register("FrameEnd");

    public string Name
    {
        get
        {
            lock (RegistrationLock)
            {
                return (uint)Value < (uint)Names.Count ? Names[Value] : $"Phase({Value})";
            }
        }
    }

    public static PhaseId Register(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (RegistrationLock)
        {
            var existing = Names.IndexOf(name);
            if (existing >= 0)
            {
                return new PhaseId(existing);
            }

            Names.Add(name);
            return new PhaseId(Names.Count - 1);
        }
    }

    public override string ToString()
    {
        return Name;
    }
}
