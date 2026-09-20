using System.Collections.Concurrent;
using DivisionEngine.Tests.Entities;

namespace DivisionEngine.Tests.Scheduling;

[TestFixture]
public sealed class JobSchedulerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static (JobScheduler scheduler, World world, JobGraph graph) Create(int workers)
    {
        var scheduler = new JobScheduler(workers);
        var world = new World();
        return (scheduler, world, new JobGraph(scheduler, world));
    }

    [Test]
    public void SingleJob_RunsAndCompletes()
    {
        var (scheduler, world, graph) = Create(2);
        using (scheduler)
        using (world)
        {
            var ran = 0;
            var handle = graph.Schedule("job", AccessSet.None, _ => Interlocked.Increment(ref ran));
            graph.Wait(handle);
            Assert.Multiple(() =>
            {
                Assert.That(ran, Is.EqualTo(1));
                Assert.That(handle.IsCompleted, Is.True);
                Assert.That(scheduler.LiveNodeCount, Is.Zero);
            });
        }
    }

    [Test]
    public void JobException_IsRethrownByWait()
    {
        var (scheduler, world, graph) = Create(2);
        using (scheduler)
        using (world)
        {
            var handle = graph.Schedule("boom", AccessSet.None, _ => throw new InvalidDataException("boom"));
            Assert.That(() => graph.Wait(handle),
                Throws.TypeOf<JobFailedException>().With.InnerException.TypeOf<InvalidDataException>());
            Assert.That(() => graph.WaitAll(), Throws.Nothing, "error is reported once");
        }
    }

    [Test]
    public void IndependentJobs_RunConcurrently()
    {
        var (scheduler, world, graph) = Create(4);
        using (scheduler)
        using (world)
        {
            const int jobs = 3;
            using var barrier = new Barrier(jobs);
            var arrived = 0;
            for (var i = 0; i < jobs; i++)
            {
                graph.Schedule($"concurrent {i}", AccessSet.None, _ =>
                {
                    // Only completes if all three jobs are running at the same time.
                    if (barrier.SignalAndWait(Timeout))
                    {
                        Interlocked.Increment(ref arrived);
                    }
                });
            }

            graph.WaitAll();
            Assert.That(arrived, Is.EqualTo(jobs));
        }
    }

    [Test]
    public void ConflictingWrites_SerializeInIssueOrder()
    {
        var (scheduler, world, graph) = Create(4);
        using (scheduler)
        using (world)
        {
            var order = new List<int>(); // unsynchronized on purpose: writers must be serialized
            for (var i = 0; i < 32; i++)
            {
                var index = i;
                graph.Schedule($"writer {i}", Access.Write<Health>(), _ =>
                {
                    Thread.SpinWait(Random.Shared.Next(100, 2000));
                    order.Add(index);
                });
            }

            graph.WaitAll();
            Assert.That(order, Is.EqualTo(Enumerable.Range(0, 32)));
        }
    }

    [Test]
    public void Readers_Share_And_WritersWaitForReaders()
    {
        var (scheduler, world, graph) = Create(4);
        using (scheduler)
        using (world)
        {
            var value = 0;
            var observed = new ConcurrentBag<int>();
            using var readersTogether = new Barrier(2);

            graph.Schedule("write 1", Access.Write<Health>(), _ => value = 1);
            for (var i = 0; i < 2; i++)
            {
                graph.Schedule($"read {i}", Access.Read<Health>(), _ =>
                {
                    observed.Add(value);
                    readersTogether.SignalAndWait(Timeout); // both readers run concurrently
                });
            }

            graph.Schedule("write 2", Access.Write<Health>(), _ => value = 2);
            graph.WaitAll();

            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.EqualTo(new[] { 1, 1 }));
                Assert.That(value, Is.EqualTo(2));
            });
        }
    }

    [Test]
    public void StructureWrite_IsOrderedAgainstEverything()
    {
        var (scheduler, world, graph) = Create(4);
        using (scheduler)
        using (world)
        {
            var log = new List<string>();
            graph.Schedule("A", Access.Read<Position>(), _ =>
            {
                Thread.Sleep(30);
                log.Add("A");
            });
            graph.Schedule("B", AccessSet.Exclusive, _ => log.Add("B"));
            graph.Schedule("C", Access.Write<Velocity>(), _ => log.Add("C"));
            graph.Schedule("D", new AccessSetBuilder().Write(ResourceId.Named("queue")), _ => log.Add("D"));
            graph.WaitAll();

            Assert.Multiple(() =>
            {
                Assert.That(log.IndexOf("A"), Is.LessThan(log.IndexOf("B")));
                Assert.That(log.IndexOf("B"), Is.LessThan(log.IndexOf("C")));
                Assert.That(log, Has.Count.EqualTo(4));
            });
        }
    }

    [Test]
    public void MainThreadJob_RunsOnMainThread_EvenWhileWorkersAreIdle()
    {
        var (scheduler, world, graph) = Create(4);
        using (scheduler)
        using (world)
        {
            var threadId = -1;
            var handle = graph.ScheduleOnMainThread("main", AccessSet.None, _ => threadId = Environment.CurrentManagedThreadId);
            graph.Wait(handle);
            Assert.That(threadId, Is.EqualTo(Environment.CurrentManagedThreadId));

            // A main-thread job that depends on a pool job: the main thread must not deadlock waiting.
            var order = new List<string>();
            graph.Schedule("pool", Access.Write<Health>(), _ =>
            {
                Thread.Sleep(20);
                order.Add("pool");
            });
            graph.ScheduleOnMainThread("main after pool", Access.Read<Health>(), _ => order.Add("main"));
            graph.WaitAll();
            Assert.That(order, Is.EqualTo(new[] { "pool", "main" }));
        }
    }

    [Test]
    public void ZeroWorkers_RunsEverythingOnTheMainThread()
    {
        var (scheduler, world, graph) = Create(0);
        using (scheduler)
        using (world)
        {
            var threads = new ConcurrentBag<int>();
            for (var i = 0; i < 8; i++)
            {
                graph.Schedule($"job {i}", AccessSet.None, _ => threads.Add(Environment.CurrentManagedThreadId));
            }

            graph.WaitAll();
            Assert.That(threads, Is.All.EqualTo(Environment.CurrentManagedThreadId));
            Assert.That(threads, Has.Count.EqualTo(8));
        }
    }

    [Test]
    public void ChunkJob_VisitsEveryChunkOnce()
    {
        var (scheduler, world, graph) = Create(4);
        using (scheduler)
        using (world)
        {
            const int count = 50_000;
            for (var i = 0; i < count; i++)
            {
                var e = world.CreateEntity(ComponentType<Position>.Id, ComponentType<Velocity>.Id);
                world.SetComponent(e, new Velocity(1, 0, 0));
            }

            for (var i = 0; i < 1_000; i++)
            {
                world.CreateEntity(ComponentType<Position>.Id); // second archetype
            }

            var query = world.Query().With<Position>().Build();
            var visits = 0;
            var workers = new ConcurrentBag<int>();
            graph.ScheduleChunks("mark", query, Access.Write<Position>(), (in _, chunk) =>
            {
                workers.Add(JobScheduler.CurrentWorkerIndex);
                var positions = chunk.GetSpan<Position>();
                for (var i = 0; i < positions.Length; i++)
                {
                    positions[i].X += 1;
                }

                Interlocked.Add(ref visits, chunk.Count);
            });
            graph.WaitAll();

            var sum = 0f;
            foreach (var chunk in query)
            {
                foreach (var p in chunk.GetReadOnlySpan<Position>())
                {
                    sum += p.X;
                }
            }

            Assert.Multiple(() =>
            {
                Assert.That(visits, Is.EqualTo(count + 1_000));
                Assert.That(sum, Is.EqualTo(count + 1_000), "each entity written exactly once");
                Assert.That(workers.Distinct().Count(), Is.GreaterThan(1), "batches spread over several threads");
            });
        }
    }

    [Test]
    public void ChunkJob_OnEmptyQuery_Completes()
    {
        var (scheduler, world, graph) = Create(2);
        using (scheduler)
        using (world)
        {
            var handle = graph.ScheduleChunks("empty", world.Query().With<Health>().Build(), Access.Write<Health>(),
                (in _, _) => Assert.Fail("no chunks expected"));
            graph.Wait(handle);
            Assert.That(handle.IsCompleted, Is.True);
        }
    }

    [Test]
    public void ChunkJob_SeesStructuralChanges_OrderedBeforeIt()
    {
        var (scheduler, world, graph) = Create(4);
        using (scheduler)
        using (world)
        {
            var ecb = new EntityCommandBuffer();
            for (var i = 0; i < 10; i++)
            {
                var e = ecb.CreateEntity();
                ecb.AddComponent(e, new Health { Value = i });
            }

            var query = world.Query().With<Health>().Build();
            var seen = 0;
            graph.SchedulePlayback(ecb);
            graph.ScheduleChunks("count", query, Access.Read<Health>(), (in _, chunk) => Interlocked.Add(ref seen, chunk.Count));
            graph.WaitAll();

            Assert.That(seen, Is.EqualTo(10), "chunks are snapshotted when the job becomes ready, after playback");
        }
    }

    [Test]
    public void EndFrame_ForgetsDependencies()
    {
        var (scheduler, world, graph) = Create(2);
        using (scheduler)
        using (world)
        {
            graph.Schedule("frame 1", Access.Write<Health>(), _ => Thread.Sleep(5));
            graph.EndFrame();
            var handle = graph.Schedule("frame 2", Access.Write<Health>(), _ => { });
            graph.Wait(handle);
            Assert.That(scheduler.LiveNodeCount, Is.Zero);
        }
    }

    [Test]
    public void SchedulingFromAJob_IsAllowed_AndWaitedForByWaitAll()
    {
        var (scheduler, world, graph) = Create(2);
        using (scheduler)
        using (world)
        {
            var innerRan = false;
            graph.Schedule("outer", AccessSet.None, _ => graph.Schedule("inner", AccessSet.None, _ =>
            {
                Thread.Sleep(10);
                innerRan = true;
            }));
            graph.WaitAll();
            Assert.That(innerRan, Is.True);
        }
    }
}
