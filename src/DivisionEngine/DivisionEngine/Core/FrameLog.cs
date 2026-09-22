using System.Collections.Immutable;

namespace DivisionEngine;

/// <summary>
///     Identifies one external completion (an await on something outside the graph, or a background
///     job) of one behavior: the turn and the per-turn sequence number assigned when the await
///     started. The sequence is assigned inside the segment, so it does not depend on timing.
/// </summary>
public readonly record struct ExternalKey(int TurnId, int Sequence) : IComparable<ExternalKey>
{
    public int CompareTo(ExternalKey other)
    {
        var byTurn = TurnId.CompareTo(other.TurnId);
        return byTurn != 0 ? byTurn : Sequence.CompareTo(other.Sequence);
    }

    public override string ToString()
    {
        return $"{TurnId}#{Sequence}";
    }
}

/// <summary>Everything the engine observed from outside during one frame: the clock sample and the external completions admitted.</summary>
public sealed class FrameRecord
{
    public FrameRecord(Realtime realtime, ImmutableArray<ExternalKey> externals)
    {
        Realtime = realtime;
        Externals = externals;
    }

    public Realtime Realtime { get; }
    public ImmutableArray<ExternalKey> Externals { get; }
}

/// <summary>
///     A recording of the non-deterministic inputs of a run, frame by frame. Feeding it back with
///     <see cref="Engine.Replay" /> reproduces the run: the same clock samples and the same external
///     completions are admitted in the same frames, so every job and behavior sees the same
///     values. Determinism of everything else is the job system's guarantee.
/// </summary>
public sealed class FrameLog
{
    private readonly List<FrameRecord> _frames = new();

    public IReadOnlyList<FrameRecord> Frames => _frames;

    public int Count => _frames.Count;

    public void Add(FrameRecord record)
    {
        _frames.Add(record);
    }
}
