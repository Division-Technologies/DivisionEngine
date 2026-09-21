namespace DivisionEngine.Tests.Profiling;

/// <summary>
///     The profiler has two shapes and both have to hold: compiled out (the default, where every
///     entry point must be an inert no-op) and compiled in with the native library present. These
///     tests assert whichever shape the build actually has, so the suite is meaningful either way.
/// </summary>
[TestFixture]
public sealed class ProfilerTests
{
    private static readonly ProfilerZoneSource Zone = Profiler.DeclareZone("Test zone");

    /// <summary>
    ///     Starts the profiler up front so that the instrumentation test below actually reaches
    ///     the native library. Every entry point is guarded by <see cref="Profiler.IsRunning" />,
    ///     so without this the calls would return before resolving anything and an export renamed
    ///     upstream would pass unnoticed. No-op when profiling is compiled out.
    /// </summary>
    [OneTimeSetUp]
    public void StartProfiler()
    {
        Profiler.Startup();
    }

    /// <summary>
    ///     Nothing here may throw, whether or not profiling is compiled in and whether or not the
    ///     profiler has been started. Instrumentation that can take the engine down is worse than
    ///     no instrumentation.
    /// </summary>
    [Test]
    public void Instrumentation_IsSafe_RegardlessOfBuild()
    {
        // When compiled in, this run really does cross into the native library: it is the only
        // coverage that each declared entry point still exists in the vendored Tracy version.

        var name = Profiler.DeclareName("Test frame");

        Assert.DoesNotThrow(() =>
        {
            using (var zone = Profiler.Zone(Zone))
            {
                zone.Name("named");
                zone.Text("some text");
                zone.Value(42);
            }

            Profiler.FrameMarkStart(name);
            Profiler.FrameMarkEnd(name);
            Profiler.FrameMark();
            Profiler.ConfigurePlot(name, PlotFormat.Memory, step: true);
            Profiler.Plot(name, 1.0);
            Profiler.Message("hello");

            var guarded = new ProfiledLock("Test lock");
            using (guarded.EnterScope())
            {
            }
        });
    }

    /// <summary>
    ///     A profiled lock is a lock first. Instrumentation that quietly stopped excluding anyone
    ///     would be far worse than no instrumentation, and nothing else in the suite would notice.
    /// </summary>
    [Test]
    public void ProfiledLock_StillExcludes()
    {
        var guarded = new ProfiledLock("Contended");
        var counter = 0;
        var inside = 0;
        var overlapped = false;

        var threads = new Thread[8];
        for (var i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(() =>
            {
                for (var iteration = 0; iteration < 20_000; iteration++)
                {
                    using (guarded.EnterScope())
                    {
                        if (Interlocked.Increment(ref inside) != 1)
                        {
                            overlapped = true;
                        }

                        counter++;
                        Interlocked.Decrement(ref inside);
                    }
                }
            });

            threads[i].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        Assert.Multiple(() =>
        {
            Assert.That(overlapped, Is.False, "two threads were inside the lock at once");
            Assert.That(counter, Is.EqualTo(threads.Length * 20_000));
        });
    }

    /// <summary>
    ///     Entering is safe before the profiler is up and whether or not lock reporting is on: the
    ///     announcement is retried rather than attempted once, because locks outlive that decision.
    /// </summary>
    [Test]
    public void ProfiledLock_IsSafe_RegardlessOfBuild()
    {
        var guarded = new ProfiledLock("Unannounced");

        Assert.DoesNotThrow(() =>
        {
            for (var iteration = 0; iteration < 3; iteration++)
            {
                using (guarded.EnterScope())
                {
                }
            }
        });
    }

    /// <summary>
    ///     Names are identities in the profiler: two pointers for one name would split a frame or
    ///     plot series in two.
    /// </summary>
    [Test]
    public void DeclareName_Interns()
    {
        var first = Profiler.DeclareName("Interned");
        var second = Profiler.DeclareName("Interned");

        Assert.That(second.Handle, Is.EqualTo(first.Handle));
    }

    [Test]
    public void CompiledOut_StaysInert()
    {
        if (Profiler.IsCompiledIn)
        {
            Assert.Ignore("Profiling is compiled in; see Startup_LoadsTheNativeLibrary.");
        }

        Assert.Multiple(() =>
        {
            Assert.That(Profiler.Startup(), Is.False);
            Assert.That(Profiler.IsRunning, Is.False);
            Assert.That(Profiler.IsConnected, Is.False);
            Assert.That(Profiler.UnavailableReason, Is.Not.Null);
        });
    }

    /// <summary>
    ///     With DIVISION_PROFILING defined, the DivisionTracy shared library has to be next to the
    ///     managed output. If this fails, the native build has not run or landed elsewhere; the
    ///     failure message carries the reason rather than a bare DllNotFoundException from
    ///     somewhere deep in a worker thread.
    /// </summary>
    [Test]
    public void Startup_LoadsTheNativeLibrary()
    {
        if (!Profiler.IsCompiledIn)
        {
            Assert.Ignore("Profiling is compiled out; build with -p:DivisionProfiling=true.");
        }

        Assert.That(Profiler.Startup(), Is.True, Profiler.UnavailableReason);
        Assert.Multiple(() =>
        {
            Assert.That(Profiler.IsRunning, Is.True);
            Assert.That(Profiler.UnavailableReason, Is.Null);

            // Nothing is attached in a test run, so the on-demand client records nothing. Reading
            // it at all proves the entry point resolves.
            Assert.That(Profiler.IsConnected, Is.False);
        });
    }
}
