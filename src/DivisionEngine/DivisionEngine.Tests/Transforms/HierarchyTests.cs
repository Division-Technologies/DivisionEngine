namespace DivisionEngine.Tests.Transforms;

[TestFixture]
public sealed class HierarchyTests
{
    private World _world = null!;

    [SetUp]
    public void SetUp()
    {
        _world = new World();
    }

    [TearDown]
    public void TearDown()
    {
        _world.Dispose();
    }

    private static List<Entity> Children(World world, Entity parent)
    {
        var children = new List<Entity>();
        foreach (var child in world.GetChildren(parent))
        {
            children.Add(child);
        }

        return children;
    }

    [Test]
    public void SetParent_LinksBothDirections()
    {
        var parent = _world.CreateTransform();
        var child = _world.CreateTransform();

        _world.SetParent(child, parent);

        Assert.Multiple(() =>
        {
            Assert.That(_world.GetParent(child), Is.EqualTo(parent));
            Assert.That(_world.HasChildren(parent), Is.True);
            Assert.That(Children(_world, parent), Is.EqualTo(new[] { child }));
            Assert.That(_world.HasComponent<Sibling>(child), Is.True);
            Assert.That(_world.HasComponent<Parent>(parent), Is.False, "the parent is still a root");
        });
    }

    [Test]
    public void Children_EnumerateInAttachmentOrder()
    {
        var parent = _world.CreateTransform();
        var a = _world.CreateTransform();
        var b = _world.CreateTransform();
        var c = _world.CreateTransform();

        _world.SetParent(a, parent);
        _world.SetParent(b, parent);
        _world.SetParent(c, parent);

        Assert.That(Children(_world, parent), Is.EqualTo(new[] { a, b, c }));
    }

    [Test]
    public void ClearParent_UnlinksAndDropsTheChildComponentWhenTheListEmpties()
    {
        var parent = _world.CreateTransform();
        var a = _world.CreateTransform();
        var b = _world.CreateTransform();
        _world.SetParent(a, parent);
        _world.SetParent(b, parent);

        _world.ClearParent(a);

        Assert.Multiple(() =>
        {
            Assert.That(_world.GetParent(a), Is.EqualTo(Entity.Null));
            Assert.That(_world.HasComponent<Sibling>(a), Is.False);
            Assert.That(Children(_world, parent), Is.EqualTo(new[] { b }));
        });

        _world.ClearParent(b);

        Assert.Multiple(() =>
        {
            Assert.That(_world.HasChildren(parent), Is.False);
            Assert.That(_world.HasComponent<Child>(parent), Is.False, "the empty list component is removed");
        });
    }

    [Test]
    public void ClearParent_RemovingTheMiddleChild_KeepsTheChainIntact()
    {
        var parent = _world.CreateTransform();
        var a = _world.CreateTransform();
        var b = _world.CreateTransform();
        var c = _world.CreateTransform();
        _world.SetParent(a, parent);
        _world.SetParent(b, parent);
        _world.SetParent(c, parent);

        _world.ClearParent(b);

        Assert.That(Children(_world, parent), Is.EqualTo(new[] { a, c }));
    }

    [Test]
    public void SetParent_Reparenting_MovesTheChildBetweenLists()
    {
        var first = _world.CreateTransform();
        var second = _world.CreateTransform();
        var child = _world.CreateTransform();
        _world.SetParent(child, first);

        _world.SetParent(child, second);

        Assert.Multiple(() =>
        {
            Assert.That(_world.GetParent(child), Is.EqualTo(second));
            Assert.That(_world.HasChildren(first), Is.False);
            Assert.That(Children(_world, second), Is.EqualTo(new[] { child }));
        });
    }

    [Test]
    public void SetParent_ToTheSameParent_IsANoOp()
    {
        var parent = _world.CreateTransform();
        var a = _world.CreateTransform();
        var b = _world.CreateTransform();
        _world.SetParent(a, parent);
        _world.SetParent(b, parent);

        _world.SetParent(a, parent);

        Assert.That(Children(_world, parent), Is.EqualTo(new[] { a, b }), "the child keeps its place in the list");
    }

    [Test]
    public void SetParent_RejectsCycles()
    {
        var a = _world.CreateTransform();
        var b = _world.CreateTransform();
        var c = _world.CreateTransform();
        _world.SetParent(b, a);
        _world.SetParent(c, b);

        Assert.Multiple(() =>
        {
            Assert.That(() => _world.SetParent(a, a), Throws.InvalidOperationException);
            Assert.That(() => _world.SetParent(a, b), Throws.InvalidOperationException);
            Assert.That(() => _world.SetParent(a, c), Throws.InvalidOperationException, "an indirect cycle too");
        });
    }

    [Test]
    public void SetParent_WithNullParent_DetachesTheChild()
    {
        var parent = _world.CreateTransform();
        var child = _world.CreateTransform();
        _world.SetParent(child, parent);

        _world.SetParent(child, Entity.Null);

        Assert.That(_world.GetParent(child), Is.EqualTo(Entity.Null));
        Assert.That(_world.HasChildren(parent), Is.False);
    }

    [Test]
    public void DestroyEntity_DestroysTheWholeSubtree()
    {
        var root = _world.CreateTransform();
        var child = _world.CreateTransform();
        var grandchild = _world.CreateTransform();
        var sibling = _world.CreateTransform();
        _world.SetParent(child, root);
        _world.SetParent(sibling, root);
        _world.SetParent(grandchild, child);

        _world.DestroyEntity(root);

        Assert.Multiple(() =>
        {
            Assert.That(_world.IsAlive(root), Is.False);
            Assert.That(_world.IsAlive(child), Is.False);
            Assert.That(_world.IsAlive(grandchild), Is.False);
            Assert.That(_world.IsAlive(sibling), Is.False);
            Assert.That(_world.EntityCount, Is.EqualTo(0));
        });
    }

    [Test]
    public void DestroyEntity_DetachesItFromItsParent()
    {
        var parent = _world.CreateTransform();
        var a = _world.CreateTransform();
        var b = _world.CreateTransform();
        _world.SetParent(a, parent);
        _world.SetParent(b, parent);

        _world.DestroyEntity(a);

        Assert.Multiple(() =>
        {
            Assert.That(_world.IsAlive(parent), Is.True, "destroying a child leaves the parent alone");
            Assert.That(_world.IsAlive(b), Is.True);
            Assert.That(Children(_world, parent), Is.EqualTo(new[] { b }));
        });
    }

    [Test]
    public void DestroyEntity_DestroyingTheLastChild_RemovesTheChildComponent()
    {
        var parent = _world.CreateTransform();
        var only = _world.CreateTransform();
        _world.SetParent(only, parent);

        _world.DestroyEntity(only);

        Assert.That(_world.HasComponent<Child>(parent), Is.False);
    }

    [Test]
    public void StructuralHook_RunsBeforeTheEntityIsRemoved()
    {
        var observed = new List<(Entity entity, bool alive)>();
        _world.AddStructuralHook(new RecordingHook(observed));
        var entity = _world.CreateEntity();

        _world.DestroyEntity(entity);

        Assert.That(observed, Is.EqualTo(new[] { (entity, true) }));
    }

    [Test]
    public void AddStructuralHook_IsIdempotent()
    {
        var hook = new RecordingHook(new List<(Entity, bool)>());

        _world.AddStructuralHook(hook);
        _world.AddStructuralHook(hook);

        Assert.That(_world.StructuralHooks, Has.Count.EqualTo(1));
    }

    private sealed class RecordingHook(List<(Entity entity, bool alive)> observed) : IStructuralHook
    {
        public void OnBeforeDestroy(World world, Entity entity)
        {
            observed.Add((entity, world.IsAlive(entity)));
        }
    }
}
