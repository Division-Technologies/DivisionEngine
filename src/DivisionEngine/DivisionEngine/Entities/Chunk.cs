using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DivisionEngine;

/// <summary>
///     A fixed-size block of native memory holding the entities of one <see cref="Archetype" /> in
///     SoA layout: the entity array first, then one contiguous array per unmanaged component type at
///     the offsets computed by the archetype. Managed components live in per-type reference arrays
///     alongside the native block.
/// </summary>
internal sealed unsafe class Chunk
{
    public const int SizeInBytes = 16 * 1024;
    public const int MemoryAlignment = 64;

    private byte* _data;

    public Chunk(Archetype archetype)
    {
        Archetype = archetype;
        _data = (byte*)NativeMemory.AlignedAlloc(SizeInBytes, MemoryAlignment);
        NativeMemory.Clear(_data, SizeInBytes);

        var managedCount = archetype.ManagedTypeCount;
        Managed = managedCount == 0 ? [] : new object?[managedCount][];
        for (var slot = 0; slot < archetype.Types.Length; slot++)
        {
            var managedIndex = archetype.ManagedIndex[slot];
            if (managedIndex >= 0)
            {
                // A T[] (T : class) is covariantly an object?[]; writes are type-checked by the runtime.
                Managed[managedIndex] = (object?[])Array.CreateInstance(archetype.Infos[slot].Type, archetype.Capacity);
            }
        }
    }

    public Archetype Archetype { get; }

    /// <summary>Per managed type (indexed by <see cref="Archetype.ManagedIndex" />), the reference array.</summary>
    public object?[][] Managed { get; }

    public int Count { get; set; }

    public bool IsFull => Count == Archetype.Capacity;

    public Span<Entity> Entities => new(_data, Count);

    private Span<Entity> EntitySlots => new(_data, Archetype.Capacity);

    public void SetEntity(int index, Entity entity)
    {
        EntitySlots[index] = entity;
    }

    public Span<T> GetSpan<T>(int slot) where T : unmanaged
    {
        return new Span<T>(_data + Archetype.Offsets[slot], Count);
    }

    public byte* GetPointer(int slot, int index)
    {
        return _data + Archetype.Offsets[slot] + index * Archetype.Infos[slot].Size;
    }

    public ref T GetRef<T>(int slot, int index) where T : unmanaged
    {
        return ref Unsafe.AsRef<T>(GetPointer(slot, index));
    }

    public object?[] GetManagedArray(int slot)
    {
        return Managed[Archetype.ManagedIndex[slot]];
    }

    /// <summary>Copies one entity's data (entity id, components, managed references) within this chunk.</summary>
    public void CopyWithin(int from, int to)
    {
        if (from == to)
        {
            return;
        }

        EntitySlots[to] = EntitySlots[from];
        var infos = Archetype.Infos;
        var offsets = Archetype.Offsets;
        for (var slot = 0; slot < infos.Length; slot++)
        {
            var size = infos[slot].Size;
            if (size > 0)
            {
                var basePtr = _data + offsets[slot];
                NativeMemory.Copy(basePtr + from * size, basePtr + to * size, (nuint)size);
            }
        }

        foreach (var array in Managed)
        {
            array[to] = array[from];
        }
    }

    /// <summary>
    ///     Copies an entity from another chunk (possibly of a different archetype). Components not
    ///     present in the source are zeroed / nulled in the destination.
    /// </summary>
    public static void CopyAcross(Chunk src, int srcIndex, Chunk dst, int dstIndex)
    {
        dst.EntitySlots[dstIndex] = src.EntitySlots[srcIndex];
        var dstArchetype = dst.Archetype;
        var srcArchetype = src.Archetype;
        var infos = dstArchetype.Infos;
        for (var dstSlot = 0; dstSlot < infos.Length; dstSlot++)
        {
            var info = infos[dstSlot];
            var srcSlot = srcArchetype.SlotOf(info.Id);
            if (info.Size > 0)
            {
                var dstPtr = dst.GetPointer(dstSlot, dstIndex);
                if (srcSlot >= 0)
                {
                    NativeMemory.Copy(src.GetPointer(srcSlot, srcIndex), dstPtr, (nuint)info.Size);
                }
                else
                {
                    NativeMemory.Clear(dstPtr, (nuint)info.Size);
                }
            }
            else if (info.IsManaged)
            {
                dst.GetManagedArray(dstSlot)[dstIndex] = srcSlot >= 0 ? src.GetManagedArray(srcSlot)[srcIndex] : null;
            }
        }
    }

    /// <summary>Drops managed references held at <paramref name="index" /> so vacated slots do not keep objects alive.</summary>
    public void ClearManaged(int index)
    {
        foreach (var array in Managed)
        {
            array[index] = null;
        }
    }

    /// <summary>Zeroes every component of the entity at <paramref name="index" /> (slots may hold stale data from a previous occupant).</summary>
    public void ClearData(int index)
    {
        var infos = Archetype.Infos;
        for (var slot = 0; slot < infos.Length; slot++)
        {
            var size = infos[slot].Size;
            if (size > 0)
            {
                NativeMemory.Clear(GetPointer(slot, index), (nuint)size);
            }
        }

        ClearManaged(index);
    }

    public void Free()
    {
        if (_data != null)
        {
            NativeMemory.AlignedFree(_data);
            _data = null;
        }
    }
}
