using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DivisionEngine;

/// <summary>
///     Records structural changes for later playback on a <see cref="World" />. Commands are applied
///     in the order they were recorded, so playback order is deterministic by construction.
///     <see cref="CreateEntity" /> returns a placeholder (<see cref="Entity.IsDeferred" />) that can
///     be used in later commands of the same buffer; it is resolved to a real entity at playback.
///     Placeholders stored inside an unmanaged component's value are resolved too, for types that
///     declared their entity fields through <see cref="ComponentTypeRegistry.RegisterEntityFields{T}" />.
///     Managed component values are never rewritten.
/// </summary>
public sealed class EntityCommandBuffer
{
    private readonly List<Command> _commands = new();
    private readonly List<object> _objects = new();
    private int _deferredCount;
    private byte[] _payload = new byte[256];
    private int _payloadLength;

    /// <summary>
    ///     The resource jobs declare to record into (<c>Write</c>) or play back this buffer. Recording
    ///     jobs are serialized against each other and against playback by the scheduler.
    /// </summary>
    public ResourceId Resource { get; } = ResourceId.Unique("EntityCommandBuffer");

    public int Count => _commands.Count;

    public bool IsEmpty => _commands.Count == 0;

    public Entity CreateEntity()
    {
        var placeholder = new Entity(-1 - _deferredCount, 0);
        _deferredCount++;
        _commands.Add(new Command(CommandKind.CreateEntity, placeholder));
        return placeholder;
    }

    public void DestroyEntity(Entity entity)
    {
        _commands.Add(new Command(CommandKind.DestroyEntity, entity));
    }

    public void AddComponent<T>(Entity entity, in T value = default) where T : unmanaged
    {
        RecordData(CommandKind.AddComponent, entity, ComponentType<T>.Info, in value);
    }

    public void SetComponent<T>(Entity entity, in T value) where T : unmanaged
    {
        RecordData(CommandKind.SetComponent, entity, ComponentType<T>.Info, in value);
    }

    public void RemoveComponent<T>(Entity entity)
    {
        _commands.Add(new Command(CommandKind.RemoveComponent, entity) { Type = ComponentType<T>.Id });
    }

    public void AddManagedComponent<T>(Entity entity, T value) where T : class
    {
        RecordManaged(CommandKind.AddManagedComponent, entity, ComponentType<T>.Id, value);
    }

    public void SetManagedComponent<T>(Entity entity, T value) where T : class
    {
        RecordManaged(CommandKind.SetManagedComponent, entity, ComponentType<T>.Id, value);
    }

    /// <summary>Applies all recorded commands to <paramref name="world" /> in order, then clears the buffer.</summary>
    public void Playback(World world)
    {
        var resolved = _deferredCount == 0 ? [] : new Entity[_deferredCount];
        var map = new DeferredEntityMap(resolved);
        foreach (var command in _commands)
        {
            if (command.Kind == CommandKind.CreateEntity)
            {
                resolved[-1 - command.Entity.Index] = world.CreateEntity();
                continue;
            }

            var entity = map.Resolve(command.Entity);
            switch (command.Kind)
            {
                case CommandKind.DestroyEntity:
                    world.DestroyEntity(entity);
                    break;
                case CommandKind.AddComponent:
                    world.AddComponent(entity, command.Type, Remapped(command, map));
                    break;
                case CommandKind.SetComponent:
                    world.SetComponent(entity, command.Type, Remapped(command, map));
                    break;
                case CommandKind.RemoveComponent:
                    world.RemoveComponent(entity, command.Type);
                    break;
                case CommandKind.AddManagedComponent:
                    world.AddManagedComponent(entity, command.Type, _objects[command.ObjectIndex]);
                    break;
                case CommandKind.SetManagedComponent:
                    world.SetManagedComponent(entity, command.Type, _objects[command.ObjectIndex]);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown command {command.Kind}.");
            }
        }

        Clear();
    }

    public void Clear()
    {
        _commands.Clear();
        _objects.Clear();
        _payloadLength = 0;
        _deferredCount = 0;
    }

    private void RecordData<T>(CommandKind kind, Entity entity, ComponentTypeInfo info, in T value) where T : unmanaged
    {
        if (info.IsManaged)
        {
            throw new ArgumentException($"{info.Type} is a managed component; use the *ManagedComponent methods.");
        }

        var size = info.Size;
        if (_payloadLength + size > _payload.Length)
        {
            Array.Resize(ref _payload, Math.Max(_payload.Length * 2, _payloadLength + size));
        }

        if (size > 0)
        {
            var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in value, 1));
            bytes[..size].CopyTo(_payload.AsSpan(_payloadLength, size));
        }

        _commands.Add(new Command(kind, entity) { Type = info.Id, PayloadOffset = _payloadLength, PayloadLength = size });
        _payloadLength += size;
    }

    private void RecordManaged(CommandKind kind, Entity entity, ComponentTypeId type, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _commands.Add(new Command(kind, entity) { Type = type, ObjectIndex = _objects.Count });
        _objects.Add(value);
    }

    /// <summary>
    ///     The recorded component value with its entity fields resolved. The rewrite happens in the
    ///     payload buffer itself, which is valid because playback consumes each command once and
    ///     clears the buffer afterwards.
    /// </summary>
    private ReadOnlySpan<byte> Remapped(in Command command, DeferredEntityMap map)
    {
        var payload = _payload.AsSpan(command.PayloadOffset, command.PayloadLength);
        if (_deferredCount > 0 && payload.Length > 0)
        {
            ComponentTypeRegistry.RemapEntityFields(command.Type, payload, map);
        }

        return payload;
    }

    private enum CommandKind : byte
    {
        CreateEntity,
        DestroyEntity,
        AddComponent,
        SetComponent,
        RemoveComponent,
        AddManagedComponent,
        SetManagedComponent
    }

    private struct Command(CommandKind kind, Entity entity)
    {
        public readonly CommandKind Kind = kind;
        public readonly Entity Entity = entity;
        public ComponentTypeId Type;
        public int PayloadOffset;
        public int PayloadLength;
        public int ObjectIndex;
    }
}
