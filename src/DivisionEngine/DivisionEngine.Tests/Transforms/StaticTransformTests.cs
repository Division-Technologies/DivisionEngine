using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;

namespace DivisionEngine.Tests.Transforms;

/// <summary>
///     <see cref="Static" /> takes a subtree out of propagation entirely. These tests fix both
///     halves of that bargain: the values are settled once before the tag goes on, and nothing
///     touches them afterwards.
/// </summary>
[TestFixture]
public sealed class StaticTransformTests
{
    [SetUp]
    public void SetUp()
    {
        _engine = new Engine(NullLogger.Instance, new JobScheduler(0));
        _world = _engine.World;
    }

    [TearDown]
    public void TearDown()
    {
        _engine.Dispose();
    }

    private Engine _engine = null!;
    private World _world = null!;

    private Vector3 WorldPositionOf(Entity entity)
    {
        return _world.GetComponentReadOnly<WorldTransform>(entity).Position;
    }

    [Test]
    public void MakeStatic_SettlesTheSubtreeBeforeExcludingIt()
    {
        var root = _world.CreateTransform(LocalTransform.FromPosition(new Vector3(10, 0, 0)));
        var child = _world.CreateTransform(LocalTransform.FromPosition(new Vector3(1, 0, 0)));
        _world.SetParent(child, root);

        // Marked before any frame has run, so the values can only come from MakeStatic itself.
        _world.MakeStatic(root);

        Assert.Multiple(() =>
        {
            Assert.That(WorldPositionOf(root).X, Is.EqualTo(10f).Within(1e-5f));
            Assert.That(WorldPositionOf(child).X, Is.EqualTo(11f).Within(1e-5f));
        });
    }

    [Test]
    public void Propagation_SkipsStaticSubtrees()
    {
        var root = _world.CreateTransform(LocalTransform.FromPosition(new Vector3(10, 0, 0)));
        var child = _world.CreateTransform(LocalTransform.FromPosition(new Vector3(1, 0, 0)));
        _world.SetParent(child, root);
        _world.MakeStatic(root);

        // Moving a static entity is a contradiction; doing it anyway proves propagation is not
        // looking at the subtree, which is exactly the property being bought.
        _world.GetComponent<LocalTransform>(root).Position = new Vector3(-100, 0, 0);
        _engine.RunFrame(Realtime.FromSeconds(0));

        Assert.Multiple(() =>
        {
            Assert.That(WorldPositionOf(root).X, Is.EqualTo(10f).Within(1e-5f));
            Assert.That(WorldPositionOf(child).X, Is.EqualTo(11f).Within(1e-5f));
        });
    }

    /// <summary>Marking the root freezes everything under it, whatever the children are tagged with.</summary>
    [Test]
    public void MakingARootStatic_FreezesTheWholeSubtree()
    {
        var root = _world.CreateTransform(LocalTransform.FromPosition(Vector3.Zero));
        var moving = _world.CreateTransform(LocalTransform.FromPosition(new Vector3(1, 0, 0)));
        var frozen = _world.CreateTransform(LocalTransform.FromPosition(new Vector3(5, 0, 0)));
        var underFrozen = _world.CreateTransform(LocalTransform.FromPosition(new Vector3(2, 0, 0)));
        _world.SetParent(moving, root);
        _world.SetParent(frozen, root);
        _world.SetParent(underFrozen, frozen);

        _engine.RunFrame(Realtime.FromSeconds(0));

        _world.MakeStatic(root);
        _world.GetComponent<LocalTransform>(frozen).Position = new Vector3(-50, 0, 0);
        _engine.RunFrame(Realtime.FromSeconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(WorldPositionOf(moving).X, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(WorldPositionOf(frozen).X, Is.EqualTo(5f).Within(1e-5f));
            Assert.That(WorldPositionOf(underFrozen).X, Is.EqualTo(7f).Within(1e-5f));
        });
    }

    [Test]
    public void MakeDynamic_PutsTheSubtreeBackIntoPropagation()
    {
        var root = _world.CreateTransform(LocalTransform.FromPosition(new Vector3(10, 0, 0)));
        var child = _world.CreateTransform(LocalTransform.FromPosition(new Vector3(1, 0, 0)));
        _world.SetParent(child, root);
        _world.MakeStatic(root);

        _world.MakeDynamic(root);
        _world.GetComponent<LocalTransform>(root).Position = new Vector3(20, 0, 0);
        _engine.RunFrame(Realtime.FromSeconds(0));

        Assert.Multiple(() =>
        {
            Assert.That(_world.IsStatic(root), Is.False);
            Assert.That(WorldPositionOf(root).X, Is.EqualTo(20f).Within(1e-5f));
            Assert.That(WorldPositionOf(child).X, Is.EqualTo(21f).Within(1e-5f));
        });
    }

    /// <summary>
    ///     The invariant the walk relies on, now that it no longer tests for <see cref="Static" />
    ///     per entity: a static subtree hangs only under a static root, and a moving entity is
    ///     always reachable from a moving root. Every route that could break it is closed.
    /// </summary>
    [Test]
    public void StaticnessCannotBeMixedAcrossAParentLink()
    {
        var mover = _world.CreateTransform(LocalTransform.Identity);
        var child = _world.CreateTransform(LocalTransform.Identity);
        _world.SetParent(child, mover);

        Assert.Throws<InvalidOperationException>(() => _world.MakeStatic(child),
            "a static child under a moving parent would keep a stale WorldTransform");

        var loose = _world.CreateTransform(LocalTransform.Identity);
        _world.MakeStatic(loose);
        Assert.Throws<InvalidOperationException>(() => _world.SetParent(loose, mover),
            "reparenting a static entity under a moving one is the same mistake");

        var moving = _world.CreateTransform(LocalTransform.Identity);
        Assert.Throws<InvalidOperationException>(() => _world.SetParent(moving, loose),
            "a moving entity under a static parent would never be reached by propagation");
    }

    [Test]
    public void MakeDynamic_IsRefusedUnderAStaticParent()
    {
        var root = _world.CreateTransform(LocalTransform.Identity);
        var child = _world.CreateTransform(LocalTransform.Identity);
        _world.SetParent(child, root);
        _world.MakeStatic(root);
        _world.MakeStatic(child);

        Assert.Throws<InvalidOperationException>(() => _world.MakeDynamic(child),
            "thawing inside a frozen subtree leaves an entity propagation never reaches");

        // From the top it is fine, and then the child may follow.
        _world.MakeDynamic(root);
        Assert.DoesNotThrow(() => _world.MakeDynamic(child));
    }

    [Test]
    public void MakeStatic_AndMakeDynamic_AreIdempotent()
    {
        var root = _world.CreateTransform(LocalTransform.FromPosition(new Vector3(3, 0, 0)));

        _world.MakeStatic(root);
        _world.MakeStatic(root);
        Assert.That(_world.IsStatic(root), Is.True);

        _world.MakeDynamic(root);
        _world.MakeDynamic(root);
        Assert.That(_world.IsStatic(root), Is.False);
    }

    /// <summary>A static entity is allowed under a static parent; that is the supported shape.</summary>
    [Test]
    public void StaticUnderStatic_IsAllowed()
    {
        var root = _world.CreateTransform(LocalTransform.FromPosition(new Vector3(1, 0, 0)));
        var child = _world.CreateTransform(LocalTransform.FromPosition(new Vector3(2, 0, 0)));
        _world.SetParent(child, root);

        _world.MakeStatic(root);
        Assert.DoesNotThrow(() => _world.MakeStatic(child));
        Assert.That(WorldPositionOf(child).X, Is.EqualTo(3f).Within(1e-5f));
    }

    [Test]
    public void MakeStatic_TagsTheWholeSubtree_AndMakeDynamicUntagsIt()
    {
        var root = _world.CreateTransform();
        var child = _world.CreateTransform();
        var grandchild = _world.CreateTransform();
        _world.SetParent(child, root);
        _world.SetParent(grandchild, child);

        _world.MakeStatic(root);
        Assert.That(new[] { root, child, grandchild }.Select(_world.IsStatic), Is.All.True);

        var moving = _world.CreateTransform();
        Assert.Throws<InvalidOperationException>(() => _world.SetParent(moving, grandchild),
            "a moving entity cannot hang anywhere inside a static subtree, not only under its root");

        _world.MakeDynamic(root);
        Assert.That(new[] { root, child, grandchild }.Select(_world.IsStatic), Is.All.False);
    }

    [Test]
    public void ReparentingAStaticSubtree_SettlesItsWorldTransformAgain()
    {
        var a = _world.CreateTransform(LocalTransform.FromPosition(new Vector3(10, 0, 0)));
        var b = _world.CreateTransform(LocalTransform.FromPosition(new Vector3(1, 0, 0)));
        var leaf = _world.CreateTransform(LocalTransform.FromPosition(new Vector3(0, 1, 0)));
        _world.SetParent(leaf, b);
        _world.MakeStatic(a);
        _world.MakeStatic(b);

        _world.SetParent(b, a);
        Assert.Multiple(() =>
        {
            Assert.That(WorldPositionOf(b).X, Is.EqualTo(11f).Within(1e-5f), "under its new parent");
            Assert.That(WorldPositionOf(leaf), Is.EqualTo(new Vector3(11, 1, 0)), "and so is its subtree");
        });

        _world.ClearParent(b);
        Assert.Multiple(() =>
        {
            Assert.That(WorldPositionOf(b).X, Is.EqualTo(1f).Within(1e-5f), "relative to the world again");
            Assert.That(WorldPositionOf(leaf), Is.EqualTo(new Vector3(1, 1, 0)));
        });
    }
}