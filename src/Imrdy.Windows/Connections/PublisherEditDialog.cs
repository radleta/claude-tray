using System.Drawing;
using System.Windows.Forms;
using Imrdy.Core.Publishing;
using Imrdy.Windows.Theme;

namespace Imrdy.Windows.Connections;

/// <summary>
/// The add/edit half of D26's "manage". A small modal over one <see cref="PublisherEntry"/>.
/// Validation is deliberately thin: the endpoint's <em>shape</em> decides which sink gets
/// built (<c>SinkFactory</c>), and re-deciding that here would be a second copy of a rule
/// that already has one home. What is checked is what cannot be recovered from — an empty
/// name, which would write an unaddressable record.
/// <para>
/// A blank endpoint is <em>not</em> an error: it is how the operator says receive-only (r-1),
/// and it round-trips as null rather than as an empty string, because an empty string is a
/// malformed record and a null one is a deliberate one.
/// </para>
/// </summary>
internal sealed class PublisherEditDialog : Form
{
    private readonly TextBox _name = new();
    private readonly TextBox _endpoint = new();
    private readonly TextBox _desktop = new();
    private readonly CheckBox _muted = new();
    private readonly CheckBox _enabled = new();
    private readonly Label _error = new();

    /// <summary>The saved record, or null when the dialog was cancelled.</summary>
    public PublisherEntry? Result { get; private set; }

    /// <param name="existing">The record being edited, or null to add a new one.</param>
    public PublisherEditDialog(PublisherEntry? existing)
    {
        Text = existing is null ? "imrdy — Add link" : $"imrdy — Edit {existing.Name}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(400, 232);
        BackColor = ImrdyPalette.BgForm;
        ForeColor = ImrdyPalette.FgPrimary;

        BuildLayout();

        if (existing is not null)
        {
            _name.Text = existing.Name;
            _endpoint.Text = existing.Endpoint ?? string.Empty;
            _desktop.Text = existing.DesktopIndex?.ToString() ?? string.Empty;
            _muted.Checked = existing.Muted;
            _enabled.Checked = existing.Enabled;
        }
        else
        {
            _enabled.Checked = true;
        }
    }

    private void BuildLayout()
    {
        AddLabel("Machine name", 16);
        Place(_name, 36);

        AddLabel("Endpoint  (host:port, a directory for a file sink, or blank to receive only)", 66);
        Place(_endpoint, 86);

        AddLabel("Desktop index  (blank for none)", 116);
        Place(_desktop, 136);
        _desktop.Width = 80;

        _muted.Text = "Mute notifications from this machine";
        _muted.ForeColor = ImrdyPalette.FgSecondary;
        _muted.AutoSize = true;
        _muted.Location = new Point(16, 166);

        _enabled.Text = "Enabled";
        _enabled.ForeColor = ImrdyPalette.FgSecondary;
        _enabled.AutoSize = true;
        _enabled.Location = new Point(240, 166);

        _error.ForeColor = Color.FromArgb(232, 90, 90);
        _error.AutoSize = true;
        _error.Location = new Point(16, 196);

        var ok = new Button
        {
            Text = "Save",
            Size = new Size(80, 26),
            Location = new Point(216, 192),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(34, 36, 46),
            ForeColor = ImrdyPalette.FgPrimary,
        };
        ok.Click += (_, _) => TrySave();

        var cancel = new Button
        {
            Text = "Cancel",
            Size = new Size(80, 26),
            Location = new Point(304, 192),
            DialogResult = DialogResult.Cancel,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(34, 36, 46),
            ForeColor = ImrdyPalette.FgPrimary,
        };

        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(_muted);
        Controls.Add(_enabled);
        Controls.Add(_error);
        Controls.Add(ok);
        Controls.Add(cancel);
    }

    private void AddLabel(string text, int y)
    {
        Controls.Add(new Label
        {
            Text = text,
            ForeColor = ImrdyPalette.FgMuted,
            AutoSize = true,
            Location = new Point(16, y),
            Font = new Font("Segoe UI", 8f),
        });
    }

    private void Place(TextBox box, int y)
    {
        box.Location = new Point(16, y);
        box.Width = 368;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.BackColor = Color.FromArgb(34, 36, 46);
        box.ForeColor = ImrdyPalette.FgPrimary;
        Controls.Add(box);
    }

    private void TrySave()
    {
        var name = _name.Text.Trim();
        var endpoint = _endpoint.Text.Trim();

        if (name.Length == 0)
        {
            _error.Text = "Machine name is required.";
            return;
        }

        int? desktop = null;
        var desktopText = _desktop.Text.Trim();
        if (desktopText.Length > 0)
        {
            if (!int.TryParse(desktopText, out var parsed) || parsed < 0)
            {
                _error.Text = "Desktop index must be a non-negative whole number, or blank.";
                return;
            }
            desktop = parsed;
        }

        Result = new PublisherEntry
        {
            Name = name,
            Endpoint = endpoint.Length == 0 ? null : endpoint,
            DesktopIndex = desktop,
            Muted = _muted.Checked,
            Enabled = _enabled.Checked,
        };

        DialogResult = DialogResult.OK;
        Close();
    }
}
