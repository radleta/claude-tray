using FluentAssertions;
using Imrdy.Core.Publishing;

namespace Imrdy.Core.Tests.Publishing;

/// <summary>
/// The join D26 and D27 turn on. Every case here is a way a link can exist in one input and
/// not the others — which is the normal state, not the edge case: a file-sink publisher never
/// connects, a disabled record never builds a sink, and a receiver holds no allow-list so a
/// publisher can arrive with no record at all.
/// </summary>
public class ConnectionsViewModelBuilderTests
{
    private static PublisherConfig Config(params PublisherEntry[] entries) =>
        new() { Publishers = [.. entries] };

    private static PublisherEntry Entry(
        string name,
        string endpoint = "10.0.0.5:47600",
        bool enabled = true,
        bool muted = false,
        int? desktop = null) =>
        new() { Name = name, Endpoint = endpoint, Enabled = enabled, Muted = muted, DesktopIndex = desktop };

    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static SinkHealth Health(string name, SinkState state, string? error = null, int sessions = 0) =>
        new(name, state, LastSuccessAt: null, LastError: error, SessionCount: sessions);

    private static ConnectionsViewModel Build(
        PublisherConfig config,
        IReadOnlyList<SinkHealth>? outbound = null,
        IReadOnlyList<SinkHealth>? inbound = null) =>
        ConnectionsViewModelBuilder.Build(
            config, outbound ?? [], inbound ?? [], "receiver-box", listenEnabled: true, listenPort: 47600,
            authKeyConfigured: true, Now);

    [Fact]
    public void Build_CarriesTheReceiversOwnIdentityAndListenState()
    {
        var vm = ConnectionsViewModelBuilder.Build(
            Config(), [], [], "receiver-box", listenEnabled: false, listenPort: 47610,
            authKeyConfigured: true, Now);

        vm.MachineName.Should().Be("receiver-box");
        vm.ListenEnabled.Should().BeFalse(
            "a receiver that is not listening explains every inbound link being absent, "
            + "and that is the first thing to check");
        vm.ListenPort.Should().Be(47610);
    }

    [Fact]
    public void Build_RegisteredMachineWithNoHealthAtAll_StillGetsARow()
    {
        var vm = Build(Config(Entry("laptop", enabled: false)));

        var row = vm.Rows.Should().ContainSingle().Subject;
        row.Name.Should().Be("laptop");
        row.IsRegistered.Should().BeTrue();
        row.Enabled.Should().BeFalse();
        row.Outbound.Should().BeNull("a disabled record builds no sink");
        row.Inbound.Should().BeNull();
        row.IsFailed.Should().BeFalse("absent is not failed — the operator disabled it on purpose");
    }

    [Fact]
    public void Build_FileSinkPublisher_ReportsFileSinkAndIsNeverFailed()
    {
        var vm = Build(
            Config(Entry("desk2-Ubuntu-22", endpoint: @"C:\Users\me\.imrdy\sessions")),
            outbound: [Health("desk2-Ubuntu-22", SinkState.FileSink)]);

        var row = vm.Rows.Should().ContainSingle().Subject;
        row.Outbound!.State.Should().Be(SinkState.FileSink,
            "D27: a file sink has no connection to be healthy, and reporting it as Connected "
            + "would be a lie the operator acts on");
        row.Inbound.Should().BeNull(
            "a file-sink publisher never opens a link, so it is absent from the inbound table "
            + "by construction — the row exists because publishers.json carries it");
        row.IsFailed.Should().BeFalse();
    }

    [Fact]
    public void Build_MergesBothDirectionsOntoOneRow()
    {
        var vm = Build(
            Config(Entry("desk2", desktop: 3, muted: true)),
            outbound: [Health("desk2", SinkState.Connected, sessions: 4)],
            inbound: [Health("desk2", SinkState.Failed, error: "disconnected")]);

        var row = vm.Rows.Should().ContainSingle().Subject;
        row.DesktopIndex.Should().Be(3);
        row.Muted.Should().BeTrue();
        row.Outbound!.State.Should().Be(SinkState.Connected);
        row.Outbound.SessionCount.Should().Be(4);
        row.Inbound!.State.Should().Be(SinkState.Failed);
        row.Inbound.LastError.Should().Be("disconnected");
        row.IsFailed.Should().BeTrue("one failed direction is enough to want the operator's attention");
    }

    [Fact]
    public void Build_UnregisteredInboundPublisher_GetsARowWithNoEndpointOrMapping()
    {
        var vm = Build(Config(), inbound: [Health("stranger", SinkState.Connected)]);

        var row = vm.Rows.Should().ContainSingle().Subject;
        row.Name.Should().Be("stranger");
        row.IsRegistered.Should().BeFalse(
            "a receiver holds no allow-list (D24), so an unrecorded publisher is normal — "
            + "and it is exactly what the window exists to surface");
        row.Endpoint.Should().BeNull();
        row.DesktopIndex.Should().BeNull("with no record there is no mapping to report");
        row.Inbound!.State.Should().Be(SinkState.Connected);
    }

    [Fact]
    public void Build_MatchesNamesCaseInsensitively()
    {
        var vm = Build(
            Config(Entry("Desk2")),
            outbound: [Health("desk2", SinkState.Connected)],
            inbound: [Health("DESK2", SinkState.Connected)]);

        vm.Rows.Should().ContainSingle("a machine name differing only in case is one machine, "
            + "not three — PublisherStore already matches names case-insensitively");
        vm.Rows[0].Outbound.Should().NotBeNull();
        vm.Rows[0].Inbound.Should().NotBeNull();
    }

    [Fact]
    public void Build_SortsRowsByName()
    {
        var vm = Build(Config(Entry("zed"), Entry("alpha")), inbound: [Health("mid", SinkState.Connected)]);

        vm.Rows.Select(r => r.Name).Should().Equal("alpha", "mid", "zed");
    }

    [Fact]
    public void Build_DuplicateNames_ProduceOneRowRatherThanThrowing()
    {
        // D24 accepts two publishers claiming one name rather than guarding it. The window
        // must still open.
        var vm = Build(
            Config(Entry("twin", endpoint: "a:1"), Entry("twin", endpoint: "b:2")),
            inbound: [Health("twin", SinkState.Connected), Health("twin", SinkState.Failed)]);

        var row = vm.Rows.Should().ContainSingle().Subject;
        row.Endpoint.Should().Be("a:1", "the first record wins, so the list order the operator sees is the file's");
        row.Inbound!.State.Should().Be(SinkState.Failed, "the last health report wins — it is the newer fact");
    }

    [Fact]
    public void Build_NoLinksAnywhere_ReturnsAnEmptyListNotNull()
    {
        var vm = Build(Config());

        vm.Rows.Should().BeEmpty();
    }
}
