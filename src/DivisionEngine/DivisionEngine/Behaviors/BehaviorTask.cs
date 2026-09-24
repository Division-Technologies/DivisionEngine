using System.Runtime.CompilerServices;

namespace DivisionEngine;

/// <summary>
///     Return type of a behavior's <see cref="Behavior.Run" />. The method is compiled with
///     <see cref="BehaviorTaskMethodBuilder" />, which routes every continuation through the job
///     graph: awaits on the engine's own awaitables issue the next segment directly, and awaits on
///     anything else (Task, ValueTask, ...) resume through the frame's intake queue.
/// </summary>
[AsyncMethodBuilder(typeof(BehaviorTaskMethodBuilder))]
public readonly struct BehaviorTask
{
    private readonly BehaviorRun? _run;

    internal BehaviorTask(BehaviorRun run)
    {
        _run = run;
    }

    /// <summary>False until the behavior has started (its first segment ran) and finished.</summary>
    public bool IsCompleted => _run?.IsCompleted ?? false;

    public bool IsStarted => _run is not null;

    public bool IsFaulted => _run?.Exception is not null;

    public Exception? Exception => _run?.Exception;
}

/// <summary>Completion state shared between the builder (inside the state machine) and the task handle.</summary>
internal sealed class BehaviorRun
{
    public StateMachineBox? Box;
    public Exception? Exception;
    public volatile bool IsCompleted;

    public void SetCompleted()
    {
        IsCompleted = true;
    }

    public void SetFailed(Exception exception)
    {
        Exception = exception;
        IsCompleted = true;
        if (BehaviorContext.Current is { } context)
        {
            // A failed turn is not running: cancelling it lets the start system start the behavior
            // again from the top, the same as after a reload.
            context.Cancel();
            context.Graph.Scheduler.ReportError(
                exception is BehaviorFailedException failed && failed.Behavior == context.Name
                    ? failed // already attributed, e.g. rethrown from a background await
                    : new BehaviorFailedException(context.Name, exception));
        }
    }
}

/// <summary>
///     Marker for awaiters that schedule their continuation as a behavior segment themselves. The
///     builder hands the continuation straight to them; any other awaiter is treated as external.
/// </summary>
public interface IBehaviorAwaiter : ICriticalNotifyCompletion
{
}

/// <summary>Holds the boxed state machine of one behavior run and its cached MoveNext delegate.</summary>
internal abstract class StateMachineBox
{
    protected StateMachineBox(BehaviorContext context)
    {
        Context = context;
        MoveNextAction = MoveNext;
    }

    public BehaviorContext Context { get; }

    public Action MoveNextAction { get; }

    public abstract void MoveNext();

    /// <summary>
    ///     A continuation for foreign awaiters: completion lands in the graph's external queue, not on
    ///     the completing thread. The key is assigned now, inside the segment, so it is deterministic.
    /// </summary>
    public Action ExternalContinuation()
    {
        var sequence = Context.NextExternalSequence();
        return () => Context.Graph.EnqueueExternal(Context, sequence, MoveNextAction);
    }
}

internal sealed class StateMachineBox<TStateMachine>(BehaviorContext context) : StateMachineBox(context)
    where TStateMachine : IAsyncStateMachine
{
    public TStateMachine StateMachine = default!;

    public override void MoveNext()
    {
        StateMachine.MoveNext();
    }
}

/// <summary>Async method builder for <see cref="BehaviorTask" />. See the task type for the routing rules.</summary>
public struct BehaviorTaskMethodBuilder
{
    private BehaviorRun? _run;

    public static BehaviorTaskMethodBuilder Create()
    {
        return default;
    }

    public BehaviorTask Task => new(_run ??= new BehaviorRun());

    public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
    {
        stateMachine.MoveNext();
    }

    public void SetStateMachine(IAsyncStateMachine stateMachine)
    {
    }

    public void SetResult()
    {
        (_run ??= new BehaviorRun()).SetCompleted();
    }

    public void SetException(Exception exception)
    {
        (_run ??= new BehaviorRun()).SetFailed(exception);
    }

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        var box = GetBox(ref stateMachine);
        if (awaiter is IBehaviorAwaiter)
        {
            awaiter.OnCompleted(box.MoveNextAction);
        }
        else
        {
            awaiter.OnCompleted(box.ExternalContinuation());
        }
    }

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion
        where TStateMachine : IAsyncStateMachine
    {
        var box = GetBox(ref stateMachine);
        if (awaiter is IBehaviorAwaiter)
        {
            awaiter.UnsafeOnCompleted(box.MoveNextAction);
        }
        else
        {
            awaiter.UnsafeOnCompleted(box.ExternalContinuation());
        }
    }

    private StateMachineBox GetBox<TStateMachine>(ref TStateMachine stateMachine)
        where TStateMachine : IAsyncStateMachine
    {
        var run = _run ??= new BehaviorRun();
        if (run.Box is { } existing)
        {
            return existing;
        }

        var context = BehaviorContext.Current
                      ?? throw new InvalidOperationException(
                          "A BehaviorTask method must be started from a behavior segment (JobGraph.Start).");
        var box = new StateMachineBox<TStateMachine>(context);
        run.Box = box;
        // Copy the state machine into the box; the copy's builder already refers to this run.
        box.StateMachine = stateMachine;
        return box;
    }
}