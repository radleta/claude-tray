using Microsoft.Extensions.Logging;

namespace Imrdy.Core.Publishing;

/// <summary>
/// Holds the live set of sinks, one per registered link, and keeps it in step with
/// publishers.json.
/// <para>
/// Sinks are cached rather than rebuilt per emit for two reasons that point the same way.
/// <see cref="SinkHealth"/> is per-sink state — last success, last error, session count —
/// and a sink rebuilt on every emit reports a health snapshot that has never seen anything.
/// A TCP sink also owns a persistent connection, and rebuilding it per emit would redial
/// constantly. So a link is rebuilt only when its record actually changes, which record
/// value-equality answers exactly, and a link whose record is untouched keeps both its
/// connection and its history across a config reload (D25).
/// </para>
/// <para>
/// <b>Which call reconciles.</b> <see cref="Current"/> and <see cref="Reconcile()"/> read
/// publishers.json and mutate the live set, evicting and building sinks. <see cref="Health"/>
/// does neither — it projects whatever sinks exist right now and returns. That split is not a
/// micro-optimization: evicting a <see cref="TcpSink"/> disposes it, and that dispose blocks
/// for up to its two-second grace window waiting for the dial loop to park. The read-only
/// surfaces — the <c>links-live</c> IPC handler and the connections window's refresh tick —
/// both run on the tray's UI thread, so a reconcile from either stalls the message pump for
/// two seconds per evicted TCP link, taking the tray icons, the overlay and every menu with
/// it. A read must therefore never reconcile.
/// </para>
/// <para>
/// Liveness is preserved by triggering <see cref="Reconcile()"/> where records actually
/// change, rather than as a side effect of somebody reading health: the connections window's
/// save and remove paths call it off the UI thread, and the publish path calls
/// <see cref="Current"/> off-thread on the drain tick. A read-only surface therefore still
/// sees every sink those paths have built — it just does not build them itself. Note also
/// that both surfaces render an outer join over the publishers.json records themselves
/// (see <c>ConnectionsViewModelBuilder</c>), so a newly registered link appears in the list
/// the moment it is saved whether or not its sink exists yet. A record changed by hand in
/// publishers.json rather than through the window has no trigger and waits for the next
/// reconcile some other path makes; nothing watches that file.
/// </para>
/// <para>
/// <b>Why a reconcile is three phases.</b> Evict, then dispose, then build — in that order,
/// with the gate held only across the first and third. The ordering is load-bearing in both
/// directions and the two constraints pull against each other. Disposal cannot happen under
/// the gate, or a UI-thread <see cref="Health"/> call waits out the two-second grace window and
/// the freeze this split exists to prevent simply moves onto the lock. But disposal also cannot
/// happen after the build, because <see cref="TcpSink"/> dials from its constructor: eviction
/// keys on whole-record inequality, so an ordinary in-place edit — a desktop index, a mute —
/// replaces the sink, and a replacement that dials while the outgoing socket is still open
/// gives the receiver two connections from one machine. The receiver keys links by machine
/// name, so the departing connection's end-of-stream lands after the replacement's
/// <c>hello</c> and marks a live link <c>Failed</c> until the publisher's next frame. Doing
/// both means releasing the gate between the phases rather than choosing one.
/// </para>
/// </summary>
public sealed class SinkRegistry : IDisposable
{
    private readonly PublisherStore _store;
    private readonly SinkContext _context;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    // Serializes a whole reconcile, from reading publishers.json through to the last sink built.
    // _gate cannot do it: a reconcile deliberately releases the gate between its evict and build
    // phases, and two reconciles interleaving there would put a replacement sink's dial back
    // alongside the outgoing sink's dispose — the overlap the three-phase ordering exists to
    // remove. The config read is inside this gate for a second reason, given at ReconcileCore:
    // a snapshot read before the wait can otherwise be applied after a newer one.
    // Always taken before _gate, never while holding it.
    private readonly Lock _reconcileGate = new();

    private readonly Dictionary<string, (PublisherEntry Definition, ISessionSink Sink)> _sinks =
        new(StringComparer.OrdinalIgnoreCase);

    // Guarded by _gate. A reconcile can be in flight on a pool thread when the tray shuts down
    // (TrayApp queues one per record change), and without this the build phase would happily
    // dial a fresh set of TcpSinks after Dispose has already emptied the dictionary — sockets
    // and dial loops nobody will ever dispose. Reconciling is a no-op once disposed; it is not
    // an error, because the caller raced rather than misused the type.
    private bool _disposed;

    public SinkRegistry(PublisherStore store, SinkContext context, ILogger logger)
    {
        _store = store;
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// The sinks to emit through, reconciled against publishers.json on every call. This is
    /// what makes an added link appear on the next tick with no restart.
    /// </summary>
    public IReadOnlyList<ISessionSink> Current()
    {
        ReconcileCore();

        lock (_gate)
        {
            return _sinks.Values.Select(v => v.Sink).ToList();
        }
    }

    /// <summary>
    /// Brings the live set in step with publishers.json without returning anything, for the
    /// paths that changed a record and want its sink built or torn down now. Call it off the
    /// UI thread: evicting a <see cref="TcpSink"/> disposes it, and that blocks.
    /// </summary>
    public void Reconcile() => ReconcileCore();

    private void ReconcileCore()
    {
        lock (_reconcileGate)
        {
            // Inside the gate, not before it: the snapshot is part of the reconcile. Read
            // outside, a thread that waited here would apply a config older than the one the
            // reconcile it waited on just applied — and since eviction keys on whole-record
            // inequality, that means disposing the sink the newer pass built and rebuilding a
            // record the operator had removed, or restoring a re-pointed link's old endpoint.
            // Nothing corrects it afterwards: reading health no longer reconciles, and both
            // record-change triggers have already fired.
            //
            // That is not merely a stale dial. A rebuilt TcpSink dials from its constructor,
            // sends a hello carrying network.authKey, and then sends every locally-owned
            // session's full state through its connect snapshot — to an endpoint the operator
            // deliberately deregistered. Reading the file here is what makes the last reconcile
            // in the one that wins.
            var wanted = new Dictionary<string, PublisherEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _store.Load().Publishers)
            {
                wanted[entry.Name] = entry;
            }

            List<IDisposable> evicted;

            lock (_gate)
            {
                evicted = EvictStale(wanted);
            }

            // Between the phases, and outside the gate: an outgoing socket is closed before its
            // replacement dials, and no reader waits on the grace window. See the type remarks.
            DisposeAll(evicted);

            lock (_gate)
            {
                BuildMissing(wanted);
            }
        }
    }

    /// <summary>
    /// One <see cref="SinkHealth"/> per live link, for <c>imrdy links</c> and the connections
    /// window. A link whose endpoint is unusable has no sink and so reports nothing here —
    /// its reason was logged when the sink failed to build.
    /// <para>
    /// This does not reconcile, and must not: both callers are on the UI thread, where a
    /// reconcile can block for two seconds per evicted TCP link. See the type's remarks for
    /// what keeps the set live instead.
    /// </para>
    /// </summary>
    public IReadOnlyList<SinkHealth> Health()
    {
        lock (_gate)
        {
            return _sinks.Values.Select(v => v.Sink.Health).ToList();
        }
    }

    /// <summary>
    /// Disposes evicted sinks after the gate is released. Holding <see cref="_gate"/> across a
    /// <see cref="TcpSink"/> dispose would put the two-second grace window inside the lock, so
    /// a UI-thread <see cref="Health"/> call would wait on it and the freeze this split exists
    /// to prevent would simply move from the reconcile to the lock.
    /// </summary>
    private void DisposeAll(List<IDisposable> evicted)
    {
        foreach (var sink in evicted)
        {
            try
            {
                sink.Dispose();
            }
            catch (Exception ex)
            {
                // A sink that faults on the way out must not take the reconcile with it; the
                // rest of the evicted set still has sockets to release.
                _logger.LogWarning(ex, "Disposing an evicted sink failed");
            }
        }
    }

    private List<IDisposable> EvictStale(Dictionary<string, PublisherEntry> wanted)
    {
        var evicted = new List<IDisposable>();

        foreach (var name in _sinks.Keys.ToList())
        {
            // Gone, disabled, or redefined: the cached sink no longer describes the link.
            // Record value-equality is the whole test — a record that compares equal means
            // nothing about the link changed, so its connection and history are still valid.
            if (!wanted.TryGetValue(name, out var entry) || entry != _sinks[name].Definition)
            {
                Evict(name, evicted);
            }
        }

        return evicted;
    }

    /// <summary>
    /// Builds a sink for every wanted link that has none. Runs under <see cref="_gate"/>, which
    /// carries a standing constraint on the sinks themselves: <b>no sink constructor may do
    /// I/O.</b> Both are non-blocking today — <see cref="TcpSink"/> starts its dial loop with
    /// <c>Task.Run</c> and <see cref="FileSink"/> only assigns fields — and that is what lets
    /// this hold the gate at all. A constructor that probed its target path would put a
    /// multi-second stall inside the lock the moment an endpoint named an unreachable mount, and
    /// a UI-thread <see cref="Health"/> call would wait it out: the freeze this whole split
    /// exists to prevent, reintroduced through a side door.
    /// </summary>
    private void BuildMissing(Dictionary<string, PublisherEntry> wanted)
    {
        // Disposed between this reconcile's phases, or before it started. Anything evicted above
        // is still disposed by the caller; nothing new is dialed.
        if (_disposed)
        {
            return;
        }

        foreach (var (name, entry) in wanted)
        {
            if (_sinks.ContainsKey(name))
            {
                continue;
            }

            var sink = SinkFactory.TryCreate(entry, _context, _logger);
            if (sink is not null)
            {
                _sinks[name] = (entry, sink);
            }
        }
    }

    private void Evict(string name, List<IDisposable> evicted)
    {
        // A TCP sink owns a socket; dropping the reference without disposing would leak it.
        // The dispose itself is the caller's, once the gate is released — see DisposeAll.
        if (_sinks[name].Sink is IDisposable disposable)
        {
            evicted.Add(disposable);
        }

        _sinks.Remove(name);
    }

    public void Dispose()
    {
        List<IDisposable> evicted = [];

        lock (_gate)
        {
            _disposed = true;

            foreach (var name in _sinks.Keys.ToList())
            {
                Evict(name, evicted);
            }
        }

        DisposeAll(evicted);
    }
}
