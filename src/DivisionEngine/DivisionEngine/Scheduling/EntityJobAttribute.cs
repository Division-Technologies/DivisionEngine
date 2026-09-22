using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace DivisionEngine;

/// <summary>
///     Marks a partial struct whose <c>Execute</c> method describes work over one entity, from which
///     the source generator derives everything else.
///     <para>
///         The point is not brevity. A job's declared access and what its body actually touches are
///         two statements of the same thing, and when they drift apart the result is a data race that
///         only the debug safety checks catch, at run time. Deriving the declaration from the
///         signature makes them impossible to disagree.
///     </para>
///     <para>
///         <c>ref T</c> is a write, <c>in T</c> is a read. An <see cref="Entity" /> parameter receives
///         the entity, and an <c>in JobContext</c> parameter the frame's time and world. Components
///         that only filter — tags in particular, which have no data to pass — go in
///         <see cref="WithAllAttribute" />, <see cref="WithAnyAttribute" /> and
///         <see cref="WithNoneAttribute" />.
///     </para>
///     <example>
///         <code>
///         [EntityJob]
///         public partial struct Integrate
///         {
///             public float Delta;
///             private void Execute(ref Position position, in Velocity velocity)
///                 =&gt; position.X += velocity.X * Delta;
///         }
///
///         // in a system:
///         new Integrate { Delta = (float)context.Time.Delta }.Schedule(context);
///         </code>
///     </example>
///     Work that needs the chunk itself — walking a hierarchy, say — keeps using
///     <see cref="JobGraph.ScheduleChunks" /> directly.
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class EntityJobAttribute : Attribute;

/// <summary>Components an entity must have for the job to run on it, beyond those in the signature.</summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
public sealed class WithAllAttribute(params Type[] types) : Attribute
{
    public ImmutableArray<Type> Types { get; } = ImmutableCollectionsMarshal.AsImmutableArray(types);
}

/// <summary>Components an entity must have at least one of.</summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
public sealed class WithAnyAttribute(params Type[] types) : Attribute
{
    public ImmutableArray<Type> Types { get; } = ImmutableCollectionsMarshal.AsImmutableArray(types);
}

/// <summary>Components that exclude an entity from the job.</summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
public sealed class WithNoneAttribute(params Type[] types) : Attribute
{
    public ImmutableArray<Type> Types { get; } = ImmutableCollectionsMarshal.AsImmutableArray(types);
}
