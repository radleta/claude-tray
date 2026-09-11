namespace Imrdy.Core.Publishing;

/// <summary>
/// Feeds a <see cref="SessionChangeQueue"/> from filesystem events on the local sessions
/// directory. Deliberately thin: it holds no policy, so everything worth asserting about
/// coalescing lives in the queue where a test can reach it without a real watcher.
/// </summary>
public sealed class SessionDirectoryWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly SessionChangeQueue _queue;

    public SessionDirectoryWatcher(string sessionsDir, SessionChangeQueue queue)
    {
        _queue = queue;

        if (!Directory.Exists(sessionsDir))
        {
            // The hook creates this on the first event of the first session. A watcher
            // constructed against a missing directory throws, so create it rather than
            // making daemon startup depend on a session already having run.
            Directory.CreateDirectory(sessionsDir);
        }

        _watcher = new FileSystemWatcher(sessionsDir, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };

        _watcher.Created += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Deleted += OnDeleted;
        _watcher.Renamed += OnRenamed;
    }

    /// <summary>Begins delivering events into the queue.</summary>
    public void Start() => _watcher.EnableRaisingEvents = true;

    /// <summary>
    /// Strips the directory and the <c>.json</c> extension. Returns null for a path with no
    /// usable name, which a watcher can deliver for a transient temp file.
    /// </summary>
    internal static string? SessionIdFromPath(string path)
    {
        var id = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrEmpty(id) ? null : id;
    }

    private void OnChanged(object sender, FileSystemEventArgs e) =>
        Enqueue(e.FullPath, SessionChangeKind.Changed);

    private void OnDeleted(object sender, FileSystemEventArgs e) =>
        Enqueue(e.FullPath, SessionChangeKind.Removed);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // A rename moves a session id: the old name is gone and the new one appeared.
        // The order here is load-bearing. An atomic writer renames `<id>.tmp` onto
        // `<id>.json`, so both halves carry the SAME session id, and the queue keeps
        // whichever was enqueued last — Changed. Enqueuing these the other way round would
        // turn every atomic write into a removal.
        Enqueue(e.OldFullPath, SessionChangeKind.Removed);
        Enqueue(e.FullPath, SessionChangeKind.Changed);
    }

    private void Enqueue(string path, SessionChangeKind kind)
    {
        var sessionId = SessionIdFromPath(path);
        if (sessionId is not null)
        {
            _queue.Enqueue(sessionId, kind);
        }
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
    }
}
