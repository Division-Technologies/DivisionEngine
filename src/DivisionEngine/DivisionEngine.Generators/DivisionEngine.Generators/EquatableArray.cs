using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace DivisionEngine.Generators;

/// <summary>
///     An immutable array wrapper with value-based equality for use in incremental generator pipelines.
/// </summary>
public readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly ImmutableArray<T> _array;

    public EquatableArray(ImmutableArray<T> array)
    {
        _array = array;
    }

    public ImmutableArray<T> AsImmutableArray()
    {
        return _array.IsDefault ? ImmutableArray<T>.Empty : _array;
    }

    public bool Equals(EquatableArray<T> other)
    {
        return AsImmutableArray().SequenceEqual(other.AsImmutableArray());
    }

    public override bool Equals(object obj)
    {
        return obj is EquatableArray<T> other && Equals(other);
    }

    public override int GetHashCode()
    {
        var arr = AsImmutableArray();
        var hash = 0;
        foreach (var item in arr) hash = hash * 31 + item.GetHashCode();
        return hash;
    }

    public IEnumerator<T> GetEnumerator()
    {
        return ((IEnumerable<T>)AsImmutableArray()).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}