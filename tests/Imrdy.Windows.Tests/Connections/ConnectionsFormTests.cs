using System.Drawing;
using System.Windows.Forms;
using FluentAssertions;
using Imrdy.Core.Publishing;
using Imrdy.Windows.Connections;
using Imrdy.Windows.Dashboard;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Imrdy.Windows.Tests.Connections;

/// <summary>
/// The connections window's binding constraints are prohibitions, and a prohibition is
/// exactly the kind of thing a later refactor re-introduces by copying the nearest similar
/// form. These tests pin the four that would break it: it must not be a hover dashboard, it
/// must activate, it must show in the taskbar, and it must resize.
/// </summary>
public class ConnectionsFormTests
{
    private static ConnectionsForm NewForm(ConnectionsViewModel? vm = null) =>
        new(new StubHost(vm ?? new ConnectionsViewModel("box", true, 47600, AuthKeyConfigured: true, [])), NullLogger.Instance);

    [Fact]
    public void Form_IsNotAHoverDashboard()
    {
        using var form = NewForm();

        form.Should().NotBeAssignableTo<HoverDashboardFormBase>(
            "the hover base carries ShowWithoutActivation and a WM_MOUSEACTIVATE guard that "
            + "would make this window unfocusable — the opposite of what it is for");
    }

    [Fact]
    public void Form_ActivatesNormally()
    {
        using var form = NewForm();

        // ShowWithoutActivation is protected; the base Form default is false and the hover
        // base overrides it to true. Reading it back is the only way to catch a copy-paste.
        var property = typeof(Form).GetProperty(
            "ShowWithoutActivation",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        property!.GetValue(form).Should().Be(false,
            "a management window the operator types into has to take focus");
    }

    [Fact]
    public void Form_IsAResizableTaskbarWindow()
    {
        using var form = NewForm();

        form.FormBorderStyle.Should().Be(FormBorderStyle.Sizable);
        form.ShowInTaskbar.Should().BeTrue();
        form.TopMost.Should().BeFalse("a normal window, not an overlay");
        form.MinimumSize.Width.Should().BeGreaterThan(0, "a resizable list needs a floor to stay readable");
    }

    [Fact]
    public void Update_IsTheSoleContentSourceAndRendersOneRowPerLink()
    {
        var vm = new ConnectionsViewModel("box", true, 47600, AuthKeyConfigured: true, [
            new ConnectionRow("alpha", "1.2.3.4:47600", true, true, false, 2,
                new SinkHealth("alpha", SinkState.Connected, null, null, 3), null, "4m ago"),
            new ConnectionRow("beta", @"C:\mount\sessions", true, true, true, null,
                new SinkHealth("beta", SinkState.FileSink, null, null, 1), null, "never"),
        ]);

        using var form = NewForm();
        form.Update(vm);

        var list = form.Controls.OfType<ListView>().Should().ContainSingle().Subject;
        list.Items.Count.Should().Be(2);
        list.Items[0].Text.Should().Be("alpha");
        list.Items[1].SubItems[2].Text.Should().Be("FileSink",
            "D27: a file sink is never rendered as Connected");
    }

    [Fact]
    public void Update_LinkThatNeverConnectedInbound_ReadsNeverNotFailed()
    {
        var vm = new ConnectionsViewModel("box", true, 47600, AuthKeyConfigured: true, [
            new ConnectionRow("wsl", @"C:\mount\sessions", true, true, false, 1,
                new SinkHealth("wsl", SinkState.FileSink, null, null, 0), Inbound: null, LastDelivery: "never"),
        ]);

        using var form = NewForm();
        form.Update(vm);

        var list = form.Controls.OfType<ListView>().Single();
        list.Items[0].SubItems[3].Text.Should().Be("never",
            "a file-sink publisher has no inbound link to have lost, so calling it Failed "
            + "would send the operator after a fault that does not exist");
    }

    [Fact]
    public void GrowingTheWindow_StretchesTheListAndKeepsTheButtonsOnScreen()
    {
        // "Resizes" is half the acceptance outcome, and growing is the half that broke: the
        // list kept its original size while the window got bigger, leaving a band of empty
        // form and the buttons off the visible area. Shrinking hid it — a list that is merely
        // clipped looks exactly like a list that fits.
        using var form = NewForm();
        form.Show();
        var list = form.Controls.OfType<ListView>().Single();
        var before = list.Width;

        form.Size = new Size(form.Width + 400, form.Height + 240);

        list.Width.Should().Be(before + 400, "the list is anchored to both side edges");
        form.Controls.OfType<Button>().Should().AllSatisfy(b =>
            b.Bottom.Should().BeLessThanOrEqualTo(form.ClientSize.Height,
                "the buttons are anchored to the bottom edge"));
    }

    [Fact]
    public void GrowingTheWindow_LeavesNoRegionClippingItsOwnFrame()
    {
        // Whichever corner path this machine takes, the invariant is the same: a window may
        // not carry a Region smaller than itself. Constructor-time resizes raise OnResize
        // before the handle exists, and a region applied there is one DWM never replaces —
        // the window then paints clipped to its opening size, which is what a live drag showed.
        using var form = NewForm();
        form.Show();

        form.Size = new Size(form.Width + 400, form.Height + 240);

        if (form.Region is not null)
        {
            using var graphics = form.CreateGraphics();
            var bounds = form.Region.GetBounds(graphics);
            bounds.Width.Should().BeApproximately(form.Width, 1f);
            bounds.Height.Should().BeApproximately(form.Height, 1f);
        }
    }

    [Fact]
    public void Close_HidesRatherThanDisposes_SoTheWindowReopensWithItsListIntact()
    {
        // Escape and any programmatic close arrive as CloseReason.None, not UserClosing —
        // testing for UserClosing alone let the form dispose itself, and the next
        // Connections… click threw ObjectDisposedException out of the menu handler.
        using var form = NewForm(new ConnectionsViewModel("box", true, 47600, AuthKeyConfigured: true, [
            new ConnectionRow("alpha", "1.2.3.4:47600", true, true, false, null, null, null, "never"),
        ]));
        form.Show();

        form.Close();

        form.IsDisposed.Should().BeFalse("the window is created once and hidden on close");
        form.Visible.Should().BeFalse();
        form.Controls.OfType<ListView>().Single().Items.Count.Should().Be(1,
            "reopening must show the list it was closed with");
    }

    [Fact]
    public void Reopening_RefreshesFromTheHostRatherThanRenderingWhatItWasClosedWith()
    {
        // Form.Shown is raised only the first time an instance is displayed, and this window is
        // created once and hidden on close — so a refresh timer started from OnShown ran for the
        // first open and never again. A stale row is indistinguishable from a live one, which
        // defeats the one thing the window reports.
        var host = new StubHost(Vm("alpha"));
        using var form = new ConnectionsForm(host, NullLogger.Instance);

        form.Show();
        form.Close();

        host.Vm = Vm("alpha", "beta");
        form.Show();

        form.Controls.OfType<ListView>().Single().Items.Count.Should().Be(2,
            "the second open must render what the host reports now");
    }

    private static ConnectionsViewModel Vm(params string[] names) =>
        new("box", true, 47600, AuthKeyConfigured: true,
            names.Select(n => new ConnectionRow(n, "1.2.3.4:47600", true, true, false, null, null, null, "never")).ToList());

    private sealed class StubHost(ConnectionsViewModel vm) : IConnectionsHost
    {
        public ConnectionsViewModel Vm { get; set; } = vm;

        public ConnectionsViewModel BuildViewModel() => Vm;
        public void SavePublisher(PublisherEntry entry, string? previousName) { }
        public void RemovePublisher(string name) { }
        public void ClearMachineSessions(string name) { }
    }
}
