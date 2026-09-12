using System.Drawing;
using System.Windows.Forms;
using Imrdy.Core.Publishing;
using Imrdy.Windows.Theme;
using Microsoft.Extensions.Logging;

namespace Imrdy.Windows.Connections;

/// <summary>
/// D26's connections window: a normal, activatable, resizable window listing every link in
/// both directions, with add / edit / remove / clear-this-machine.
/// <para>
/// <b>Deliberately not a hover dashboard.</b> This form does not derive from
/// <c>HoverDashboardFormBase</c> and copies nothing from that lifecycle — no
/// <c>ShowWithoutActivation</c>, no <c>WM_MOUSEACTIVATE</c> guard returning
/// <c>MA_NOACTIVATE</c>, no recreate-and-dispose-per-show, no dwell/grace state machine, and
/// no <c>FormBorderStyle.None</c> with <c>TopMost</c> and <c>ShowInTaskbar = false</c>. The
/// first two would make the window unfocusable, which is the opposite of what it is for. It
/// is created once and shown, activated and hidden.
/// </para>
/// <para>
/// For its look it follows <c>OverlayPanel</c> instead: mica plus DWM rounded corners, with
/// the Windows 10 <c>ApplyRoundedRegion</c> fallback taken only when DWM declines. That
/// fallback recomputes the region on <em>every</em> size change, which no existing dashboard
/// needs because none of them resize — a region computed once clips wrongly the moment this
/// window is dragged larger.
/// </para>
/// </summary>
internal sealed class ConnectionsForm : Form
{
    private const int RefreshIntervalMs = 1000;
    private const int CornerRadius = 14;

    /// <summary>
    /// How far the empty-state label sits below the list's top edge. A <see cref="ListView"/>
    /// draws its column header inside its own client area, so an overlay pinned to the list's
    /// origin would cover the headers this empty state exists to keep visible.
    /// </summary>
    private const int ListHeaderHeight = 26;

    private static readonly Color BgRow = Color.FromArgb(34, 36, 46);
    private static readonly Color BgHeader = Color.FromArgb(22, 24, 32);
    private static readonly Color FgOk = Color.FromArgb(70, 200, 120);
    private static readonly Color FgBad = Color.FromArgb(232, 90, 90);
    private static readonly Color FgIdleState = Color.FromArgb(150, 160, 200);

    private readonly IConnectionsHost _host;
    private readonly ILogger _logger;

    private readonly Label _title = new();
    private readonly Label _subtitle = new();
    private readonly ListView _list = new();
    private readonly Label _emptyState = new();
    private readonly Button _addButton = new();
    private readonly Button _editButton = new();
    private readonly Button _removeButton = new();
    private readonly Button _clearButton = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new();

    /// <summary>
    /// True when DWM rounded the corners itself. When false the Windows 10 GDI region
    /// fallback is in use and has to be recomputed on every resize — the whole reason this
    /// field exists, since the DWM path needs nothing.
    /// </summary>
    private bool _usesDwmCorners;

    private ConnectionsViewModel _vm = new(string.Empty, false, 0, false, []);

    public ConnectionsForm(IConnectionsHost host, ILogger logger)
    {
        _host = host;
        _logger = logger;

        Text = "imrdy — Connections";
        // A normal window: sizable chrome, present in the taskbar, not topmost, and it
        // activates like anything else. Every one of those is the opposite of a hover
        // dashboard, and each is load-bearing rather than a default left in place.
        FormBorderStyle = FormBorderStyle.Sizable;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 320);
        ClientSize = new Size(880, 460);
        BackColor = ImrdyPalette.BgForm;
        ForeColor = ImrdyPalette.FgPrimary;
        KeyPreview = true;

        BuildLayout();

        _refreshTimer.Interval = RefreshIntervalMs;
        _refreshTimer.Tick += (_, _) => RefreshFromHost();
    }

    /// <summary>
    /// The sole content source, mirroring <c>WorkspaceDashboardForm.Update</c>: every dynamic
    /// control is refreshed from the view model and nothing else writes to them.
    /// </summary>
    public void Update(ConnectionsViewModel vm)
    {
        _vm = vm;

        _title.Text = $"Connections — {vm.MachineName}";
        var unauthenticated = vm.ListenEnabled && !vm.AuthKeyConfigured;
        _subtitle.Text = vm.ListenEnabled
            ? $"Listening on port {vm.ListenPort}{(unauthenticated ? " · NO AUTH KEY: any peer that reaches this port is accepted" : "")} · {vm.Rows.Count} link(s)"
            : $"Not listening — {ConnectionRowFormatter.NotListening} · {vm.Rows.Count} link(s)";

        // Only the unauthenticated case is an alarm. Not listening is an ordinary, working
        // configuration on the WSL path — the file sink needs no listener — and painting it red
        // told the operator to fix something that was not broken.
        _subtitle.ForeColor = unauthenticated ? FgBad : ImrdyPalette.FgSecondary;

        var selected = SelectedName();
        _emptyState.Visible = vm.Rows.Count == 0;

        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var row in vm.Rows)
            {
                var item = new ListViewItem(row.Name) { Tag = row.Name };
                item.SubItems.Add(ConnectionRowFormatter.Endpoint(row));
                item.SubItems.Add(ConnectionRowFormatter.Outbound(row));
                item.SubItems.Add(ConnectionRowFormatter.Inbound(row));
                item.SubItems.Add(ConnectionRowFormatter.Desktop(row));
                item.SubItems.Add(ConnectionRowFormatter.Notify(row));
                item.SubItems.Add(row.LastDelivery);
                item.SubItems.Add(ConnectionRowFormatter.LastError(row));
                item.Selected = row.Name == selected;
                _list.Items.Add(item);
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        UpdateButtonState();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ImrdyPalette.ApplyMica(this);
        // The body is dark unconditionally, like every other imrdy surface, so the title bar
        // is forced dark too. Without this the caption follows the OS theme and a light-theme
        // machine gets a white title bar glued to a dark window — the one way this form fails
        // the "reads correctly in both Windows themes" bar.
        ImrdyPalette.ApplyDarkTitleBar(this);
        // The list owns a scrollbar whenever it is narrower than its columns, and a scrollbar
        // is painted from the OS theme rather than from the control's colours.
        ImrdyPalette.ApplyDarkScrollbars(_list);
        _usesDwmCorners = ImrdyPalette.ApplyRoundedCorners(this);
        if (!_usesDwmCorners)
        {
            ImrdyPalette.ApplyRoundedRegion(this, CornerRadius);
        }
    }

    /// <summary>
    /// The Windows 10 fallback's whole difficulty: a <see cref="Region"/> is in window
    /// coordinates and does not stretch, so one computed at the original size clips the
    /// window's own content the moment it is resized. Recomputed here; the DWM path is
    /// skipped because DWM re-rounds the frame itself.
    /// <para>
    /// The handle check is load-bearing and was found live: setting <c>MinimumSize</c> and
    /// <c>ClientSize</c> in the constructor raises <c>OnResize</c> <em>before</em>
    /// <c>OnHandleCreated</c>, when <see cref="_usesDwmCorners"/> is still its default false.
    /// Without the check, a Windows 11 machine applied the Win10 GDI region at the
    /// constructor's size, DWM then took the corners over, and nothing ever recomputed that
    /// region — so the window painted clipped to its opening size the moment it was dragged
    /// larger, title bar included.
    /// </para>
    /// </summary>
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (IsHandleCreated && !_usesDwmCorners)
        {
            ImrdyPalette.ApplyRoundedRegion(this, CornerRadius);
        }
    }

    /// <summary>
    /// Refreshing follows visibility rather than <c>OnShown</c>, which WinForms raises only the
    /// first time an instance is displayed. This window is created once and hidden on close, so
    /// a timer started from <c>OnShown</c> ran for the first open and never again: from the
    /// second open on, the window rendered the snapshot captured when it was last closed, and a
    /// stale row is indistinguishable from a live one — the one thing it exists to report.
    /// </summary>
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);

        if (Visible)
        {
            RefreshFromHost();
            _refreshTimer.Start();
        }
        else
        {
            _refreshTimer.Stop();
        }
    }

    /// <summary>
    /// Closing hides rather than disposes: the window is created once and reopened from the
    /// controller menu with its list intact. Only <see cref="Dispose(bool)"/> at tray shutdown
    /// really tears it down.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Every close hides except the ones that mean the process is going away. Testing for
        // UserClosing alone is not enough and was wrong: WinForms reports that reason only for
        // the caption's X, while Escape goes through Form.Close() and arrives as None — so the
        // window disposed itself, and the next Connections… click threw ObjectDisposedException
        // out of the menu handler. Found by closing and reopening a live window.
        if (e.CloseReason is not (CloseReason.ApplicationExitCall or CloseReason.WindowsShutDown))
        {
            e.Cancel = true;
            // Hide() raises OnVisibleChanged, which stops the refresh timer.
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void BuildLayout()
    {
        _title.Text = "Connections";
        _title.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
        _title.ForeColor = ImrdyPalette.FgPrimary;
        _title.AutoSize = true;
        _title.Location = new Point(16, 12);

        _subtitle.Font = new Font("Segoe UI", 8.5f);
        _subtitle.ForeColor = ImrdyPalette.FgSecondary;
        _subtitle.AutoSize = true;
        _subtitle.Location = new Point(16, 36);

        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.OwnerDraw = true;
        _list.BorderStyle = BorderStyle.None;
        _list.BackColor = BgRow;
        _list.ForeColor = ImrdyPalette.FgPrimary;
        _list.Location = new Point(16, 60);
        _list.Size = new Size(ClientSize.Width - 32, ClientSize.Height - 60 - 52);
        _list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        // Widths sum to under the default client width so the list never opens with a
        // horizontal scrollbar; the window is resizable, so wider is the operator's call.
        _list.Columns.Add("Machine", 140);
        _list.Columns.Add("Endpoint", 155);
        _list.Columns.Add("Outbound", 85);
        _list.Columns.Add("Inbound", 80);
        _list.Columns.Add("Desktop", 68);
        _list.Columns.Add("Notify", 55);
        _list.Columns.Add("Last delivery", 95);
        _list.Columns.Add("Last error", 160);
        _list.Resize += (_, _) => StretchLastColumn();
        _list.DrawColumnHeader += OnDrawColumnHeader;
        _list.DrawSubItem += OnDrawSubItem;
        _list.SelectedIndexChanged += (_, _) => UpdateButtonState();
        _list.DoubleClick += (_, _) => EditSelected();

        // Overlaid on the list's body rather than shown instead of the list, so the column
        // headers stay put: the operator sees an empty table, not a missing one. Same sentence
        // the two CLI surfaces print, from the one place that owns it.
        //
        // It is a child of the list, not a sibling on the form: a ListView is a native control
        // with its own window, so a sibling label overlapping it is painted underneath it no
        // matter where it sits in the form's z-order. Parenting it to the list puts it above
        // the list's own surface, which is the only placement that actually shows.
        _emptyState.Text = ConnectionRowFormatter.NoLinks;
        _emptyState.Font = new Font("Segoe UI", 9f);
        _emptyState.ForeColor = ImrdyPalette.FgMuted;
        _emptyState.BackColor = BgRow;
        _emptyState.TextAlign = ContentAlignment.MiddleCenter;
        _emptyState.Location = new Point(0, ListHeaderHeight);
        _emptyState.Size = new Size(_list.ClientSize.Width, 44);
        _emptyState.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _emptyState.Visible = false;
        _list.Controls.Add(_emptyState);

        StyleButton(_addButton, "Add…", 16);
        StyleButton(_editButton, "Edit…", 100);
        StyleButton(_removeButton, "Remove", 184);
        StyleButton(_clearButton, "Clear sessions", 268);
        _clearButton.Width = 110;

        _addButton.Click += (_, _) => AddNew();
        _editButton.Click += (_, _) => EditSelected();
        _removeButton.Click += (_, _) => RemoveSelected();
        _clearButton.Click += (_, _) => ClearSelected();

        Controls.Add(_title);
        Controls.Add(_subtitle);
        Controls.Add(_list);
        Controls.Add(_addButton);
        Controls.Add(_editButton);
        Controls.Add(_removeButton);
        Controls.Add(_clearButton);

        StretchLastColumn();
    }

    private void StyleButton(Button button, string text, int x)
    {
        button.Text = text;
        button.Size = new Size(78, 28);
        button.Location = new Point(x, ClientSize.Height - 40);
        button.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Color.FromArgb(60, 64, 78);
        button.BackColor = BgRow;
        button.ForeColor = ImrdyPalette.FgPrimary;
        button.Font = new Font("Segoe UI", 8.5f);
    }

    /// <summary>
    /// Gives the last column whatever width the others leave over. Without it the strip right
    /// of the final column is painted by the OS header theme rather than by
    /// <see cref="OnDrawColumnHeader"/> — a light sliver on a dark window that gets wider the
    /// more the operator resizes. Owner-drawing cannot reach that strip; only filling it can.
    /// </summary>
    private void StretchLastColumn()
    {
        if (_list.Columns.Count == 0) return;

        var used = 0;
        for (var i = 0; i < _list.Columns.Count - 1; i++)
        {
            used += _list.Columns[i].Width;
        }

        var remaining = _list.ClientSize.Width - used;
        _list.Columns[^1].Width = Math.Max(80, remaining);
    }

    private void OnDrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        // Owner-drawn so the header carries the imrdy palette. A themed header would paint
        // itself in the OS colours and read as a light strip glued to a dark window.
        using var bg = new SolidBrush(BgHeader);
        e.Graphics.FillRectangle(bg, e.Bounds);
        TextRenderer.DrawText(
            e.Graphics,
            e.Header?.Text ?? string.Empty,
            _list.Font,
            Rectangle.Inflate(e.Bounds, -6, 0),
            ImrdyPalette.FgMuted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        var selected = e.Item?.Selected == true;
        using var bg = new SolidBrush(selected ? Color.FromArgb(48, 52, 68) : BgRow);
        e.Graphics.FillRectangle(bg, e.Bounds);

        var text = e.SubItem?.Text ?? string.Empty;
        TextRenderer.DrawText(
            e.Graphics,
            text,
            _list.Font,
            Rectangle.Inflate(e.Bounds, -6, 0),
            ColorForColumn(e.ColumnIndex, text),
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private static Color ColorForColumn(int columnIndex, string text) => columnIndex switch
    {
        // The two state columns carry the only colour in the list, so a bad link is findable
        // without reading every row.
        2 or 3 when text.StartsWith("Connected", StringComparison.Ordinal) => FgOk,
        2 or 3 when text.StartsWith("Failed", StringComparison.Ordinal) => FgBad,
        2 or 3 => FgIdleState,
        7 when text.Length > 0 => FgBad,
        0 => ImrdyPalette.FgPrimary,
        _ => ImrdyPalette.FgSecondary,
    };

    private void RefreshFromHost()
    {
        try
        {
            Update(_host.BuildViewModel());
        }
        catch (Exception ex)
        {
            // A refresh tick must never take the window down; the previous content stays.
            _logger.LogWarning(ex, "ConnectionsForm: refresh failed, keeping the last rendered view");
        }
    }

    private string? SelectedName() =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as string : null;

    private ConnectionRow? SelectedRow()
    {
        var name = SelectedName();
        return name is null
            ? null
            : _vm.Rows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateButtonState()
    {
        var row = SelectedRow();
        _editButton.Enabled = row is not null;
        _removeButton.Enabled = row is { IsRegistered: true };
        _clearButton.Enabled = row is not null;
    }

    private void AddNew()
    {
        using var dialog = new PublisherEditDialog(null);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Result is { } entry)
        {
            _host.SavePublisher(entry, previousName: null);
            RefreshFromHost();
        }
    }

    private void EditSelected()
    {
        if (SelectedRow() is not { } row) return;

        // A row whose name came only from the beat filename must not seed a record with it. The
        // token is lossy — PC-Excalibur-Ubuntu-24.04 flattens to pc-excalibur-ubuntu-24_04 — and
        // every behaviour keyed on a saved record joins on its name by plain case-insensitive
        // equality against the publisher's own origin_machine, so such a record would never match
        // again: the desktop mapping, the mute and clear-this-machine would all silently do
        // nothing, forever, with the operator shown a healthy registered row. So the name field
        // starts empty and the dialog's own "Machine name is required" rule makes them type it.
        if (row.NameIsToken)
        {
            MessageBox.Show(
                this,
                $"This publisher has not delivered a session yet, so imrdy only knows it by its "
                + $"heartbeat filename: {row.Name}\n\n"
                + "That name is flattened and may not be exact. Enter the machine's own name — "
                + "network.machineName on that machine, or <hostname>-<distro> by default — or it "
                + "will never match the sessions it sends.",
                "imrdy — Confirm the machine name",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        // Null for an unregistered row: there is no record to rename, and handing the token name
        // in as one would ask the store to remove a record that does not exist.
        var previousName = row.IsRegistered ? row.Name : null;

        using var dialog = new PublisherEditDialog(new PublisherEntry
        {
            Name = row.NameIsToken ? string.Empty : row.Name,
            Endpoint = row.Endpoint,
            DesktopIndex = row.DesktopIndex,
            Muted = row.Muted,
            Enabled = row.Enabled,
        });

        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Result is { } entry)
        {
            // The name is editable here, and the store upserts on it, so a rename that did not
            // say what it replaced left two records: the old one kept its endpoint, its desktop
            // mapping, its mute and its sink, and the operator saw one machine twice.
            _host.SavePublisher(entry, previousName);
            RefreshFromHost();
        }
    }

    private void RemoveSelected()
    {
        if (SelectedRow() is not { IsRegistered: true } row) return;

        var answer = MessageBox.Show(
            this,
            $"Remove {row.Name} and delete the sessions it delivered?",
            "imrdy — Remove link",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);

        if (answer != DialogResult.OK) return;

        _host.RemovePublisher(row.Name);
        RefreshFromHost();
    }

    private void ClearSelected()
    {
        if (SelectedRow() is not { } row) return;

        var answer = MessageBox.Show(
            this,
            $"Delete every session {row.Name} delivered? A live publisher will repopulate them.",
            "imrdy — Clear machine",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);

        if (answer != DialogResult.OK) return;

        _host.ClearMachineSessions(row.Name);
        RefreshFromHost();
    }
}
