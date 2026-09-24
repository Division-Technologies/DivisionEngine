namespace DivisionEngine;

/// <summary>
///     Infers dependencies from declared access. Issue order is the program order, so the result
///     of running the graph equals running the jobs sequentially in issue order.
///     Multi-granularity: a type-level entry remembers the last type-level writer (X), the
///     type-level readers since (S), and the entity-level writers (IX) and readers (IS) since. An
///     entity-level entry remembers the last writer and readers of one component of one entity, and
///     is invalidated whenever the type is written as a whole (epoch).
///     <list type="bullet">
///         <item>type read: after last type writer and all entity writers</item>
///         <item>type write: after everything on the type; clears the lists</item>
///         <item>entity read: after last type writer and the entity's last writer</item>
///         <item>entity write: after last type writer, type readers, the entity's last writer and readers</item>
///     </list>
///     Not thread-safe; the graph serializes issues with a lock.
/// </summary>
internal sealed class ResourceTracker
{
    private readonly Dictionary<(int entity, int type), EntityEntry> _entityEntries = new();
    private Entry[] _entries = new Entry[64];
    private Entry[] _namedEntries = new Entry[8];

    public void Issue(TaskNode node)
    {
        var access = node.Access;

        foreach (var resource in access.Reads)
        {
            ref var entry = ref GetEntry(resource);
            DependOn(node, entry.LastWriter);
            DependOnAll(node, entry.EntityWriters);
            (entry.Readers ??= new List<TaskNode>()).Add(node);
        }

        foreach (var resource in access.Writes)
        {
            ref var entry = ref GetEntry(resource);
            DependOn(node, entry.LastWriter);
            DependOnAll(node, entry.Readers);
            DependOnAll(node, entry.EntityWriters);
            DependOnAll(node, entry.EntityReaders);
            entry.Readers?.Clear();
            entry.EntityWriters?.Clear();
            entry.EntityReaders?.Clear();
            entry.LastWriter = node;
            entry.Epoch++;
        }

        foreach (var access1 in access.EntityReads)
        {
            ref var typeEntry = ref GetEntry(ResourceId.Component(access1.Type));
            DependOn(node, typeEntry.LastWriter);
            var entityEntry = GetEntityEntry(access1, typeEntry.Epoch);
            DependOn(node, entityEntry.LastWriter);
            entityEntry.Readers.Add(node);
            (typeEntry.EntityReaders ??= new List<TaskNode>()).Add(node);
        }

        foreach (var access1 in access.EntityWrites)
        {
            ref var typeEntry = ref GetEntry(ResourceId.Component(access1.Type));
            DependOn(node, typeEntry.LastWriter);
            DependOnAll(node, typeEntry.Readers);
            var entityEntry = GetEntityEntry(access1, typeEntry.Epoch);
            DependOn(node, entityEntry.LastWriter);
            DependOnAll(node, entityEntry.Readers);
            entityEntry.Readers.Clear();
            entityEntry.LastWriter = node;
            (typeEntry.EntityWriters ??= new List<TaskNode>()).Add(node);
        }
    }

    /// <summary>Forgets all nodes. Only valid once every issued node has completed.</summary>
    public void Clear()
    {
        ClearEntries(_entries);
        ClearEntries(_namedEntries);
        _entityEntries.Clear();
    }

    private static void DependOn(TaskNode node, TaskNode? predecessor)
    {
        if (predecessor is not null)
        {
            node.DependOn(predecessor);
        }
    }

    private static void DependOnAll(TaskNode node, List<TaskNode>? predecessors)
    {
        if (predecessors is null)
        {
            return;
        }

        foreach (var predecessor in predecessors)
        {
            node.DependOn(predecessor);
        }
    }

    private static void ClearEntries(Entry[] entries)
    {
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i].LastWriter = null;
            entries[i].Readers?.Clear();
            entries[i].EntityWriters?.Clear();
            entries[i].EntityReaders?.Clear();
        }
    }

    private EntityEntry GetEntityEntry(EntityComponentAccess access, int epoch)
    {
        var key = (access.Entity.Index, access.Type.Value);
        if (!_entityEntries.TryGetValue(key, out var entry))
        {
            entry = new EntityEntry { Epoch = epoch };
            _entityEntries.Add(key, entry);
        }
        else if (entry.Epoch != epoch)
        {
            // The type was written as a whole since: everything recorded here is already ordered
            // before that writer, which every new access depends on.
            entry.LastWriter = null;
            entry.Readers.Clear();
            entry.Epoch = epoch;
        }

        return entry;
    }

    private ref Entry GetEntry(ResourceId resource)
    {
        if (resource.Value < 0)
        {
            var index = -resource.Value - 1;
            if (index >= _namedEntries.Length)
            {
                Array.Resize(ref _namedEntries, Math.Max(_namedEntries.Length * 2, index + 1));
            }

            return ref _namedEntries[index];
        }

        if (resource.Value >= _entries.Length)
        {
            Array.Resize(ref _entries, Math.Max(_entries.Length * 2, resource.Value + 1));
        }

        return ref _entries[resource.Value];
    }

    private struct Entry
    {
        public TaskNode? LastWriter;
        public List<TaskNode>? Readers;
        public List<TaskNode>? EntityWriters;
        public List<TaskNode>? EntityReaders;
        public int Epoch;
    }

    private sealed class EntityEntry
    {
        public readonly List<TaskNode> Readers = new();
        public int Epoch;
        public TaskNode? LastWriter;
    }
}