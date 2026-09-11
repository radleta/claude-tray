using Imrdy.Core;
using Imrdy.Core.Menus;
using Imrdy.Core.Overlay;
using Microsoft.Extensions.Logging;

namespace Imrdy.Windows.Menus;

/// <summary>
/// Builds the controller tray icon context menu.
/// Rebuilt dynamically on each Opening event from in-memory state.
/// </summary>
internal static class ControllerMenuBuilder
{
    public static ContextMenuStrip Create(
        Func<ControllerMenuState> stateProvider,
        Action<ImrdyConfig> onConfigChanged,
        Action<string> onSwitchSession,
        Action<string> onSwitchWorkspace,
        Action onExit,
        Action<string>? onLaunchPreview = null,
        Action? onCloseAllPreviews = null,
        Action? onOpenConnections = null,
        ILogger? logger = null)
    {
        var menu = new ContextMenuStrip();
        // Diagnostic-only (Step 07): see SessionMenuBuilder's Opening comment for the full
        // rationale — same start/end + item-count + e.Cancel logging, correlated by
        // menu.GetHashCode() with TrayApp.ShowContextMenuAt's before/after Show() logs. The
        // controller (tray icon) menu goes through NotifyIconMenuHost, not the AtControl path
        // under live investigation, but it shares MenuRenderer/Opening plumbing so the same
        // instrumentation is cheap and gives a healthy-case baseline to compare against.
        menu.Opening += (_, e) =>
        {
            logger?.LogDebug("ControllerMenuBuilder.Opening: start, menu={MenuId}", menu.GetHashCode());
            try
            {
                var state = stateProvider();
                var items = ControllerMenuModel.Build(state);
                MenuRenderer.Apply(menu, items,
                    tag => OnClick(tag, state, onConfigChanged, onSwitchSession, onSwitchWorkspace, onExit,
                        onLaunchPreview, onCloseAllPreviews, onOpenConnections, logger),
                    logger);

                // The fix (Step 08): ContextMenuStrip.OnOpening pre-sets e.Cancel = true
                // whenever Items.Count == 0 — always true on the FIRST show of this
                // freshly-constructed menu, since items are built here, not at Create() time.
                // Left uncleared, WinForms refuses to display the menu no matter how many
                // items were just added. Only reached when the rebuild above completed
                // without throwing — see MenuOpeningPolicy's remarks for why an exception
                // must skip this line rather than clear Cancel on a partial/stale collection.
                // This menu goes through NotifyIconMenuHost (not the AtControl path where the
                // defect was originally observed), but shares the same Opening/MenuRenderer
                // plumbing and was equally susceptible.
                if (MenuOpeningPolicy.ShouldClearCancel(menu.Items.Count))
                {
                    e.Cancel = false;
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(
                    ex,
                    "ControllerMenuBuilder.Opening: exception rebuilding controller menu — menu.Items left stale/empty, Show() will likely refuse to display");
            }
            logger?.LogDebug(
                "ControllerMenuBuilder.Opening: end, menu={MenuId}, items={ItemCount}, cancel={Cancel}",
                menu.GetHashCode(), menu.Items.Count, e.Cancel);
        };
        return menu;
    }

    private static async void OnClick(
        string tag,
        ControllerMenuState state,
        Action<ImrdyConfig> onConfigChanged,
        Action<string> onSwitchSession,
        Action<string> onSwitchWorkspace,
        Action onExit,
        Action<string>? onLaunchPreview,
        Action? onCloseAllPreviews,
        Action? onOpenConnections,
        ILogger? logger)
    {
        try
        {
            if (tag == "toggle-sound")
            {
                await Task.Run(() => ConfigReader.Update(c => c with { Sound = c.Sound with { Enabled = !state.Config.Sound.Enabled } }));
                onConfigChanged(ConfigReader.Read());
            }
            else if (tag.StartsWith("switch-pack:", StringComparison.Ordinal))
            {
                var packName = tag["switch-pack:".Length..];
                await Task.Run(() => ConfigReader.Update(c => c with { Sound = c.Sound with { DefaultPack = packName } }));
                onConfigChanged(ConfigReader.Read());
            }
            else if (tag.StartsWith("toggle-pack-enabled:", StringComparison.Ordinal))
            {
                var packName = tag["toggle-pack-enabled:".Length..];
                await Task.Run(() => ConfigReader.Update(c =>
                {
                    var disabled = c.Sound.DisabledPacks;
                    var isCurrentlyDisabled = disabled.Any(d =>
                        string.Equals(d, packName, StringComparison.OrdinalIgnoreCase));
                    var newDisabled = isCurrentlyDisabled
                        ? disabled.Where(d => !string.Equals(d, packName, StringComparison.OrdinalIgnoreCase)).ToList()
                        : [.. disabled, packName];
                    return c with { Sound = c.Sound with { DisabledPacks = newDisabled } };
                }));
                onConfigChanged(ConfigReader.Read());
            }
            else if (tag.StartsWith("switch-icon-style:", StringComparison.Ordinal))
            {
                var newStyle = tag["switch-icon-style:".Length..];
                await Task.Run(() => ConfigReader.Update(c => c with { Tray = c.Tray with { IconStyle = newStyle } }));
                onConfigChanged(ConfigReader.Read());
            }
            else if (await TryHandleOverlayTag(tag, state, onConfigChanged, logger))
            {
                // overlay tag handled
            }
            else if (tag == "toggle-tray")
            {
                await Task.Run(() => ConfigReader.Update(c => c with { Tray = c.Tray with { Enabled = !state.Config.Tray.Enabled } }));
                onConfigChanged(ConfigReader.Read());
            }
            else if (tag.StartsWith("switch-session:", StringComparison.Ordinal))
            {
                var sessionId = tag["switch-session:".Length..];
                if (!string.IsNullOrEmpty(sessionId))
                    onSwitchSession(sessionId);
            }
            else if (tag.StartsWith("switch-workspace:", StringComparison.Ordinal))
            {
                var workspacePath = tag["switch-workspace:".Length..];
                if (!string.IsNullOrEmpty(workspacePath))
                    onSwitchWorkspace(workspacePath);
            }
            else if (tag == "open-config")
            {
                OpenFolder("explorer.exe", ImrdyPaths.Home, logger);
            }
            else if (tag == "open-log")
            {
                OpenFolder("explorer.exe", "/select," + state.LogPath, logger);
            }
            else if (tag.StartsWith("dev-preview:", StringComparison.Ordinal))
            {
                var fixturePath = tag["dev-preview:".Length..];
                if (!string.IsNullOrEmpty(fixturePath))
                    onLaunchPreview?.Invoke(fixturePath);
            }
            else if (tag == "dev-preview-close-all")
            {
                onCloseAllPreviews?.Invoke();
            }
            else if (tag == "open-connections")
            {
                onOpenConnections?.Invoke();
            }
            else if (tag == "exit")
            {
                onExit();
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Error handling menu click: {Tag}", tag);
        }
    }

    /// <summary>
    /// Handles overlay-related menu tags. Returns <c>true</c> when the tag was recognized
    /// (regardless of whether a mutation occurred), <c>false</c> when it is not an overlay tag.
    /// On int-parse failure the tag is still recognized — returns <c>true</c> without mutating.
    /// </summary>
    internal static async Task<bool> TryHandleOverlayTag(
        string tag,
        ControllerMenuState state,
        Action<ImrdyConfig> onConfigChanged,
        ILogger? logger = null)
    {
        if (tag == "toggle-overlay")
        {
            await PersistOverlayUpdateAsync(
                c => c with { Overlay = c.Overlay with { Enabled = !c.Overlay.Enabled } },
                "overlay enabled toggle persist failed", logger);
            onConfigChanged(ConfigReader.Read());
            return true;
        }
        if (tag == "toggle-overlay-lock")
        {
            await PersistOverlayUpdateAsync(
                c => c with { Overlay = c.Overlay with { Locked = !c.Overlay.Locked } },
                "overlay lock toggle persist failed", logger);
            onConfigChanged(ConfigReader.Read());
            return true;
        }
        if (tag.StartsWith("set-overlay-position:", StringComparison.Ordinal))
        {
            var position = tag["set-overlay-position:".Length..];
            // D7 — a preset must also write the RESOLVED offset (via Core AnchorToOffset)
            // for the target monitor, or the overlay would not actually move under the
            // offset-as-source-of-truth model (offset wins over Position when both present
            // — see OverlayPlacement.ResolveOrigin). workingArea/panelSize come from the
            // menu-open-time state snapshot (state.OverlayWorkingArea/OverlayPanelSize) —
            // exactly the basis ControllerMenuModel.BuildOverlaySubmenu used to render the
            // Checked state the user just clicked, so write and Checked-state stay coherent.
            var (offsetX, offsetY) = OverlayPlacement.AnchorToOffset(
                position, state.OverlayWorkingArea, state.OverlayPanelSize);
            await PersistOverlayUpdateAsync(
                c => c with { Overlay = c.Overlay with { Position = position, OffsetX = offsetX, OffsetY = offsetY } },
                "overlay position preset persist failed", logger);
            onConfigChanged(ConfigReader.Read());
            return true;
        }
        if (tag.StartsWith("set-overlay-size:", StringComparison.Ordinal))
        {
            if (!int.TryParse(tag["set-overlay-size:".Length..], out var size)) return true;
            await PersistOverlayUpdateAsync(
                c => c with { Overlay = c.Overlay with { Size = size } },
                "overlay size preset persist failed", logger);
            onConfigChanged(ConfigReader.Read());
            return true;
        }
        if (tag.StartsWith("set-overlay-spacing:", StringComparison.Ordinal))
        {
            if (!int.TryParse(tag["set-overlay-spacing:".Length..], out var spacing)) return true;
            await PersistOverlayUpdateAsync(
                c => c with { Overlay = c.Overlay with { Spacing = spacing } },
                "overlay spacing preset persist failed", logger);
            onConfigChanged(ConfigReader.Read());
            return true;
        }
        if (tag.StartsWith("set-overlay-monitor:", StringComparison.Ordinal))
        {
            if (!int.TryParse(tag["set-overlay-monitor:".Length..], out var monitor)) return true;
            await PersistOverlayUpdateAsync(
                c => c with { Overlay = c.Overlay with { Monitor = monitor } },
                "overlay monitor preset persist failed", logger);
            onConfigChanged(ConfigReader.Read());
            return true;
        }
        return false;
    }

    /// <summary>
    /// Shared RMW-persist helper for the 6 overlay menu handlers above (Risk 8 —
    /// csharp-expert "swallowed exceptions" / concurrent-write race). <paramref name="mutate"/>
    /// runs inside <see cref="ConfigReader.Update"/> on a background thread via
    /// <see cref="Task.Run(Action)"/>; the antecedent task is still awaited here (so a fault
    /// propagates to the caller's existing try/catch exactly as before), but a SEPARATE,
    /// deliberately-discarded fault-observing continuation also logs the full exception with
    /// a handler-specific message via <paramref name="logger"/> — mirroring the drag-drop
    /// persist pattern in <c>OverlayPanel.OnMouseUp</c> (Step 04b). The continuation task is
    /// intentionally never awaited: awaiting a <c>TaskContinuationOptions.OnlyOnFaulted</c>
    /// continuation directly throws <see cref="TaskCanceledException"/> on the (common)
    /// success path, since the continuation itself transitions to Canceled when its predicate
    /// does not match. Writes serialize through <see cref="ConfigReader.Update"/> /
    /// <c>AtomicFileWriter</c>; the resulting FSW re-entrant reload is a harmless no-op
    /// (config-live-reload.md, R4), so no additional ordering guard is needed here.
    /// </summary>
    private static async Task PersistOverlayUpdateAsync(
        Func<ImrdyConfig, ImrdyConfig> mutate,
        string faultMessage,
        ILogger? logger)
    {
        var updateTask = Task.Run(() => ConfigReader.Update(mutate));
        _ = updateTask.ContinueWith(
            t => logger?.LogError(t.Exception?.InnerException ?? t.Exception, faultMessage),
            TaskContinuationOptions.OnlyOnFaulted);
        await updateTask;
    }

    private static void OpenFolder(string exe, string args, ILogger? logger)
    {
        System.Diagnostics.Process? proc = null;
        try
        {
            proc = System.Diagnostics.Process.Start(exe, args);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to open: {Exe} {Args}", exe, args);
        }
        finally
        {
            proc?.Dispose();
        }
    }
}
