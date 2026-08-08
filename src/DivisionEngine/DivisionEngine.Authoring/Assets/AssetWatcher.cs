namespace DivisionEngine.Authoring.Assets;

/// <summary>
///     Watches one or more asset root directories and forwards file changes, additions, and removals
///     (ignoring <c>.meta</c> sidecars) to the database's pending-change queue. The watcher only
///     enqueues — it never imports. Re-import happens when <see cref="AssetDatabase.Refresh" /> is
///     called explicitly.
/// </summary>
public sealed class AssetWatcher : IDisposable
{
    private readonly Action<string> _onChanged;
    private readonly Action<string> _onDeleted;
    private readonly List<FileSystemWatcher> _watchers = new();

    public AssetWatcher(IEnumerable<string> roots, Action<string> onChanged, Action<string> onDeleted)
    {
        _onChanged = onChanged;
        _onDeleted = onDeleted;

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnDeleted;
            watcher.Renamed += OnRenamed;
            _watchers.Add(watcher);
        }
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (IsMeta(e.FullPath))
        {
            return;
        }

        _onChanged(e.FullPath);
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        if (IsMeta(e.FullPath))
        {
            return;
        }

        _onDeleted(e.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (!IsMeta(e.OldFullPath))
        {
            _onDeleted(e.OldFullPath);
        }

        if (!IsMeta(e.FullPath))
        {
            _onChanged(e.FullPath);
        }
    }

    private static bool IsMeta(string path)
    {
        return path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
    }
}