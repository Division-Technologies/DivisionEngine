namespace DivisionEngine;

/// <summary>Thrown when a job touches a resource it did not declare in its <see cref="AccessSet" />.</summary>
public sealed class JobAccessViolationException(string message) : InvalidOperationException(message);

/// <summary>
///     Runtime verification that jobs only touch what they declared. Each worker records the
///     access set of the job it is running; the world and chunk accessors consult it. Enabled by
///     default in Debug builds; the checks are a binary search over a few entries, so enabling
///     them in Release for diagnosis is cheap too.
/// </summary>
public static class JobSafety
{
    [ThreadStatic] private static AccessSet? _current;
    [ThreadStatic] private static string? _currentName;

#if DEBUG
    public static bool Enabled { get; set; } = true;
#else
    public static bool Enabled { get; set; }
#endif

    /// <summary>The access set of the job running on this thread, or null outside jobs.</summary>
    public static AccessSet? Current => _current;

    internal static void Enter(AccessSet access, string name)
    {
        _current = access;
        _currentName = name;
    }

    internal static void Exit()
    {
        _current = null;
        _currentName = null;
    }

    public static void AssertRead(ResourceId resource)
    {
        if (!Enabled)
        {
            return;
        }

        var current = _current;
        if (current is not null && !current.CanRead(resource))
        {
            throw new JobAccessViolationException(
                $"Job '{_currentName}' reads {resource} but declared only {current}.");
        }
    }

    public static void AssertWrite(ResourceId resource)
    {
        if (!Enabled)
        {
            return;
        }

        var current = _current;
        if (current is not null && !current.CanWrite(resource))
        {
            throw new JobAccessViolationException(
                $"Job '{_currentName}' writes {resource} but declared only {current}.");
        }
    }
}