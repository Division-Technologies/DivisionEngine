using System.Runtime.InteropServices;

namespace DivisionEngine;

/// <summary>
///     A version of a component within a phase. Behavior writes are buffered into a round and
///     committed at the end of the phase in round order, then in (turn, sequence) order; reads
///     name the round whose result they want.
///     <list type="bullet">
///         <item><see cref="Initial" />: the state after the phase's systems ran and before any behavior commit. Never waits.</item>
///         <item><see cref="Label" />: a user-registered intermediate round; reading it waits until no turn can still write to it.</item>
///         <item><see cref="Main" />: the default round for writes.</item>
///         <item><see cref="Completed" />: after every commit of the phase; equivalent to the next phase's initial.</item>
///     </list>
/// </summary>
public readonly struct Round : IEquatable<Round>
{
    private Round(RoundKind kind, string? label)
    {
        Kind = kind;
        LabelName = label;
    }

    public RoundKind Kind { get; }
    public string? LabelName { get; }

    public static Round Initial => new(RoundKind.Initial, null);
    public static Round Main => new(RoundKind.Main, null);
    public static Round Completed => new(RoundKind.Completed, null);

    /// <summary>
    ///     A registered intermediate round. Deliberately no implicit conversion from string: with one,
    ///     a bare <c>null</c> in a conditional expression is inferred as string and turned into Label(null).
    /// </summary>
    public static Round Label(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new Round(RoundKind.Label, name);
    }

    public bool Equals(Round other)
    {
        return Kind == other.Kind && LabelName == other.LabelName;
    }

    public override bool Equals(object? obj)
    {
        return obj is Round other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Kind, LabelName);
    }

    public override string ToString()
    {
        return Kind == RoundKind.Label ? LabelName! : Kind.ToString().ToLowerInvariant();
    }
}

public enum RoundKind
{
    Initial,
    Label,
    Main,
    Completed
}

/// <summary>
///     One buffered behavior write: a value (last writer wins) or a modification applied to the
///     value current at commit time. Ordered by (turn, sequence), which does not depend on timing.
/// </summary>
internal sealed class PendingWrite
{
    public required int TurnId { get; init; }
    public required int Sequence { get; init; }
    public required Entity Entity { get; init; }
    public required ComponentTypeId Type { get; init; }
    public required int Round { get; init; }

    /// <summary>For value writes, the buffer the segment writes into; null for modifications.</summary>
    public byte[]? Value { get; init; }

    /// <summary>Applies this write to a buffer holding the component's bytes.</summary>
    public required Action<byte[]> Apply { get; init; }

    public static int Compare(PendingWrite a, PendingWrite b)
    {
        var byTurn = a.TurnId.CompareTo(b.TurnId);
        return byTurn != 0 ? byTurn : a.Sequence.CompareTo(b.Sequence);
    }

    public static PendingWrite ForValue(int turnId, int sequence, Entity entity, ComponentTypeInfo info, int round, byte[] initial)
    {
        var buffer = initial;
        return new PendingWrite
        {
            TurnId = turnId,
            Sequence = sequence,
            Entity = entity,
            Type = info.Id,
            Round = round,
            Value = buffer,
            Apply = target => buffer.AsSpan().CopyTo(target)
        };
    }

    public static PendingWrite ForModify<T>(int turnId, int sequence, Entity entity, int round, Func<T, T> modify) where T : unmanaged
    {
        return new PendingWrite
        {
            TurnId = turnId,
            Sequence = sequence,
            Entity = entity,
            Type = ComponentType<T>.Id,
            Round = round,
            Apply = target =>
            {
                ref var value = ref MemoryMarshal.AsRef<T>(target.AsSpan());
                value = modify(value);
            }
        };
    }
}
