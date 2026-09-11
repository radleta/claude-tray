using FluentAssertions;
using Imrdy.Core.Publishing;
using Imrdy.Core.State;

namespace Imrdy.Core.Tests.Publishing;

public class RemoteSessionMergeTests
{
    private static StateFileModel Model(
        string status = "busy",
        string? soundPack = null,
        string? iconStyle = null,
        int? desktopIndex = null,
        string? originMachine = null) => new()
        {
            SessionId = "s1",
            Status = status,
            Project = "imrdy",
            Cwd = "/home/user/imrdy",
            HookEvent = "Stop",
            SoundPack = soundPack,
            IconStyle = iconStyle,
            DesktopIndex = desktopIndex,
            OriginMachine = originMachine,
        };

    [Fact]
    public void Merge_FirstSight_StampsOriginAndTakesPayload()
    {
        var merged = RemoteSessionMerge.Merge(Model(status: "idle"), existing: null, "workstation-Ubuntu");

        merged.OriginMachine.Should().Be("workstation-Ubuntu");
        merged.Status.Should().Be("idle");
    }

    [Fact]
    public void Merge_ReceiverOverrides_SurviveAPublisherWrite()
    {
        var existing = Model(soundPack: "retro", iconStyle: "hexagons");

        var merged = RemoteSessionMerge.Merge(Model(status: "idle"), existing, "workstation");

        merged.SoundPack.Should().Be("retro");
        merged.IconStyle.Should().Be("hexagons");
    }

    [Fact]
    public void Merge_PublisherOverrides_NeverWinOverReceiverOnes()
    {
        // The publisher has its own per-session sound pack; it is not the receiver's business.
        var existing = Model(soundPack: "retro");
        var incoming = Model(soundPack: "assistant", iconStyle: "plus");

        var merged = RemoteSessionMerge.Merge(incoming, existing, "workstation");

        merged.SoundPack.Should().Be("retro");
        merged.IconStyle.Should().BeNull("the receiver has no icon style set, and the publisher's does not fill in");
    }

    [Fact]
    public void Merge_IncomingDesktopIndex_IsDiscarded()
    {
        // D34: the publisher's desktop number means nothing on this machine. Dropping it is
        // what keeps activation, tray ordering and workspace inference on the receiver's own
        // per-publisher mapping.
        var merged = RemoteSessionMerge.Merge(Model(desktopIndex: 7), existing: null, "workstation");

        merged.DesktopIndex.Should().BeNull();
    }

    [Fact]
    public void Merge_ReceiverDesktopIndex_IsKept()
    {
        var merged = RemoteSessionMerge.Merge(Model(desktopIndex: 7), Model(desktopIndex: 2), "workstation");

        merged.DesktopIndex.Should().Be(2);
    }

    [Fact]
    public void Merge_PayloadClaimingAnotherOrigin_IsOverwritten()
    {
        var merged = RemoteSessionMerge.Merge(Model(originMachine: "impostor"), existing: null, "workstation");

        merged.OriginMachine.Should().Be("workstation");
    }

    [Fact]
    public void Merge_NonOwnedFields_AreTakenAsSent()
    {
        var existing = Model(status: "busy") with { LastMessage = "old", ClaudePid = 111 };
        var incoming = Model(status: "idle") with { LastMessage = "new", ClaudePid = 222 };

        var merged = RemoteSessionMerge.Merge(incoming, existing, "workstation");

        merged.Status.Should().Be("idle");
        merged.LastMessage.Should().Be("new");
        merged.ClaudePid.Should().Be(222);
    }
}
