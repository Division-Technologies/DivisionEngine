using DivisionEngine.Tests.Entities;

namespace DivisionEngine.Tests.Scheduling;

[TestFixture]
public sealed class AccessSetTests
{
    [Test]
    public void Build_Normalizes_AndImpliesStructureRead()
    {
        AccessSet access = Access.Read<Position>().Write<Velocity>().Read<Velocity>().Read<Position>();

        Assert.Multiple(() =>
        {
            Assert.That(access.Reads.ToArray(), Is.EqualTo(new[] { ResourceId.Structure, ResourceId.Component<Position>() }));
            Assert.That(access.Writes.ToArray(), Is.EqualTo(new[] { ResourceId.Component<Velocity>() }));
            Assert.That(access.CanRead(ResourceId.Component<Velocity>()), Is.True, "writes imply reads");
            Assert.That(access.CanWrite(ResourceId.Component<Position>()), Is.False);
            Assert.That(access.CanRead(ResourceId.Structure), Is.True);
            Assert.That(access.CanWrite(ResourceId.Structure), Is.False);
            Assert.That(access.WritesStructure, Is.False);
        });
    }

    [Test]
    public void None_AllowsNothing_Exclusive_AllowsEverything()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AccessSet.None.CanRead(ResourceId.Structure), Is.False);
            Assert.That(AccessSet.None.Reads.Length, Is.Zero);
            Assert.That(AccessSet.Exclusive.WritesStructure, Is.True);
            Assert.That(AccessSet.Exclusive.CanWrite(ResourceId.Component<Health>()), Is.True);
            Assert.That(AccessSet.Exclusive.CanRead(ResourceId.Named("anything")), Is.True);
        });
    }

    [Test]
    public void NamedResources_AreStableAndDoNotImplyStructure()
    {
        var queue = ResourceId.Named("test-queue");
        Assert.Multiple(() =>
        {
            Assert.That(ResourceId.Named("test-queue"), Is.EqualTo(queue));
            Assert.That(queue.IsNamed, Is.True);
            Assert.That(queue.ToString(), Is.EqualTo("test-queue"));
            Assert.That(ResourceId.Named("other-queue"), Is.Not.EqualTo(queue));
        });

        AccessSet access = new AccessSetBuilder().Write(queue);
        Assert.Multiple(() =>
        {
            Assert.That(access.Reads.Length, Is.Zero, "named-only access does not touch the structure");
            Assert.That(access.CanWrite(queue), Is.True);
        });
    }

    [Test]
    public void WriteStructure_DoesNotDuplicateStructureAsRead()
    {
        AccessSet access = Access.WriteStructure().Read<Position>();
        Assert.Multiple(() =>
        {
            Assert.That(access.WritesStructure, Is.True);
            Assert.That(access.Reads.ToArray(), Is.EqualTo(new[] { ResourceId.Component<Position>() }));
        });
    }
}
