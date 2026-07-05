using System.Numerics;
using Microsoft.Extensions.Logging;

namespace DivisionEngine;

public sealed class Engine(ILogger logger)
{
    private readonly ILogger _logger = logger;
    private readonly RootSystemGroup _rootSystemGroup = new();

    /// <summary>
    ///     Adds a system to the root system group so it executes once per frame. The authoring
    ///     environment uses this to register the asset-refresh system, for example.
    /// </summary>
    public void AddSystem(ISystem system)
    {
        _rootSystemGroup.Add(system);
    }

    public void Main(CancellationToken ct = default)
    {
        var sc = new DivisionSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(sc);
        var time = new Time { Current = 0, Delta = 0 };

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            sc.Update();
            var frameContext = new FrameContext(ref time, Realtime.Current, this);
            _rootSystemGroup.Execute(ref frameContext);
        }
    }
}

public interface IComponent : ISerializableObject
{
    bool Enabled { get; set; }
}

public abstract class Component : IComponent
{
    public Actor Actor { get; internal set; }
    public bool Enabled { get; set; }

    public SerializationScope Scope { get; set; }
    public LocalId Id { get; set; }
}

[AutoSerialization]
public sealed partial class Actor : ISerializableObject
{
    private readonly List<Actor> Children = new();

    [Serialize] private List<Component> _components = new();
    [Serialize] public Vector3 LocalPosition;
    [Serialize] public Quaternion LocalRotation;
    [Serialize] public Vector3 LocalScale;
    [field: Serialize] public string Name { get; set; } = "";
    [field: Serialize] public Actor? Parent { get; set; }

    public Matrix4x4 LocalTransform => Matrix4x4.CreateScale(LocalScale) *
                                       Matrix4x4.CreateFromQuaternion(LocalRotation) *
                                       Matrix4x4.CreateTranslation(LocalPosition);

    public SerializationScope Scope { get; set; }
    public LocalId Id { get; set; }
}

/*
[AutoSerialization]
public sealed partial class Scene : ISerializableObject
{
    private readonly List<Hierarchy> _hierarchies = new();
    public SerializationScope Scope { get; set; }
    public LocalId Id { get; set; }
}

[AutoSerialization]
internal sealed partial class Hierarchy : ISerializableObject
{
    public Actor Root;
    public SerializationScope Scope { get; set; }
    public LocalId Id { get; set; }
}

public sealed class World : IDisposable
{
    #region statics

    private static readonly List<World> _worlds = new();

    #endregion

    private readonly List<Scene> _scenes = new();
    private int _index;

    internal World()
    {
        _index = _worlds.Count;
        _worlds.Add(this);
    }

    public void Dispose()
    {
    }
}*/