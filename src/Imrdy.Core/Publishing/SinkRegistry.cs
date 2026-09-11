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
/// </summary>
public sealed class SinkRegistry : IDisposable
{
    private readonly PublisherStore _store;
    private readonly SinkContext _context;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    private readonly Dictionary<string, (PublisherEntry Definition, ISessionSink Sink)> _sinks =
        new(StringComparer.OrdinalIgnoreCase);

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
        var config = _store.Load();

        lock (_gate)
        {
            Reconcile(config);
            return _sinks.Values.Select(v => v.Sink).ToList();
        }
    }

    /// <summary>
    /// One <see cref="SinkHealth"/> per live link, for <c>imrdy links</c> and the connections
    /// window. A link whose endpoint is unusable has no sink and so reports nothing here —
    /// its reason was logged when the sink failed to build.
    /// </summary>
    public IReadOnlyList<SinkHealth> Health() =>
        Current().Select(sink => sink.Health).ToList();

    private void Reconcile(PublisherConfig config)
    {
        var wanted = new Dictionary<string, PublisherEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in config.Publishers)
        {
            wanted[entry.Name] = entry;
        }

        foreach (var name in _sinks.Keys.ToList())
        {
            // Gone, disabled, or redefined: the cached sink no longer describes the link.
            // Record value-equality is the whole test — a record that compares equal means
            // nothing about the link changed, so its connection and history are still valid.
            if (!wanted.TryGetValue(name, out var entry) || entry != _sinks[name].Definition)
            {
                Evict(name);
            }
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

    private void Evict(string name)
    {
        // A TCP sink owns a socket; dropping the reference without disposing would leak it.
        if (_sinks[name].Sink is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _sinks.Remove(name);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var name in _sinks.Keys.ToList())
            {
                Evict(name);
            }
        }
    }
}
