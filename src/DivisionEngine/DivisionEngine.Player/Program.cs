using System.Numerics;
using DivisionEngine;
using Microsoft.Extensions.Logging.Abstractions;

// Sample: a parent spinning about Y with three children held at a fixed local offset. The children
// never move in their own space; the orbit you see is TransformPropagation composing the parent's
// rotation into their world transforms during PhaseId.TransformPropagation.
// There is no window yet, so the frame loop is driven headless and the result is printed.

const int frames = 24;
const double frameSeconds = 1.0 / 12.0;
const int children = 3;

using var engine = new Engine(NullLogger.Instance);
var world = engine.World;

var pivot = world.CreateTransform(LocalTransform.FromPosition(new Vector3(0, 1, 0)));
var arms = new Entity[children];
for (var i = 0; i < children; i++)
{
    var angle = MathF.Tau * i / children;
    var offset = new Vector3(MathF.Cos(angle) * 2, 0, MathF.Sin(angle) * 2);
    arms[i] = world.CreateTransform(LocalTransform.FromPosition(offset));
    world.SetParent(arms[i], pivot);
}

engine.AddSystem(PhaseId.Update, new SpinSystem(pivot, MathF.Tau / 4));

Console.WriteLine($"Spinning {children} children around a pivot for {frames} frames.");
Console.WriteLine($"{"frame",5} {"t",6}  {"child 0 (world)",-26} {"child 1 (world)",-26} child 2 (world)");

for (var frame = 0; frame < frames; frame++)
{
    var seconds = frame * frameSeconds;
    engine.RunFrame(Realtime.FromSeconds(seconds));

    if (frame % 4 != 0)
    {
        continue;
    }

    Console.Write($"{frame,5} {seconds,6:0.00}  ");
    foreach (var arm in arms)
    {
        Console.Write($"{Format(world.GetComponentReadOnly<WorldTransform>(arm).Position),-26} ");
    }

    Console.WriteLine();
}

Console.WriteLine();
Console.WriteLine("The children's local transforms never changed:");
foreach (var arm in arms)
{
    Console.WriteLine($"  local {Format(world.GetComponentReadOnly<LocalTransform>(arm).Position)}");
}

// Destroying the pivot takes its whole subtree with it (HierarchyIntegrity).
world.DestroyEntity(pivot);
Console.WriteLine($"After destroying the pivot, {world.EntityCount} entities remain.");

return;

static string Format(Vector3 v)
{
    return $"({v.X,6:0.00},{v.Y,6:0.00},{v.Z,6:0.00})";
}

/// <summary>Turns one entity about the Y axis at a constant rate, in the Update phase.</summary>
internal sealed class SpinSystem(Entity entity, float radiansPerSecond) : IJobSystem
{
    public void Schedule(in JobSchedulingContext context)
    {
        var step = Quaternion.CreateFromAxisAngle(Vector3.UnitY, radiansPerSecond * (float)context.Time.Delta);
        var target = entity;
        context.Graph.Schedule("Spin", Access.Write<LocalTransform>(), job =>
        {
            ref var local = ref job.World.GetComponent<LocalTransform>(target);
            local.Rotation = Quaternion.Normalize(local.Rotation * step);
        });
    }
}
