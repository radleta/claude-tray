using FluentAssertions;
using Imrdy.Core.Publishing;
using Imrdy.Core.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Imrdy.Core.Tests.Publishing;

public class SinkRegistryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _mountA;
    private readonly string _mountB;
    private readonly PublisherStore _store;
    private readonly SinkRegistry _registry;

    public SinkRegistryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "imrdy-sinkreg-tests", Guid.NewGuid().ToString());
        _mountA = Path.Combine(_tempDir, "mount-a");
        _mountB = Path.Combine(_tempDir, "mount-b");
        Directory.CreateDirectory(_mountA);
        Directory.CreateDirectory(_mountB);

        _store = new PublisherStore(Path.Combine(_tempDir, "publishers.json"));
        _registry = new SinkRegistry(
            _store,
            new SinkContext(() => "workstation-Ubuntu", () => null, new StateFileReader(), () => []),
            NullLogger.Instance);
    }

    public void Dispose()
    {
        _registry.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void Current_NoPublishers_IsEmpty()
    {
        _registry.Current().Should().BeEmpty();
    }

    [Fact]
    public void Current_RootedEndpoint_BuildsAFileSink()
    {
        _store.Add("host", _mountA);

        _registry.Current().Should().ContainSingle()
            .Which.Health.State.Should().Be(SinkState.FileSink);
    }

    [Fact]
    public void Current_DisabledEntry_BuildsNoSink()
    {
        _store.Add("host", _mountA);
        _store.SetEnabled("host", false);

        _registry.Current().Should().BeEmpty();
    }

    [Fact]
    public void Current_UnusableEndpoint_IsSkippedNotThrown()
    {
        // Neither host:port nor a rooted path: the link is skipped with a logged reason rather
        // than taking the daemon down.
        _store.Add("desk2", "desktop2");

        _registry.Current().Should().BeEmpty();
    }

    [Fact]
    public void Current_HostPortEndpoint_BuildsATcpSink()
    {
        // Port 1 on loopback refuses immediately, so the sink dials and fails without waiting.
        _store.Add("desk2", "127.0.0.1:1");

        _registry.Current().Should().ContainSingle().Which.Should().BeOfType<TcpSink>();
    }

    [Fact]
    public void Current_AddedPublisher_AppearsWithNoRestart()
    {
        _registry.Current().Should().BeEmpty();

        _store.Add("host", _mountA);

        _registry.Current().Should().ContainSingle();
    }

    [Fact]
    public void Current_RemovedPublisher_Disappears()
    {
        _store.Add("host", _mountA);
        _registry.Current().Should().ContainSingle();

        _store.Remove("host");

        _registry.Current().Should().BeEmpty();
    }

    [Fact]
    public async Task Current_UnchangedEntry_ReturnsTheSameInstanceAndKeepsItsHealth()
    {
        // A sink rebuilt per call reports a health snapshot that has never seen anything,
        // and a TCP sink would redial on every emit.
        _store.Add("host", _mountA);
        var first = _registry.Current().Single();
        await first.PublishAsync(Model(), CancellationToken.None);

        var second = _registry.Current().Single();

        second.Should().BeSameAs(first);
        second.Health.SessionCount.Should().Be(1);
        second.Health.LastSuccessAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Current_ChangedEndpoint_RebuildsTheSinkAndResetsItsHealth()
    {
        _store.Add("host", _mountA);
        var first = _registry.Current().Single();
        await first.PublishAsync(Model(), CancellationToken.None);

        _store.Add("host", _mountB);

        var second = _registry.Current().Single();
        second.Should().NotBeSameAs(first);
        second.Health.SessionCount.Should().Be(0);
    }

    [Fact]
    public async Task Current_UnrelatedPublisherChanged_LeavesTheOthersAlone()
    {
        _store.Add("host-a", _mountA);
        _store.Add("host-b", _mountB);
        var a = _registry.Current().Single(s => s.Health.Name == "host-a");
        await a.PublishAsync(Model(), CancellationToken.None);

        _store.SetMuted("host-b", true);

        _registry.Current().Single(s => s.Health.Name == "host-a").Should().BeSameAs(a);
    }

    [Fact]
    public void Health_NamesTheLink_NotThisPublisher()
    {
        // imrdy links must be able to say WHICH link is unhealthy. Every sink on this
        // publisher shares one origin machine name, so that name cannot be the identifier.
        _store.Add("host-a", _mountA);
        _store.Add("host-b", _mountB);
        _registry.Reconcile();

        _registry.Health().Select(h => h.Name).Should().BeEquivalentTo(["host-a", "host-b"]);
    }

    [Fact]
    public void Health_ReturnsOneRecordPerLiveLink()
    {
        _store.Add("host-a", _mountA);
        _store.Add("host-b", _mountB);
        _registry.Reconcile();

        _registry.Health().Should().HaveCount(2);
        _registry.Health().Should().AllSatisfy(h => h.State.Should().Be(SinkState.FileSink));
    }

    [Fact]
    public void Health_DoesNotReconcile_BecauseItsCallersAreOnTheUiThread()
    {
        // Health() is read by the links-live IPC handler and by the connections window's
        // refresh tick, both on the tray's UI thread. Reconciling there would dispose evicted
        // TCP sinks inline, and each of those blocks for up to its two-second grace window —
        // freezing the message pump, and with it the tray icons, the overlay and every menu.
        // So a record that has never been reconciled is invisible to Health() by design.
        _store.Add("host-a", _mountA);

        _registry.Health().Should().BeEmpty();

        // ...and the mutating calls are what make it visible. Current() is the publish path's;
        // Reconcile() is the one a record change triggers.
        _registry.Reconcile();
        _registry.Health().Should().ContainSingle();
    }

    [Fact]
    public void Reconcile_AfterDispose_BuildsNothing()
    {
        // A reconcile is queued onto a pool thread per record change, so one can still be in
        // flight when the tray shuts down. Building there would dial a fresh set of sinks after
        // Dispose emptied the dictionary — sockets and dial loops with no owner left to close
        // them.
        _store.Add("host-a", _mountA);
        _registry.Dispose();

        _registry.Reconcile();

        _registry.Health().Should().BeEmpty();
    }

    [Fact]
    public void Reconcile_DropsASinkWhoseRecordIsGone()
    {
        _store.Add("host-a", _mountA);
        _registry.Reconcile();
        _registry.Health().Should().ContainSingle();

        _store.Remove("host-a");
        _registry.Reconcile();

        _registry.Health().Should().BeEmpty();
    }

    [Fact]
    public void Current_MatchesPublisherNamesCaseInsensitively()
    {
        _store.Add("Host", _mountA);
        _registry.Current().Should().ContainSingle();

        _store.Remove("HOST");

        _registry.Current().Should().BeEmpty();
    }

    private static StateFileModel Model() => new()
    {
        SessionId = "s1",
        Status = "busy",
        Project = "imrdy",
        Cwd = "/home/user/imrdy",
        HookEvent = "Stop",
    };
}
