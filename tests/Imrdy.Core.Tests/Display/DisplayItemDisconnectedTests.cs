using FluentAssertions;
using Imrdy.Core.Display;

namespace Imrdy.Core.Tests.Display;

/// <summary>
/// D20's disconnected flag rides the shared display model, so the tray and the overlay
/// cannot disagree about which sessions have a live publisher. These tests pin that it
/// survives <see cref="DisplayItemCollection.Build"/> and that it stays independent of
/// the aging tier it must never be confused with.
/// </summary>
public class DisplayItemDisconnectedTests
{
    private static DisplayItemInput Input(
        string id,
        bool disconnected = false,
        int agingTier = 0,
        int? desktop = 0) =>
        new(id, DisplayItemType.Session, "idle", desktop, "circles", agingTier, true, id, disconnected);

    [Fact]
    public void Build_CarriesTheDisconnectedFlagThrough()
    {
        var built = DisplayItemCollection.Build(
            [Input("live"), Input("gone", disconnected: true)],
            trayEnabled: true);

        built.ForTray.Single(x => x.Id == "gone").IsDisconnected.Should().BeTrue();
        built.ForTray.Single(x => x.Id == "live").IsDisconnected.Should().BeFalse();
    }

    [Fact]
    public void Build_GivesTrayAndOverlayTheSameFlag()
    {
        var built = DisplayItemCollection.Build([Input("gone", disconnected: true)], trayEnabled: true);

        built.ForOverlay.Single().IsDisconnected.Should()
            .Be(built.ForTray.Single().IsDisconnected,
                "one snapshot feeds both surfaces; a divergence here is a divergence on screen");
    }

    [Fact]
    public void Build_KeepsDisconnectedIndependentOfAgingTier()
    {
        var built = DisplayItemCollection.Build(
            [Input("fresh-but-gone", disconnected: true, agingTier: 0, desktop: 0),
             Input("old-but-live", disconnected: false, agingTier: 4, desktop: 1)],
            trayEnabled: true);

        var gone = built.ForTray.Single(x => x.Id == "fresh-but-gone");
        var live = built.ForTray.Single(x => x.Id == "old-but-live");

        gone.IsDisconnected.Should().BeTrue();
        gone.AgingTier.Should().Be(0, "a link can drop the instant after an event arrives");
        live.IsDisconnected.Should().BeFalse();
        live.AgingTier.Should().Be(4, "a quiet session on a live link is aged, not disconnected");
    }

    [Fact]
    public void DisplayItem_DefaultsToConnected()
    {
        var local = new DisplayItem("id", DisplayItemType.Session, "idle", 0, "circles", 0, true, "id");

        local.IsDisconnected.Should().BeFalse(
            "a local session has no publisher, so every call site that omits the flag means connected");
    }
}
