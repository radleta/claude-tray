using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Windows.Forms;
using Imrdy.Core;
using Imrdy.Core.Publishing;
using Imrdy.Core.Rendering;
using Imrdy.Windows.Connections;
using Microsoft.Extensions.Logging;

namespace Imrdy.Windows.Rendering;

/// <summary>
/// Renders a <see cref="ConnectionsForm"/> from a <see cref="ConnectionsViewModel"/> fixture
/// JSON and captures the result via <c>DrawToBitmap</c> to a PNG.
/// <para>
/// Unlike the three dashboard/overlay renderers, this window is <em>resizable</em>, and
/// <c>DrawToBitmap</c> captures whatever size the form currently has. So the size is pinned
/// here to an explicit constant rather than left to the form's own default — otherwise a
/// later default change would silently move every reference PNG.
/// </para>
/// </summary>
internal sealed class ConnectionsRenderer : IRenderableSurface
{
    // Offscreen sentinel — Windows treats coordinates this far outside any monitor as
    // effectively hidden. Used to make Show() invisible during headless capture.
    private const int OffscreenX = -32000;
    private const int OffscreenY = -32000;

    /// <summary>The fixed capture size. See the class remarks: the window resizes, the fixture must not.</summary>
    private static readonly Size FixtureClientSize = new(880, 460);

    public string Name => "connections";
    public string Description => "ConnectionsForm rendered from a ConnectionsViewModel fixture.";
    public string? DefaultFixtureDir => "tests/fixtures/connections";
    public string DefaultOutputExtension => "png";

    public RenderResult Render(RenderContext ctx)
    {
        try
        {
            if (ctx.Args.Length < 1 || string.IsNullOrWhiteSpace(ctx.Args[0]) || !File.Exists(ctx.Args[0]))
                return new RenderResult(false, "fixture path missing or not found", 0, 0);

            ConnectionsViewModel? vm;
            try
            {
                var bytes = File.ReadAllBytes(ctx.Args[0]);
                vm = JsonSerializer.Deserialize(bytes, ImrdyJsonContext.Default.ConnectionsViewModel);
            }
            catch (Exception ex)
            {
                return new RenderResult(false, $"fixture parse failed: {ex.Message}", 0, 0);
            }

            if (vm is null)
                return new RenderResult(false, "fixture parse failed: deserialized to null", 0, 0);

            using var form = new ConnectionsForm(
                new NullConnectionsHost(vm),
                ctx.LoggerFactory.CreateLogger<ConnectionsRenderer>());

            // Show() offscreen + DoEvents() drains the pending paint cycle for every child, so
            // DrawToBitmap captures the full layout. CreateControl() alone leaves children
            // unpainted — see WorkspaceDashboardRenderer for the same note.
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(OffscreenX, OffscreenY);
            form.ClientSize = FixtureClientSize;
            form.Show();
            try
            {
                Application.DoEvents();
                form.Update(vm);
                form.PerformLayout();
                Application.DoEvents();

                using var bmp = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));

                Directory.CreateDirectory(Path.GetDirectoryName(ctx.OutputPath)!);
                bmp.Save(ctx.OutputPath, ImageFormat.Png);

                return new RenderResult(true, null, form.Width, form.Height);
            }
            finally
            {
                form.Hide();
            }
        }
        catch (Exception ex)
        {
            return new RenderResult(false, ex.Message, 0, 0);
        }
    }
}
