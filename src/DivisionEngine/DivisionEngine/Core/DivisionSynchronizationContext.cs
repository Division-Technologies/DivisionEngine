using System.Collections.Concurrent;

namespace DivisionEngine;

internal sealed class DivisionSynchronizationContext : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback d, object? state)> _queue = new();

    public override void Post(SendOrPostCallback d, object? state)
    {
        _queue.Enqueue((d, state));
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Current == this)
        {
            d(state);
            return;
        }

        var ctx = SendContext.Get(d, state);
        _queue.Enqueue((static state => { ((SendContext)state!).Execute(); }, ctx));
        if (ctx.Wait() is { } ex) throw ex;
    }

    public void Update()
    {
        while (_queue.TryDequeue(out var item)) item.d(item.state);
    }

    private class SendContext
    {
        private static readonly ConcurrentQueue<SendContext> _pool = new();
        private readonly SemaphoreSlim _semaphore = new(0, 1);
        private SendOrPostCallback? _callback;
        private Exception? _exception;
        private object? _state;

        public static SendContext Get(SendOrPostCallback callback, object state)
        {
            if (!_pool.TryDequeue(out var context)) context = new SendContext();
            context._semaphore.Wait();
            context._callback = callback;
            context._state = state;
            return context;
        }

        public void Execute()
        {
            try
            {
                _callback.Invoke(_state);
            }
            catch (Exception ex)
            {
                _exception = ex;
            }
            finally
            {
                _semaphore.Release();
                _callback = null;
                _state = null;
            }
        }

        public Exception? Wait()
        {
            _semaphore.Wait();
            var ex = _exception;
            _exception = null;
            _semaphore.Release();
            return ex;
        }
    }
}