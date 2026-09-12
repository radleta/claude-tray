using FluentAssertions;
using Imrdy.Core.Publishing;
using Imrdy.Core.State;

namespace Imrdy.Core.Tests.Publishing;

/// <summary>
/// The caller-level cover for <c>f-filesink-no-socket</c>: a file-sink publisher opens no
/// socket, so nothing it does ever reaches <c>WireListener.Health()</c> and a surface that
/// enumerates publishers from the listener alone reports it as absent while it is actively
/// delivering. The corpus already covered the TCP stranger — an inbound machine with no
/// record — but it covered it by handing <c>ConnectionsViewModelBuilder</c> the
/// <c>inbound</c> list directly, which cannot catch a caller that never reaches that
/// parameter. These tests therefore start at the <em>disk</em>: a beat file and an ingested
/// session file, nothing hand-fed.
/// </summary>
public class HeartbeatMachinesTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A machine name that exercises the lossy part of the token: a dot and mixed case.</summary>
    private const string Machine = "PC-Excalibur-Ubuntu-24.04";

    private readonly string _root;
    private readonly string _sessions;

    public HeartbeatMachinesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "imrdy-heartbeat-machines", Guid.NewGuid().ToString());
        _sessions = Path.Combine(_root, "sessions");
        Directory.CreateDirectory(_sessions);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private void Beat(string machine, DateTimeOffset at)
    {
        Directory.CreateDirectory(PublisherHeartbeat.DirectoryFor(_sessions));
        File.WriteAllText(PublisherHeartbeat.PathFor(_sessions, machine), PublisherHeartbeat.Format(at));
    }

    private void IngestedSession(string id, string originMachine) =>
        new StateFileReader().WriteStateFile(
            Path.Combine(_sessions, id + ".json"),
            new StateFileModel
            {
                SessionId = id,
                Status = "idle",
                Project = "demo",
                Cwd = "/home/r/demo",
                HookEvent = "Stop",
                OriginMachine = originMachine,
            });

    [Fact]
    public void Read_RecoversTheMachineNameFromAnIngestedSession_NotFromTheLossyToken()
    {
        Beat(Machine, Now.AddSeconds(-3));
        IngestedSession("s1", Machine);

        var beats = HeartbeatMachines.Read(_sessions, registeredNames: []);

        beats.Should().ContainSingle().Which.Name.Should().Be(
            Machine,
            "the beat filename flattens the dot and the case, so the only verbatim copy of the "
            + "publisher's own name on this machine is what it stamped on the sessions it sent");
    }

    [Fact]
    public void LinksReport_WithNoPublishersJsonAtAll_StillReportsADeliveringFileSinkPublisher()
    {
        // The exact shape the user's demo measured: a fresh beat on disk, ingested sessions
        // carrying origin_machine, and an empty publishers.json — which printed "No links
        // registered." while the publisher was delivering.
        Beat(Machine, Now.AddSeconds(-3));
        IngestedSession("s1", Machine);

        var vm = LinksReport.Build(
            new PublisherConfig(),
            new NetworkConfig(),
            "receiver-box",
            wslDistro: null,
            HeartbeatMachines.Read(_sessions, registeredNames: []),
            Now);

        var row = vm.Rows.Should().ContainSingle().Subject;
        row.Name.Should().Be(Machine);
        row.IsRegistered.Should().BeFalse("a receiver holds no allow-list (D24)");
        row.Endpoint.Should().BeNull();
        row.Inbound!.State.Should().Be(SinkState.FileSink, "there is no connection to call connected (D27)");
        row.LastDelivery.Should().Be("3s ago");
        row.IsFailed.Should().BeFalse();
    }

    [Fact]
    public void LinksReport_RegisteredMachineWithABeat_ProducesOneRowThatKeepsItsRecord()
    {
        Beat(Machine, Now.AddSeconds(-3));

        var registered = new PublisherConfig
        {
            Publishers =
            [
                new PublisherEntry
                {
                    Name = Machine,
                    Endpoint = @"C:\Users\r\.imrdy\sessions",
                    DesktopIndex = 3,
                    Muted = true,
                },
            ],
        };

        var vm = LinksReport.Build(
            registered,
            new NetworkConfig(),
            "receiver-box",
            wslDistro: null,
            HeartbeatMachines.Read(_sessions, registered.Publishers.Select(e => (string?)e.Name)),
            Now);

        var row = vm.Rows.Should().ContainSingle(
            "a machine known by both a record and a beat is one publisher, not two").Subject;
        row.IsRegistered.Should().BeTrue();
        row.DesktopIndex.Should().Be(3, "the record's own values must survive the beat");
        row.Muted.Should().BeTrue();
        row.Inbound!.State.Should().Be(SinkState.FileSink);
    }

    [Fact]
    public void Read_WithNoBeatsAtAll_ReportsNothing()
    {
        IngestedSession("s1", Machine);

        HeartbeatMachines.Read(_sessions, registeredNames: []).Should().BeEmpty(
            "absence is not disconnection, and a receiver no file sink has ever written to must "
            + "behave exactly as it did before the heartbeat existed");
    }

    [Fact]
    public void Resolve_FallsBackToTheToken_WhenNothingOnThisMachineNamesThePublisher()
    {
        // A publisher that beats but has not yet delivered a session and was never registered:
        // the token is the only name in existence here, and a near-miss row beats no row.
        Beat(Machine, Now.AddSeconds(-3));

        var beat = HeartbeatMachines.Read(_sessions, registeredNames: []).Should().ContainSingle().Subject;

        beat.Name.Should().Be(PublisherHeartbeat.TokenFor(Machine));
        beat.NameIsToken.Should().BeTrue(
            "a flattened name is fine to render and a silent trap to save: every behaviour keyed "
            + "on a PublisherEntry joins on its name against the publisher's own origin_machine, "
            + "so a record named 24_04 would never again match the machine sending 24.04");
    }

    [Fact]
    public void Read_RecoveredName_IsNotMarkedAsAToken()
    {
        Beat(Machine, Now.AddSeconds(-3));
        IngestedSession("s1", Machine);

        HeartbeatMachines.Read(_sessions, registeredNames: [])
            .Should().ContainSingle().Which.NameIsToken.Should().BeFalse();
    }

    [Fact]
    public void LinksReport_TokenNamedRow_CarriesTheFlagAndSaysSoOnTheRow()
    {
        // The first-run sequence: the daemon is up and beating before any session exists there.
        Beat(Machine, Now.AddSeconds(-3));

        var row = LinksReport.Build(
                new PublisherConfig(),
                new NetworkConfig(),
                "receiver-box",
                wslDistro: null,
                HeartbeatMachines.Read(_sessions, registeredNames: []),
                Now)
            .Rows.Should().ContainSingle().Subject;

        row.NameIsToken.Should().BeTrue("the window reads this to refuse seeding a record from it");
        ConnectionRowFormatter.LastError(row).Should().Be(
            ConnectionRowFormatter.NameDerived,
            "imrdy links has no edit path, so the row's own cell is the only place the CLI can warn");
    }

    [Fact]
    public void LinksReport_StaleAndTokenNamed_SaysBothRatherThanPickingOne()
    {
        Beat(Machine, Now - PublisherHeartbeat.StaleAfter - TimeSpan.FromMinutes(1));

        var row = LinksReport.Build(
                new PublisherConfig(),
                new NetworkConfig(),
                "receiver-box",
                wslDistro: null,
                HeartbeatMachines.Read(_sessions, registeredNames: []),
                Now)
            .Rows.Should().ContainSingle().Subject;

        var error = ConnectionRowFormatter.LastError(row);
        error.Should().Contain("no heartbeat for");
        error.Should().Contain(ConnectionRowFormatter.NameDerived);
    }

    [Fact]
    public void LinksReport_RegisteredMachineWithABeat_IsNeverMarkedTokenNamed()
    {
        // The record's name won, so the beat's own resolution is irrelevant — and the advisory
        // must not ride along on a name the operator typed themselves.
        Beat(Machine, Now.AddSeconds(-3));

        var registered = new PublisherConfig
        {
            Publishers = [new PublisherEntry { Name = Machine, Endpoint = null }],
        };

        var row = LinksReport.Build(
                registered,
                new NetworkConfig(),
                "receiver-box",
                wslDistro: null,
                HeartbeatMachines.Read(_sessions, registered.Publishers.Select(e => (string?)e.Name)),
                Now)
            .Rows.Should().ContainSingle().Subject;

        row.NameIsToken.Should().BeFalse();
        ConnectionRowFormatter.LastError(row).Should().BeEmpty();
    }
}
