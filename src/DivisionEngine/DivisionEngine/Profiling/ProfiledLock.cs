using System.Runtime.CompilerServices;

namespace DivisionEngine;

/// <summary>
///     A declared call site for a lock. Permanent, like <see cref="ProfilerZoneSource" />: the
///     profiler keeps the pointer and reads the strings long afterwards.
/// </summary>
public readonly struct ProfilerLockSource
{
    internal readonly nint Handle;

    internal ProfilerLockSource(nint handle)
    {
        Handle = handle;
    }
}

/// <summary>
///     A mutual exclusion lock whose contention is drawn in the profiler: who held it, for how
///     long, and who was queued behind them.
/// </summary>
/// <remarks>
///     <para>
///         Use it as <c>using (_lock.EnterScope()) { ... }</c>. The <c>lock</c> statement cannot be
///         used because it takes the underlying <see cref="Lock" /> directly, leaving no place to
///         report the acquisition from.
///     </para>
///     <para>
///         Where a zone costs once per unit of work, this costs on every acquisition, so it is
///         meant for the few locks worth watching rather than for every lock in the engine. With
///         profiling compiled out the whole type is a <see cref="Lock" /> and nothing else; with it
///         compiled in but <see cref="Profiler.AreLocksVisible" /> off, one field read per
///         acquisition.
///     </para>
/// </remarks>
public sealed class ProfiledLock
{
    private readonly Lock _lock = new();

#if DIVISION_PROFILING
    private readonly ProfilerLockSource _source;

    /// <summary>The profiler's handle for this lock, or 0 while it has not been announced.</summary>
    private nint _context;
#endif

    /// <param name="name">Shown in the profiler's lock list; name it after what it guards.</param>
    public ProfiledLock(
        string name,
        [CallerMemberName] string function = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
#if DIVISION_PROFILING
        _source = Profiler.DeclareLock(name, function, file, line);
#else
        _ = name;
        _ = function;
        _ = file;
        _ = line;
#endif
    }

    /// <summary>Enters the lock, leaving it when the returned scope is disposed.</summary>
    public Scope EnterScope()
    {
#if DIVISION_PROFILING
        var context = _context;
        if (context == 0)
        {
            // Not announced yet. Retried rather than attempted once, because a lock can easily be
            // constructed before the profiler is started - a scheduler built by a test, say.
            context = Announce();
        }

        if (context != 0)
        {
            // Zero means nothing is being recorded at the moment (the UI is not attached), in which
            // case the acquisition must not be reported - but the release still must, since that is
            // what keeps the profiler's count of who holds the lock.
            var report = TracyNative.BeforeLock(context) != 0;
            var entered = _lock.EnterScope();
            if (report)
            {
                TracyNative.AfterLock(context);
            }

            return new Scope(entered, context);
        }
#endif

        return new Scope(_lock.EnterScope());
    }

#if DIVISION_PROFILING
    private nint Announce()
    {
        if (!Profiler.IsRunning || !Profiler.AreLocksVisible)
        {
            return 0;
        }

        // Under the lock itself: announcing twice would give the profiler two lockables for one
        // lock. This acquisition is the one that goes unrecorded, which is the right one to lose.
        lock (_lock)
        {
            if (_context == 0)
            {
                _context = Profiler.AnnounceLock(_source);
            }

            return _context;
        }
    }
#endif

    /// <summary>The held lock, released by <see cref="Dispose" />.</summary>
    public readonly ref struct Scope
    {
        private readonly Lock.Scope _scope;
#if DIVISION_PROFILING
        private readonly nint _context;
#endif

        internal Scope(Lock.Scope scope)
        {
            _scope = scope;
#if DIVISION_PROFILING
            _context = 0;
#endif
        }

#if DIVISION_PROFILING
        internal Scope(Lock.Scope scope, nint context)
        {
            _scope = scope;
            _context = context;
        }
#endif

        public void Dispose()
        {
            // Released first, reported second, as Tracy's own wrappers do: the profiler is told
            // when the lock became available, not when its owner decided to let go.
            _scope.Dispose();
#if DIVISION_PROFILING
            if (_context != 0)
            {
                TracyNative.AfterUnlock(_context);
            }
#endif
        }
    }
}