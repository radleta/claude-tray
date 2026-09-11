using System.Drawing;
using System.Windows.Forms;
using Imrdy.Core.Desktop;
using Imrdy.Core.Display;
using Imrdy.Core.Hooks;
using Imrdy.Windows.Models;
using Imrdy.Windows.Overlay;
using Microsoft.Extensions.Logging;

namespace Imrdy.Windows.Dashboard;

/// <summary>
/// Session-domain hover dashboard controller. Derives from
/// <see cref="HoverDashboardControllerBase"/> and provides session-specific:
/// hit-testing (filters by <see cref="DisplayItemType.Session"/>),
/// view-model building (<see cref="LiveDashboardVmBuilder.BuildForSession"/>),
/// form creation (<see cref="SessionDashboardForm"/>), async git fetch,
/// and live-refresh throttle.
///
/// Fade animation (+0.5/-0.5 per tick, 200ms per direction) is driven by the
/// base <see cref="HoverDashboardControllerBase.OnDrainTick"/> call from TrayApp.
/// </summary>
internal sealed class SessionHoverDashboardController : HoverDashboardControllerBase
{
    private readonly HookAccumulationStore _hookAccumulationStore;
    private readonly Func<IReadOnlyList<SessionEntry>> _sessionSource;
    private readonly GitInfoCache _gitCache;

    // Session id of the session whose icon triggered the current show; null when form is hidden.
    // Tracks the session identity for async git-fetch continuations that need it after form close.
    private string? _hoveredSessionId;

    public SessionHoverDashboardController(
        OverlayPanel overlayWindow,
        IDesktopManager? desktopManager,
        ILoggerFactory loggerFactory,
        HookAccumulationStore store,
        Func<IReadOnlyList<SessionEntry>> sessionSource,
        GitInfoCache gitCache)
        : base(overlayWindow, desktopManager, loggerFactory)
    {
        _hookAccumulationStore = store;
        _sessionSource = sessionSource;
        _gitCache = gitCache;
        _logger.LogDebug(
            "SessionHoverCtrl: ctor complete overlay-window-ref={Handle}",
            overlayWindow.Handle);
    }

    // ---- Abstract overrides ----

    protected override bool TryHitTestForOurDomain(int clientX, out DisplayItem? item, out int hitIndex)
    {
        if (!_overlayWindow.TryHitTestAtClient(clientX, out item, out hitIndex))
            return false;

        if (item is null || item.ItemType != DisplayItemType.Session)
        {
            item = null;
            hitIndex = -1;
            return false;
        }

        return true;
    }

    protected override object? BuildViewModel(DisplayItem item)
    {
        var sessions = _sessionSource();
        var entry = sessions.FirstOrDefault(e => e.SessionId == item.Id);
        if (entry is null)
        {
            _logger.LogDebug(
                "SessionHoverCtrl: BuildViewModel — no SessionEntry for {SessionId}; available=[{Available}]",
                item.Id,
                string.Join(",", sessions.Select(s => s.SessionId)));
            return null;
        }

        var cachedGit = _gitCache.TryGetCached(entry.State.Cwd);
        return LiveDashboardVmBuilder.BuildForSession(
            entry, _hookAccumulationStore, cachedGit, sessions, DateTimeOffset.UtcNow);
    }

    protected override Form CreateForm(object viewModel)
    {
        var vm = (DashboardViewModel)viewModel;
        return new SessionDashboardForm(vm, _desktopManager, _loggerFactory, isPinned: false, isPreviewMode: false);
    }

    protected override void ShowForm(HoverDashboardFormBase form, object viewModel)
    {
        ((SessionDashboardForm)form).Show((DashboardViewModel)viewModel);
    }

    protected override void ApplyViewModelUpdate(HoverDashboardFormBase form, object viewModel)
    {
        ((SessionDashboardForm)form).Update((DashboardViewModel)viewModel);
    }

    // ---- Extension-point override ----

    protected override void OnSameItemRefreshTick(DisplayItem currentItem)
    {
        _logger.LogDebug("SessionHoverCtrl: throttled-refresh sessionId={SessionId}", currentItem.Id);
        RebuildAndApplyUpdate(currentItem.Id);
    }

    // ---- Form-hidden: clear session tracking ----

    protected override void OnFormHidden()
    {
        _hoveredSessionId = null;
        _logger.LogDebug("SessionHoverCtrl: OnFormHidden — _hoveredSessionId cleared");
    }

    // ---- Post-show: async git fetch ----

    protected override void OnFormShown(DisplayItem item, object viewModel, Point cursor)
    {
        _hoveredSessionId = item.Id;

        var sessions = _sessionSource();
        var entry = sessions.FirstOrDefault(e => e.SessionId == item.Id);
        if (entry is null) return;

        // A remote session's cwd describes a directory on the publisher, so there is nothing
        // here to read: git would run against whatever path happens to exist on this machine,
        // and a UNC value would make the receiver authenticate to a host the peer chose.
        var cachedGit = _gitCache.TryGetCached(entry.State.Cwd);
        if (cachedGit is not null
            || string.IsNullOrEmpty(entry.State.Cwd)
            || entry.State.OriginMachine is not null) return;

        // Git info not cached — kick off async fetch. Marshal back to UI thread via
        // _overlayWindow (long-lived stable control) to avoid cross-thread race on the form.
        var sessionId = item.Id;
        var cwd = entry.State.Cwd;
        Task.Run(() => _gitCache.FetchAndStore(cwd))
            .ContinueWith(_ =>
            {
                _overlayWindow.BeginInvoke(() =>
                {
                    if (_hoveredSessionId != sessionId) return;

                    var newGit = _gitCache.TryGetCached(cwd);
                    if (newGit is null) return;

                    var currentEntry = _sessionSource().FirstOrDefault(e => e.SessionId == sessionId);
                    if (currentEntry is null) return;

                    var updatedVm = LiveDashboardVmBuilder.BuildForSession(
                        currentEntry, _hookAccumulationStore, newGit, _sessionSource(), DateTimeOffset.UtcNow);
                    _logger.LogDebug("SessionHoverCtrl: git async update arrived for {SessionId}, branch={Branch}",
                        sessionId, newGit.Branch);
                    UpdateCurrentForm(updatedVm);
                });
            });
    }

    // ---- Private helpers ----

    /// <summary>
    /// Rebuilds the <see cref="DashboardViewModel"/> for <paramref name="sessionId"/> and
    /// applies it to the currently-visible form via <see cref="SessionDashboardForm.Update"/>.
    /// Used for live-session-switch (Path B switch-detection) and throttled live-refresh.
    /// Kicks off async git fetch if info is not yet cached.
    /// </summary>
    private void RebuildAndApplyUpdate(string sessionId)
    {
        var sessions = _sessionSource();
        var entry = sessions.FirstOrDefault(e => e.SessionId == sessionId);
        if (entry is null)
        {
            _logger.LogDebug("SessionHoverCtrl: RebuildAndApplyUpdate aborted — no SessionEntry for {SessionId}", sessionId);
            return;
        }

        var cachedGit = _gitCache.TryGetCached(entry.State.Cwd);
        var vm = LiveDashboardVmBuilder.BuildForSession(
            entry, _hookAccumulationStore, cachedGit, sessions, DateTimeOffset.UtcNow);
        UpdateCurrentForm(vm);

        if (cachedGit is not null
            || string.IsNullOrEmpty(entry.State.Cwd)
            || entry.State.OriginMachine is not null) return;

        // Kick off async git fetch if not yet cached — same pattern as OnFormShown, remote
        // sessions included: their cwd is a path on the publisher.
        var cwd = entry.State.Cwd;
        Task.Run(() => _gitCache.FetchAndStore(cwd))
            .ContinueWith(_ =>
            {
                _overlayWindow.BeginInvoke(() =>
                {
                    if (_hoveredSessionId != sessionId) return;

                    var newGit = _gitCache.TryGetCached(cwd);
                    if (newGit is null) return;

                    var currentEntry = _sessionSource().FirstOrDefault(e => e.SessionId == sessionId);
                    if (currentEntry is null) return;

                    var updatedVm = LiveDashboardVmBuilder.BuildForSession(
                        currentEntry, _hookAccumulationStore, newGit, _sessionSource(), DateTimeOffset.UtcNow);
                    _logger.LogDebug("SessionHoverCtrl: git async update (refresh) arrived for {SessionId}, branch={Branch}",
                        sessionId, newGit.Branch);
                    UpdateCurrentForm(updatedVm);
                });
            });
    }
}
