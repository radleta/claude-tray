namespace Imrdy.Core.Publishing;

/// <summary>Which way a session's state file moved.</summary>
public enum SessionChangeKind
{
    Changed,
    Removed,
}

/// <summary>One coalesced change, ready to emit.</summary>
public readonly record struct SessionChange(string SessionId, SessionChangeKind Kind);

/// <summary>
/// Coalesces filesystem events into at most one emit per session per drain.
/// <para>
/// A single hook write produces several watcher events, and the publisher must not turn
/// each into a wire frame. Only the last event for a session survives a drain, which is
/// also what makes delete-then-recreate correct: a file-sink publisher recreating a state
/// file the receiver just deleted arrives as Removed-then-Changed, and the Changed is the
/// truth. The reverse order resolves the same way for the same reason — whichever event
/// landed last describes the file as it now is.
/// </para>
/// </summary>
public sealed class SessionChangeQueue
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SessionChangeKind> _pending = new(StringComparer.Ordinal);

    /// <summary>Session ids awaiting a drain. Zero means the drain tick has nothing to do.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    public void Enqueue(string sessionId, SessionChangeKind kind)
    {
        lock (_gate)
        {
            _pending[sessionId] = kind;
        }
    }

    /// <summary>
    /// Takes everything pending and empties the queue. Events arriving during the emit that
    /// follows land in the next drain rather than being lost.
    /// </summary>
    public IReadOnlyList<SessionChange> Drain()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return [];
            }

            var batch = _pending.Select(kv => new SessionChange(kv.Key, kv.Value)).ToList();
            _pending.Clear();
            return batch;
        }
    }
}
