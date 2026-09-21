using System.Buffers;
using System.Text;

namespace DivisionEngine;

/// <summary>
///     A declared call site for a zone. Build one once into a <c>static readonly</c> field with
///     <see cref="Profiler.DeclareZone" /> and reuse it: the profiler keeps the pointer and reads
///     the strings later, so declarations are permanent by construction.
/// </summary>
public readonly struct ProfilerZoneSource
{
    internal readonly nint Handle;

    internal ProfilerZoneSource(nint handle)
    {
        Handle = handle;
    }
}

/// <summary>
///     A declared name for a frame or a plot. Tracy treats the pointer as the identity of the
///     series, so these are permanent too; build them with <see cref="Profiler.DeclareName" />.
/// </summary>
public readonly struct ProfilerName
{
    internal readonly nint Handle;

    internal ProfilerName(nint handle)
    {
        Handle = handle;
    }
}

/// <summary>
///     How a plot's values are drawn and formatted. Mirrors Tracy's <c>TracyPlotFormatEnum</c>.
/// </summary>
public enum PlotFormat
{
    /// <summary>A plain number.</summary>
    Number = 0,

    /// <summary>Bytes, shown with a unit.</summary>
    Memory = 1,

    /// <summary>A percentage.</summary>
    Percentage = 2,

    /// <summary>Watts.</summary>
    Watt = 3,
}

/// <summary>
///     A running zone, ended by <see cref="Dispose" />. Obtained from <see cref="Profiler.Zone" />
///     and meant to be used as <c>using var zone = Profiler.Zone(Source);</c>.
/// </summary>
/// <remarks>
///     A ref struct on purpose: a zone must begin and end on the same thread, so it must not be
///     stored on the heap or held across an <c>await</c>. When profiling is compiled out this is
///     an empty struct whose methods have empty bodies, so the JIT erases the whole pattern.
/// </remarks>
public readonly ref struct ProfilerZone
{
    /// <summary>Names longer than this in UTF-8 borrow a buffer from the pool instead of the stack.</summary>
    private const int StackBufferBytes = 256;

#if DIVISION_PROFILING
    private readonly TracyNative.ZoneContext _context;
    private readonly bool _active;

    internal ProfilerZone(TracyNative.ZoneContext context)
    {
        _context = context;
        _active = true;
    }
#endif

    /// <summary>Overrides the displayed name, for zones whose identity is only known at run time (a job's name).</summary>
    public void Name(ReadOnlySpan<char> name)
    {
#if DIVISION_PROFILING
        if (!_active)
        {
            return;
        }

        Emit(name, asName: true);
#endif
    }

    /// <summary>Attaches free-form text to the zone (a turn id, an entity count).</summary>
    public void Text(ReadOnlySpan<char> text)
    {
#if DIVISION_PROFILING
        if (!_active)
        {
            return;
        }

        Emit(text, asName: false);
#endif
    }

    /// <summary>Attaches a number to the zone, shown alongside it in the UI.</summary>
    public void Value(ulong value)
    {
#if DIVISION_PROFILING
        if (_active)
        {
            TracyNative.ZoneValue(_context, value);
        }
#endif
    }

    public void Dispose()
    {
#if DIVISION_PROFILING
        if (_active)
        {
            TracyNative.ZoneEnd(_context);
        }
#endif
    }

#if DIVISION_PROFILING
    private unsafe void Emit(ReadOnlySpan<char> text, bool asName)
    {
        Span<byte> stack = stackalloc byte[StackBufferBytes];
        var maxBytes = Encoding.UTF8.GetMaxByteCount(text.Length);
        var rented = maxBytes <= StackBufferBytes ? null : ArrayPool<byte>.Shared.Rent(maxBytes);
        var buffer = rented is null ? stack : rented.AsSpan();

        var written = Encoding.UTF8.GetBytes(text, buffer);
        fixed (byte* pointer = buffer)
        {
            if (asName)
            {
                TracyNative.ZoneName(_context, pointer, (nuint)written);
            }
            else
            {
                TracyNative.ZoneText(_context, pointer, (nuint)written);
            }
        }

        if (rented is not null)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
#endif
}
