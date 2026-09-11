using FluentAssertions;
using Imrdy.Core.Publishing;

namespace Imrdy.Core.Tests.Publishing;

/// <summary>
/// <c>imrdy links</c> minus its presentation. The cases that matter are the ones where a
/// missing health record must not read as a fault: on the records-only path a CLI process
/// holds no sinks, so <em>every row</em> arrives with both health slots null, D27's file-sink
/// publisher is only ever diagnosable from its endpoint, and r-1's receive-only record has
/// nothing to dial by design. Live health (r-2) comes from the tray and is the only way a row
/// here can be failed — which is why <see cref="LinksReport.HealthSource"/> has to say which
/// of the two produced a run.
/// </summary>
public class LinksReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static PublisherEntry Entry(
        string name,
        string? endpoint = "10.0.0.5:47600",
        bool enabled = true,
        bool muted = false,
        int? desktop = null) =>
        new() { Name = name, Endpoint = endpoint, Enabled = enabled, Muted = muted, DesktopIndex = desktop };

    private static ConnectionsViewModel Build(params PublisherEntry[] entries) =>
        LinksReport.Build(
            new PublisherConfig { Publishers = [.. entries] },
            new NetworkConfig(),
            "receiver-box",
            wslDistro: null,
            Now);

    private static ConnectionRow Row(params PublisherEntry[] entries) => Build(entries).Rows[0];

    [Fact]
    public void Build_ResolvesTheMachineNameAndCarriesTheListenState()
    {
        var vm = LinksReport.Build(
            new PublisherConfig(),
            new NetworkConfig { ListenEnabled = true, ListenPort = 47610 },
            "receiver-box",
            wslDistro: "Ubuntu",
            Now);

        vm.MachineName.Should().Be("receiver-box-Ubuntu");
        vm.ListenEnabled.Should().BeTrue();
        vm.ListenPort.Should().Be(47610);
        vm.Rows.Should().BeEmpty();
    }

    [Fact]
    public void Build_LeavesBothHealthSlotsNull_BecauseACliProcessHoldsNoSinks()
    {
        var row = Row(Entry("desk2"));

        row.Outbound.Should().BeNull();
        row.Inbound.Should().BeNull();
        row.IsRegistered.Should().BeTrue();
    }

    [Fact]
    public void Outbound_ReportsAFileSinkEndpointAsFileSink_NeverAsConnectedOrAFault()
    {
        var row = Row(Entry("wsl-box", endpoint: @"C:\Users\me\.imrdy\sessions"));

        ConnectionRowFormatter.Outbound(row).Should().Be("FileSink");
    }

    [Fact]
    public void Outbound_ReportsATcpEndpointWithNoHealthAsNoSink()
    {
        ConnectionRowFormatter.Outbound(Row(Entry("desk2"))).Should().Be("no sink");
    }

    [Fact]
    public void Outbound_ReportsADisabledRecordAsDisabled_EvenWhenItsEndpointIsAFileSink()
    {
        var row = Row(Entry("wsl-box", endpoint: "/mnt/c/Users/me/.imrdy/sessions", enabled: false));

        ConnectionRowFormatter.Outbound(row).Should().Be("disabled");
    }

    [Fact]
    public void Inbound_ReportsAMachineThatHasNeverConnectedAsNever()
    {
        ConnectionRowFormatter.Inbound(Row(Entry("desk2"))).Should().Be("never");
    }

    [Fact]
    public void LastDelivery_ReportsTheNewerOfTheTwoDirections()
    {
        ConnectionRowFormatter.LastDelivery(
                new SinkHealth("desk2", SinkState.Connected, Now.AddMinutes(-30), null, 1),
                new SinkHealth("desk2", SinkState.Connected, Now.AddMinutes(-2), null, 1),
                Now)
            .Should().Contain("2m").And.EndWith("ago");
    }

    [Fact]
    public void Build_PrecomputesLastDelivery_SoNoSurfaceReadsTheClock()
    {
        // idea.md wants the connections render deterministic per fixture, which it cannot be
        // while the form asks the clock what "ago" means at paint time. Same contract as
        // WorkspaceDashboardViewModel.ActivityText.
        var health = new SinkHealth("desk2", SinkState.Connected, Now.AddMinutes(-5), null, 1);

        var vm = ConnectionsViewModelBuilder.Build(
            new PublisherConfig { Publishers = [Entry("desk2")] },
            outbound: [health],
            inbound: [],
            "receiver-box",
            listenEnabled: true,
            listenPort: 47600,
            authKeyConfigured: true,
            Now);

        vm.Rows[0].LastDelivery.Should().Be("5m ago");
    }

    [Fact]
    public void Build_ReportsALinkThatHasNeverDeliveredAsNever()
    {
        Row(Entry("desk2")).LastDelivery.Should().Be("never");
    }

    [Fact]
    public void RenderLines_SaysSoWhenListeningWithNoAuthKey()
    {
        // D10's key is what makes a misconfigured machine fail loudly instead of injecting
        // sessions into the wrong tray; listening without one accepts every peer the firewall
        // lets through, and that has to be visible rather than a silent default.
        var vm = new ConnectionsViewModel("me", true, 47600, AuthKeyConfigured: false, []);

        LinksReport.RenderLines(vm, live: false)[1].Should().Contain("NO AUTH KEY");
    }

    [Fact]
    public void RenderLines_SaysNothingExtraWhenAKeyIsConfigured()
    {
        var vm = new ConnectionsViewModel("me", true, 47600, AuthKeyConfigured: true, []);

        LinksReport.RenderLines(vm, live: false)[1].Should().Be("listening: yes (port 47600)");
    }

    [Fact]
    public void ExitCode_IsNonZeroWhenAnyLinkHasFailed()
    {
        var failed = new ConnectionRow(
            "desk2", "10.0.0.5:47600", IsRegistered: true, Enabled: true, Muted: false, DesktopIndex: null,
            Outbound: new SinkHealth("desk2", SinkState.Failed, null, "connection refused", 0),
            Inbound: null,
            LastDelivery: "never");

        LinksReport.ExitCode(new ConnectionsViewModel("me", true, 47600, AuthKeyConfigured: true, [failed]))
            .Should().Be(LinksReport.ExitFailedLink);
    }

    [Fact]
    public void ExitCode_IsZeroForAFileSinkRow_BecauseAFileSinkHasNoLinkToLose()
    {
        var fileSink = new ConnectionRow(
            "wsl-box", @"C:\sessions", IsRegistered: true, Enabled: true, Muted: false, DesktopIndex: null,
            Outbound: new SinkHealth("wsl-box", SinkState.FileSink, Now, null, 3),
            Inbound: null,
            LastDelivery: "0s ago");

        LinksReport.ExitCode(new ConnectionsViewModel("me", true, 47600, AuthKeyConfigured: true, [fileSink])).Should().Be(0);
    }

    [Fact]
    public void RenderLines_ColumnAlignsEveryRowUnderItsHeader()
    {
        var vm = Build(
            Entry("desk2", desktop: 3),
            Entry("a-very-long-machine-name", endpoint: "/mnt/c/sessions", muted: true));

        var lines = LinksReport.RenderLines(vm, live: false);
        var header = lines.Single(l => l.StartsWith("MACHINE", StringComparison.Ordinal));
        var body = lines.SkipWhile(l => l != header).Skip(1).ToList();

        body.Should().HaveCount(2);
        var endpointColumn = header.IndexOf("ENDPOINT", StringComparison.Ordinal);
        body.Should().AllSatisfy(line =>
            line[endpointColumn].Should().NotBe(' ', "every cell starts at its header's column"));
    }

    [Fact]
    public void RenderLines_SaysSoWhenNothingIsRegistered()
    {
        var lines = LinksReport.RenderLines(Build(), live: false);

        lines.Should().Contain("No links registered.");
        lines.Should().Contain(l => l.StartsWith("listening: no", StringComparison.Ordinal));
    }

    [Fact]
    public void Endpoint_TellsAReceiveOnlyRecordApartFromAnUnregisteredMachine()
    {
        // r-1: both have a null endpoint, and calling a registered link "(not registered)"
        // tells the operator the opposite of what is true.
        ConnectionRowFormatter.Endpoint(Row(Entry("wsl-box", endpoint: null)))
            .Should().Be(ConnectionRowFormatter.ReceiveOnly);

        var stranger = new ConnectionRow(
            "stranger", null, IsRegistered: false, Enabled: true, Muted: false, DesktopIndex: null,
            Outbound: null, Inbound: null, LastDelivery: "never");

        ConnectionRowFormatter.Endpoint(stranger).Should().Be("(not registered)");
    }

    [Fact]
    public void Outbound_ReportsAReceiveOnlyRecordAsReceiveOnly_NotAsNoSink()
    {
        // "no sink" reports a fault; there is nothing to dial here by the operator's choice —
        // the same argument D27 makes for a file sink.
        ConnectionRowFormatter.Outbound(Row(Entry("wsl-box", endpoint: null)))
            .Should().Be(ConnectionRowFormatter.ReceiveOnly);
    }

    [Fact]
    public void RenderLines_SaysWhichHealthSourceProducedTheRows()
    {
        // r-2: records-only and live look identical when every link happens to be fine, and
        // the exit code means different things in the two cases. The operator has to be able
        // to tell them apart from the output alone.
        var vm = Build(Entry("desk2"));

        LinksReport.RenderLines(vm, live: false)[2]
            .Should().Be(LinksReport.HealthSource(false))
            .And.Contain("records only");

        LinksReport.RenderLines(vm, live: true)[2]
            .Should().Be(LinksReport.HealthSource(true))
            .And.Contain("live");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankEndpoint_ReadsAsMalformedInBothCells_NotAsReceiveOnly(string blank)
    {
        // SinkFactory logs "has an empty endpoint and was skipped" for this record and builds
        // nothing; calling it receive-only on the surfaces would tell an operator who blanked
        // the field by hand that it is configured exactly as intended. The two cells also used
        // to disagree on whitespace — one tested Length > 0, the other IsNullOrWhiteSpace.
        var row = Row(Entry("wsl-box", endpoint: blank));

        ConnectionRowFormatter.Endpoint(row).Should().Be(ConnectionRowFormatter.Malformed);
        ConnectionRowFormatter.Outbound(row).Should().Be(ConnectionRowFormatter.Malformed);
    }

    [Fact]
    public void RecordsOnly_NamesTheRightCause_WhenATrayAnsweredAndRefused()
    {
        // The operator is told to read this line before trusting the exit code, so "no tray
        // answered" for a tray that answered with `unknown verb` — what an older tray beside a
        // newer CLI returns — sends them hunting a stopped process that is running.
        LinksReport.RecordsOnly(null).Should().Contain("no tray answered");

        LinksReport.RecordsOnly("unknown verb: links-live")
            .Should().Contain("the tray answered with an error")
            .And.Contain("unknown verb: links-live")
            .And.NotContain("no tray answered");
    }

    [Fact]
    public void RecordsOnlyUnresponsive_SaysNeitherAbsentNorErrored()
    {
        // A wedged tray is running and holding the pipe, so both other causes misdirect: "no
        // tray answered" sends the operator after a live process, and "answered with an error"
        // implies a reply that never came. The action this one calls for — restart the tray —
        // is not the action either other cause implies.
        var line = LinksReport.RecordsOnlyUnresponsive("5s");

        line.Should().Contain("accepted the connection")
            .And.Contain("5s")
            .And.NotContain("no tray answered")
            .And.NotContain("answered with an error");

        line.Should().Contain("always exits 0", "every records-only line carries the exit caveat");
    }
}
