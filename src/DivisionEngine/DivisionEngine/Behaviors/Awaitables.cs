namespace DivisionEngine;

/// <summary>
///     Builds the access set of the next segment: <c>await ctx.Access().Read&lt;A&gt;(e1).Write&lt;B&gt;(e2)</c>.
///     <see cref="At" /> picks the round reads see (default initial), <see cref="To" /> the round writes go to (default
///     main).
/// </summary>
public struct EntityAccessBuilder
{
    private readonly BehaviorContext _context;
    private AccessSetBuilder _builder;
    private Round _readRound;
    private Round _writeRound;

    internal EntityAccessBuilder(BehaviorContext context)
    {
        _context = context;
        _builder = new AccessSetBuilder();
        _readRound = Round.Initial;
        _writeRound = Round.Main;
    }

    public EntityAccessBuilder Read<T>(Entity entity)
    {
        _builder = _builder.ReadEntity<T>(entity);
        return this;
    }

    public EntityAccessBuilder Write<T>(Entity entity)
    {
        _builder = _builder.WriteEntity<T>(entity);
        return this;
    }

    /// <summary>The round reads observe. Labelled rounds wait until they are closed; completed resumes at the next phase.</summary>
    public EntityAccessBuilder At(Round round)
    {
        _readRound = round;
        return this;
    }

    /// <summary>The round writes are buffered into.</summary>
    public EntityAccessBuilder To(Round round)
    {
        _writeRound = round;
        return this;
    }

    public EntityAccessAwaiter GetAwaiter()
    {
        return new EntityAccessAwaiter(_context, _builder.Build(), _readRound, _writeRound);
    }
}

/// <summary>
///     Ends the current segment and issues the next one with the declared access.
///     Awaiters must carry all their state from construction: the state machine is boxed (copied)
///     before OnCompleted runs, so anything assigned there would be lost on the first await.
/// </summary>
public readonly struct EntityAccessAwaiter : IBehaviorAwaiter
{
    private readonly AccessSet _access;
    private readonly BehaviorContext _context;
    private readonly EntityAccess _handle;
    private readonly Round _readRound;

    internal EntityAccessAwaiter(BehaviorContext context, AccessSet access, Round readRound, Round writeRound)
    {
        _context = context;
        _access = access;
        _readRound = readRound;
        _handle = new EntityAccess(context, access, readRound, writeRound);

        // Fail inside the async method (a catchable behavior error) rather than inside the scheduler.
        if (context.InPhase && access.EntityWrites.Length > 0 &&
            context.Graph.ResolveRound(writeRound) < context.CurrentRound)
        {
            throw new InvalidOperationException(
                $"{context} cannot write to round '{writeRound}' after advancing past it.");
        }
    }

    public bool IsCompleted => false;

    public EntityAccess GetResult()
    {
        return _handle;
    }

    public void OnCompleted(Action continuation)
    {
        UnsafeOnCompleted(continuation);
    }

    public void UnsafeOnCompleted(Action continuation)
    {
        if (_readRound.Kind == RoundKind.Completed)
        {
            _handle.DemoteToInitialRead();
            _context.Graph.ParkOnCompleted(_context, _access, continuation, _handle);
            return;
        }

        Round? waitRound = _readRound.Kind == RoundKind.Initial ? null : _readRound;
        _context.Graph.IssueSegment(_context, _access, continuation, _handle, waitRound);
    }
}

public readonly struct ReadAwaitable<T>(BehaviorContext context, Entity target, Round round) where T : unmanaged
{
    public ReadAwaiter<T> GetAwaiter()
    {
        return new ReadAwaiter<T>(context.Access().Read<T>(target).At(round).GetAwaiter(), target);
    }
}

public readonly struct ReadAwaiter<T>(EntityAccessAwaiter inner, Entity target) : IBehaviorAwaiter where T : unmanaged
{
    public bool IsCompleted => false;

    public T GetResult()
    {
        return inner.GetResult().Get<T>(target);
    }

    public void OnCompleted(Action continuation)
    {
        inner.UnsafeOnCompleted(continuation);
    }

    public void UnsafeOnCompleted(Action continuation)
    {
        inner.UnsafeOnCompleted(continuation);
    }
}

public readonly struct TryReadAwaitable<T>(BehaviorContext context, Entity target, Round round) where T : unmanaged
{
    public TryReadAwaiter<T> GetAwaiter()
    {
        return new TryReadAwaiter<T>(context.Access().Read<T>(target).At(round).GetAwaiter(), target);
    }
}

public readonly struct TryReadAwaiter<T>(EntityAccessAwaiter inner, Entity target)
    : IBehaviorAwaiter where T : unmanaged
{
    public bool IsCompleted => false;

    public T? GetResult()
    {
        return inner.GetResult().TryGet<T>(target, out var value) ? value : null;
    }

    public void OnCompleted(Action continuation)
    {
        inner.UnsafeOnCompleted(continuation);
    }

    public void UnsafeOnCompleted(Action continuation)
    {
        inner.UnsafeOnCompleted(continuation);
    }
}

public readonly struct WriteAwaitable<T>(BehaviorContext context, Entity target, Round round) where T : unmanaged
{
    public WriteAwaiter<T> GetAwaiter()
    {
        return new WriteAwaiter<T>(context.Access().Write<T>(target).To(round).GetAwaiter(), target);
    }
}

public readonly struct WriteAwaiter<T>(EntityAccessAwaiter inner, Entity target) : IBehaviorAwaiter where T : unmanaged
{
    public bool IsCompleted => false;

    public ComponentRef<T> GetResult()
    {
        return new ComponentRef<T>(inner.GetResult(), target);
    }

    public void OnCompleted(Action continuation)
    {
        inner.UnsafeOnCompleted(continuation);
    }

    public void UnsafeOnCompleted(Action continuation)
    {
        inner.UnsafeOnCompleted(continuation);
    }
}

/// <summary>Reference into the write buffer of one component, valid for the segment that declared the write.</summary>
public readonly struct ComponentRef<T>(EntityAccess access, Entity entity) where T : unmanaged
{
    public Entity Entity => entity;

    public bool IsAlive => access.World.IsAlive(entity);

    public ref T Value => ref access.Ref<T>(entity);
}

public readonly struct PhaseAwaitable(BehaviorContext context, PhaseId phase)
{
    public PhaseAwaiter GetAwaiter()
    {
        return new PhaseAwaiter(context, phase);
    }
}

public readonly struct PhaseAwaiter(BehaviorContext context, PhaseId phase) : IBehaviorAwaiter
{
    public bool IsCompleted => false;

    public void GetResult()
    {
    }

    public void OnCompleted(Action continuation)
    {
        UnsafeOnCompleted(continuation);
    }

    public void UnsafeOnCompleted(Action continuation)
    {
        context.Graph.ParkOnPhase(context, phase, continuation);
    }
}

public readonly struct BackgroundAwaitable<T>(BehaviorContext context, Func<T> work)
{
    public BackgroundAwaiter<T> GetAwaiter()
    {
        return new BackgroundAwaiter<T>(context, work);
    }
}

public readonly struct BackgroundAwaiter<T> : IBehaviorAwaiter
{
    private readonly BehaviorContext _context;
    private readonly Result _result;
    private readonly Func<T> _work;

    internal BackgroundAwaiter(BehaviorContext context, Func<T> work)
    {
        _context = context;
        _work = work;
        _result = new Result(); // allocated before the state machine is boxed (see EntityAccessAwaiter)
    }

    public bool IsCompleted => false;

    public T GetResult()
    {
        if (_result.Error is { } error)
        {
            throw new BehaviorFailedException(_context.Name, error);
        }

        return _result.Value!;
    }

    public void OnCompleted(Action continuation)
    {
        UnsafeOnCompleted(continuation);
    }

    public void UnsafeOnCompleted(Action continuation)
    {
        var result = _result;
        var owner = _context;
        var job = _work;
        var sequence = _context.NextExternalSequence();
        _context.Graph.Schedule($"{_context.Name}.Background", AccessSet.None, _ =>
        {
            try
            {
                result.Value = job();
            }
            catch (Exception ex)
            {
                result.Error = ex;
            }

            owner.Graph.EnqueueExternal(owner, sequence, continuation);
        });
    }

    private sealed class Result
    {
        public Exception? Error;
        public T? Value;
    }
}