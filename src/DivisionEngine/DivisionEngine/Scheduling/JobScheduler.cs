using System.Collections.Concurrent;

namespace DivisionEngine;

/// <summary>
///     Worker pool that executes ready task nodes. Work items go to a shared queue; nodes flagged
///     for the main thread go to a separate queue that only the thread which created the scheduler
///     drains. The main thread participates as a worker while it waits, so it never idles and can
///     never deadlock on main-thread-only work.
/// </summary>
public sealed class JobScheduler : IDisposable
{
    [ThreadStatic] private static int _workerIndex; // 0 = not a pool worker (main or foreign thread)

    private readonly ConcurrentQueue<(TaskNode node, int work)> _mainQueue = new();
    private readonly SemaphoreSlim _mainWake = new(0);
    private readonly ConcurrentQueue<(TaskNode node, int work)> _queue = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Thread[] _workers;
    private volatile ConcurrentQueue<Exception>? _failed;
    private int _live;
    private volatile bool _mainWaiting;

    /// <param name="workerCount">Pool threads besides the main thread. 0 runs everything on the main thread (useful as a sequential oracle).</param>
    public JobScheduler(int workerCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(workerCount);
        MainThreadId = Environment.CurrentManagedThreadId;
        _workers = new Thread[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            var index = i + 1;
            _workers[i] = new Thread(() => WorkerLoop(index))
            {
                IsBackground = true,
                Name = $"Division Worker {index}"
            };
            _workers[i].Start();
        }
    }

    public static JobScheduler CreateDefault()
    {
        return new JobScheduler(Math.Max(0, Environment.ProcessorCount - 1));
    }

    public int WorkerCount => _workers.Length;

    public int MainThreadId { get; }

    /// <summary>1-based index of the current pool worker, 0 on the main thread or foreign threads.</summary>
    public static int CurrentWorkerIndex => _workerIndex;

    /// <summary>Nodes issued and not yet completed.</summary>
    public int LiveNodeCount => Volatile.Read(ref _live);

    public void Dispose()
    {
        _shutdown.Cancel();
        if (_workers.Length > 0)
        {
            _signal.Release(_workers.Length);
        }

        foreach (var worker in _workers)
        {
            worker.Join();
        }

        _signal.Dispose();
        _mainWake.Dispose();
        _shutdown.Dispose();
    }

    internal void Register(TaskNode node)
    {
        Interlocked.Increment(ref _live);
    }

    internal void Enqueue(TaskNode node, int workCount)
    {
        if (node.MainThread)
        {
            for (var i = 0; i < workCount; i++)
            {
                _mainQueue.Enqueue((node, i));
            }

            _mainWake.Release();
            return;
        }

        for (var i = 0; i < workCount; i++)
        {
            _queue.Enqueue((node, i));
        }

        if (_workers.Length > 0)
        {
            _signal.Release(Math.Min(workCount, _workers.Length));
        }

        if (_mainWaiting)
        {
            _mainWake.Release();
        }
    }

    internal void OnCompleted(TaskNode node, bool hasWaiter)
    {
        if (node.CountsAsLive)
        {
            Interlocked.Decrement(ref _live);
        }

        if (hasWaiter || _mainWaiting)
        {
            _mainWake.Release();
        }
    }

    /// <summary>Records a failure to be rethrown by the next <see cref="Wait" /> / <see cref="WaitAll" /> on the main thread.</summary>
    public void ReportError(Exception error)
    {
        (_failed ??= new ConcurrentQueue<Exception>()).Enqueue(error);
    }

    /// <summary>Runs jobs on the calling (main) thread until <paramref name="node" /> completes, then rethrows job failures.</summary>
    internal void Wait(TaskNode node)
    {
        ThrowIfNotMainThread();
        node.MarkWaited();
        _mainWaiting = true;
        try
        {
            while (!node.IsCompleted)
            {
                if (TryRunOne(true))
                {
                    continue;
                }

                _mainWake.Wait();
            }
        }
        finally
        {
            _mainWaiting = false;
        }

        ThrowIfFailed();
    }

    /// <summary>Runs jobs on the calling (main) thread until every issued node has completed, then rethrows job failures.</summary>
    public void WaitAll()
    {
        ThrowIfNotMainThread();
        _mainWaiting = true;
        try
        {
            while (Volatile.Read(ref _live) > 0)
            {
                if (TryRunOne(true))
                {
                    continue;
                }

                _mainWake.Wait();
            }
        }
        finally
        {
            _mainWaiting = false;
        }

        ThrowIfFailed();
    }

    /// <summary>Runs one queued work item on the calling thread if any is available.</summary>
    public bool TryRunOne(bool mainThread)
    {
        if (mainThread && _mainQueue.TryDequeue(out var item))
        {
            item.node.Execute(item.work);
            return true;
        }

        if (_queue.TryDequeue(out item))
        {
            item.node.Execute(item.work);
            return true;
        }

        return false;
    }

    private void WorkerLoop(int index)
    {
        _workerIndex = index;
        var token = _shutdown.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                _signal.Wait(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (TryRunOne(false))
            {
            }
        }
    }

    private void ThrowIfNotMainThread()
    {
        if (Environment.CurrentManagedThreadId != MainThreadId)
        {
            throw new InvalidOperationException("Jobs can only be waited for from the thread that owns the scheduler.");
        }
    }

    private void ThrowIfFailed()
    {
        var failed = _failed;
        if (failed is null || failed.IsEmpty)
        {
            return;
        }

        var errors = new List<Exception>();
        while (failed.TryDequeue(out var error))
        {
            errors.Add(error);
        }

        throw errors.Count == 1 ? new JobFailedException(errors[0]) : new JobFailedException(new AggregateException(errors));
    }
}

/// <summary>Wraps an exception thrown by a job body; rethrown on the main thread by the wait that observes it.</summary>
public sealed class JobFailedException(Exception inner) : Exception("A job failed. See InnerException.", inner);
