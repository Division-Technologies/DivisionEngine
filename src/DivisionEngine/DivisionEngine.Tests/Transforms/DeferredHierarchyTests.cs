using System.Numerics;
using System.Runtime.CompilerServices;

namespace DivisionEngine.Tests.Transforms;

/// <summary>
///     Placeholders recorded into an <see cref="EntityCommandBuffer" /> are resolved inside component
///     values as well as in the commands themselves, which is what lets a whole subtree be built from
///     a single buffer.
/// </summary>
[TestFixture]
public sealed class DeferredHierarchyTests
{
    [Test]
    public void Playback_ResolvesPlaceholdersStoredInsideComponentValues()
    {
        using var world = new World();
        var buffer = new EntityCommandBuffer();

        var parent = buffer.CreateEntity();
        var child = buffer.CreateEntity();
        buffer.AddComponent(child, new Parent { Value = parent });
        buffer.AddComponent(parent, new Child { First = child, Last = child });
        buffer.AddComponent<Sibling>(child);

        buffer.Playback(world);

        var realParent = world.Query().With<Child>().Build();
        var realChild = world.Query().With<Parent>().Build();
        var parentEntity = Single(realParent);
        var childEntity = Single(realChild);

        Assert.Multiple(() =>
        {
            Assert.That(world.GetParent(childEntity), Is.EqualTo(parentEntity), "the Parent field was remapped");
            Assert.That(world.GetComponentReadOnly<Child>(parentEntity).First, Is.EqualTo(childEntity));
            Assert.That(world.GetComponentReadOnly<Child>(parentEntity).Last, Is.EqualTo(childEntity));
        });
    }

    [Test]
    public void Playback_LeavesRealEntityHandlesAlone()
    {
        using var world = new World();
        var existing = world.CreateEntity();
        var buffer = new EntityCommandBuffer();

        var child = buffer.CreateEntity();
        buffer.AddComponent(child, new Parent { Value = existing });

        buffer.Playback(world);

        var childEntity = Single(world.Query().With<Parent>().Build());
        Assert.That(world.GetParent(childEntity), Is.EqualTo(existing));
    }

    [Test]
    public void Playback_RejectsAPlaceholderFromAnotherBuffer()
    {
        using var world = new World();

        var other = new EntityCommandBuffer();
        other.CreateEntity();
        other.CreateEntity();
        var foreign = other.CreateEntity();

        var buffer = new EntityCommandBuffer();
        var entity = buffer.CreateEntity();
        buffer.AddComponent(entity, new Parent { Value = foreign });

        Assert.That(() => buffer.Playback(world), Throws.InvalidOperationException);
    }

    [Test]
    public void TypesWithoutARegistration_AreLeftUntouched()
    {
        using var world = new World();
        var buffer = new EntityCommandBuffer();

        var created = buffer.CreateEntity();
        var holder = buffer.CreateEntity();
        buffer.AddComponent(holder, new UnregisteredReference { Target = created });

        buffer.Playback(world);

        var entity = Single(world.Query().With<UnregisteredReference>().Build());
        Assert.That(world.GetComponentReadOnly<UnregisteredReference>(entity).Target.IsDeferred, Is.True,
            "without a registration the placeholder survives into the world as a dangling handle");
    }

    [Test]
    public void RegisterEntityFields_MakesAUserComponentRemappable()
    {
        RegisteredReferenceRegistration.Ensure();
        using var world = new World();
        var buffer = new EntityCommandBuffer();

        var created = buffer.CreateEntity();
        var holder = buffer.CreateEntity();
        buffer.AddComponent(holder, new RegisteredReference { Target = created, Offset = new Vector3(1, 2, 3) });

        buffer.Playback(world);

        var entity = Single(world.Query().With<RegisteredReference>().Build());
        var value = world.GetComponentReadOnly<RegisteredReference>(entity);

        Assert.Multiple(() =>
        {
            Assert.That(value.Target.IsDeferred, Is.False);
            Assert.That(world.IsAlive(value.Target), Is.True);
            Assert.That(value.Offset, Is.EqualTo(new Vector3(1, 2, 3)), "the other fields are untouched");
        });
    }

    private static Entity Single(EntityQuery query)
    {
        Entity? found = null;
        foreach (var chunk in query)
        {
            foreach (var entity in chunk.Entities)
            {
                Assert.That(found, Is.Null, "expected exactly one matching entity");
                found = entity;
            }
        }

        Assert.That(found, Is.Not.Null, "expected exactly one matching entity");
        return found!.Value;
    }

    public struct UnregisteredReference
    {
        public Entity Target;
    }

    public struct RegisteredReference
    {
        public Entity Target;
        public Vector3 Offset;
    }

    private static class RegisteredReferenceRegistration
    {
        private static int _registered;

        /// <summary>
        ///     Stands in for the <c>[ModuleInitializer]</c> a generator emits for user components; done
        ///     once because registrations are process-wide.
        /// </summary>
        public static void Ensure()
        {
            if (Interlocked.Exchange(ref _registered, 1) == 0)
            {
                ComponentTypeRegistry.RegisterEntityFields<RegisteredReference>(
                    static (ref RegisteredReference value, DeferredEntityMap map) => value.Target = map.Resolve(value.Target));
            }
        }
    }
}
