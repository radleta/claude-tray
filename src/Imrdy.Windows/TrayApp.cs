using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Imrdy.Core;
using Imrdy.Core.Desktop;
using Imrdy.Core.Diagnostics;
using Imrdy.Core.Display;
using Imrdy.Core.Graphics;
using Imrdy.Core.Hooks;
using Imrdy.Core.Icons;
using Imrdy.Core.Menus;
using Imrdy.Core.Publishing;
using Imrdy.Core.Sound;
using Imrdy.Core.State;
using Imrdy.Core.Status;
using Imrdy.Core.Tooltip;
using Imrdy.Core.Workspace;
using Imrdy.Windows.Connections;
using Imrdy.Windows.Dashboard;
using Imrdy.Windows.Desktop;
using Imrdy.Windows.Diagnostics;
using Imrdy.Windows.Icons;
using Imrdy.Windows.Interaction;
using Imrdy.Windows.Menus;
using Imrdy.Windows.Models;
using Imrdy.Windows.Notifications;
using Imrdy.Windows.Overlay;
using Imrdy.Windows.Sound;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Imrdy.Windows;

/// <summary>
/// WinForms ApplicationContext that manages the system tray monitor.
/// Owns FileSystemWatchers, debounce/sweep/stale timers, and session/workspace lifecycle.
/// </summary>
internal sealed class TrayApp : ApplicationContext, ISessionInteractionRouter, IConnectionsHost
{
    private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly StateFileReader _stateReader;
    private readonly WorkspaceStore _workspaceStore;
    private readonly CooldownTracker _cooldownTracker;
    private readonly NotificationDwellState _dwellState;
    private readonly PackLoader _packLoader;
    private readonly IDesktopManager _desktopManager;
    private readonly MonitorOptions _options;
    private readonly Dictionary<string, ITrayIconRenderer> _rendererCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly TrayIconRendererFactory _rendererFactory;
    private readonly GraphicsPackLoader _graphicsPackLoader;
    private string _currentIconStyle;
    private OverlayPanel? _overlayPanel;
    private bool _overlayEnabled;
    private OverlayConfig _overlayConfig = new();
    private bool _overlayReloadDeferred;
    private readonly ContextMenuStrip _overlayMenu;

    // Guards against re-subscribing ContextMenuStrip.Closed on every ShowContextMenuAt call.
    // Session/workspace menus are built once per entry (CreateSessionIcon/CreateWorkspaceIcon)
    // and _overlayMenu is a single ctor-built instance, but ShowContextMenuAt runs on every
    // right-click over that same long-lived instance — a plain subscribe-on-every-call would
    // leak handlers and produce duplicate log lines that grow unboundedly across a session's
    // lifetime. ConditionalWeakTable ties the "already wired" marker to the ContextMenuStrip's
    // own GC lifetime, so a session/workspace ending and its Menu being disposed needs no
    // explicit removal bookkeeping here — the table entry is collected along with the menu.
    private readonly ConditionalWeakTable<ContextMenuStrip, object> _menuLifecycleWired = new();

    // Foreground HWND to restore once an AtControl-anchored menu closes (overlay right-click).
    // Showing that menu requires granting the overlay real foreground for the menu's lifetime
    // (see ShowContextMenuAt), which steals it from whatever the user was working in
    // (typically their terminal). Captured by CaptureForegroundForRestore() immediately before
    // that grant happens — the last point GetForegroundWindow() still reports the window about
    // to be replaced, NOT read from OverlayPanel (which carries no activation exception; see
    // OverlayPanel.WndProc). This field is read by the single Closed handler wired per menu
    // instance in ShowContextMenuAt to restore it once the menu closes, then cleared to
    // IntPtr.Zero so a later Closed firing (a different anchor mode, or no capture at all)
    // can't resurrect a stale HWND. Deliberately a single instance field, not per-menu state —
    // only one ContextMenuStrip is ever open at a time in this app.
    private IntPtr _pendingForegroundRestore = IntPtr.Zero;

    // Last-known-good foreground-restore candidate, persisted ACROSS menu opens (unlike
    // _pendingForegroundRestore above, which is per-invocation and cleared on every read).
    // Kept fresh two ways, both gated by PInvokeWindow.IsAcceptableForegroundCandidate:
    //   1. SampleForegroundForRestoreTracking(), piggybacked on the existing 100ms drain
    //      tick (OnDrainTimerTick) — samples GetForegroundWindow() every tick regardless of
    //      menu activity. This is the primary source in practice: by the time a user
    //      actually right-clicks the overlay, some other imrdy-owned window (a dashboard,
    //      a previous menu's transient popup) has very often already stolen foreground, so
    //      a click-time-only capture sees ownProcess=true and has nothing legitimate to
    //      fall back to. Continuous sampling means this field almost always holds a window
    //      that was genuinely foreground within the last ~100ms.
    //   2. CaptureForegroundForRestore(), called immediately before an AtControl menu grants
    //      the overlay foreground — the freshest possible signal for the (less common) case
    //      where the click itself is the first legitimate foreground change since the last
    //      tick.
    // A rejected capture from either source falls back to reading this field instead of
    // overwriting it with garbage — see both methods below.
    private IntPtr _lastGoodForegroundWindow = IntPtr.Zero;

    private bool _trayEnabled = true;
    private readonly BalloonTipManager _balloonTipManager;
    private readonly WorkspaceVisibility _workspaceVisibility = new();
    private readonly WinFormsSoundPlayer _soundPlayer = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private InspectIpcServer? _inspectIpcServer;

    // --- Cross-machine publish/receive (D30, D9) ---
    // The tray IS the Windows publisher; there is no headless Windows publisher. It runs off
    // the session watcher and drain tick it already owns rather than a second FileSystemWatcher
    // on the same directory, which would double every event.
    private readonly PublisherStore _publisherStore;
    private readonly SessionChangeQueue _publishQueue = new();
    private string _machineName = Environment.MachineName;
    private SinkRegistry? _sinkRegistry;
    private SessionPublisher? _sessionPublisher;
    private WireListener? _wireListener;

    /// <summary>
    /// The file sink's liveness signal (r-3). A TCP publisher's socket is its own heartbeat;
    /// a file-sink publisher has no connection, so it writes a beat instead and this reads it.
    /// Refreshed from the aging tick, resolved at render time, never persisted.
    /// </summary>
    private readonly HeartbeatWatch _heartbeats = new(ImrdyPaths.Heartbeats);

    /// <summary>D26's connections window. Created once on first open and reused; hidden on close, never disposed until shutdown.</summary>
    private ConnectionsForm? _connectionsForm;
    private NetworkConfig _networkConfig = new();

    // Guards the fire-and-forget drain so only one batch is in flight: the drain tick is the
    // UI thread and a TCP sink's write must never run on it.
    private int _publishDrainInFlight;
    private readonly Dictionary<string, ShuffleBag<string>> _soundBags = new();

    private bool _soundEnabled = true;
    private SoundConfig _soundConfig = new();
    private IReadOnlyList<PackLoader.LoadedPack> _loadedPacks = [];

    private NotifyIcon _controllerIcon = null!;

    private readonly Dictionary<string, SessionEntry> _sessions = new();
    private readonly Dictionary<string, WorkspaceSessionEntry> _workspaces = new();

    private readonly ConcurrentQueue<string> _pendingChanges = new();

    private FileSystemWatcher? _sessionWatcher;
    private FileSystemWatcher? _workspaceWatcher;
    private FileSystemWatcher? _configWatcher;

    private System.Windows.Forms.Timer? _drainTimer;
    private System.Windows.Forms.Timer? _sweepTimer;
    private System.Windows.Forms.Timer? _staleTimer;
    private System.Windows.Forms.Timer? _agingTimer;

    private volatile bool _disposed;

    // Dev-build preview-dashboard processes launched from Manage → Dev menu.
    // Tracked so the same menu's Close-All item can kill unreachable preview windows
    // without the user dropping to a PowerShell one-liner. Prod builds never populate.
    private readonly List<System.Diagnostics.Process> _previewProcesses = new();
    private readonly object _previewProcessesLock = new();

    private readonly HookAccumulationStore _hookAccumulationStore;
    private readonly GitInfoCache _gitCache;
    private SessionHoverDashboardController? _hoverController;
    private WorkspaceHoverDashboardController? _workspaceHoverController;
    // F6: one-shot null-controller warning guard — prevents log spam when controller is absent.
    // Reset to false whenever _hoverController becomes non-null so each absence is reported once.
    private bool _loggedNullHoverControllerWarning;

    /// <summary>
    /// True during initial sweep — suppresses toasts and sounds until first sweep completes.
    /// Checked by icon/sound/notification logic in Steps 9+.
    /// </summary>
    public bool IsBootstrapping { get; private set; } = true;

    public TrayApp(
        ILoggerFactory loggerFactory,
        StateFileReader stateReader,
        WorkspaceStore workspaceStore,
        CooldownTracker cooldownTracker,
        NotificationDwellState dwellState,
        PackLoader packLoader,
        IDesktopManager desktopManager,
        MonitorOptions options,
        TrayIconRendererFactory rendererFactory,
        GraphicsPackLoader graphicsPackLoader,
        PublisherStore publisherStore)
    {
        _logger = loggerFactory.CreateLogger<TrayApp>();
        _loggerFactory = loggerFactory;
        _stateReader = stateReader;
        _workspaceStore = workspaceStore;
        _cooldownTracker = cooldownTracker;
        _dwellState = dwellState;
        _packLoader = packLoader;
        _desktopManager = desktopManager;
        _options = options;
        _rendererFactory = rendererFactory;
        _graphicsPackLoader = graphicsPackLoader;
        _publisherStore = publisherStore;
        _hookAccumulationStore = new HookAccumulationStore();
        _gitCache = new GitInfoCache(loggerFactory);
        _currentIconStyle = StyleNames.NormalizeStyleName(ConfigReader.Read().Tray.IconStyle) ?? "circles";
        _balloonTipManager = new BalloonTipManager(loggerFactory)
        {
            Disabled = _options.NoToast,
            OnToastClicked = OnToastClicked,
        };

        // --silent overrides config
        if (_options.Silent)
        {
            _soundEnabled = false;
        }

        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        Application.ApplicationExit += OnApplicationExit;
        SetConsoleCtrlHandler(OnConsoleCtrl, true);
        ListenForStopSignal();

        _controllerIcon = new NotifyIcon
        {
            Icon = new Icon(typeof(TrayApp), "Resources.imrdy.ico"),
            Text = "imrdy",
            Visible = true,
            ContextMenuStrip = ControllerMenuBuilder.Create(
                GetControllerState,
                OnConfigChanged,
                sessionId => ActivateSession(sessionId),
                workspacePath => ActivateWorkspace(workspacePath),
                () => ExitThread(),
                LaunchPreview,
                CloseAllPreviews,
                ShowConnectionsWindow,
                _logger),
        };

        // UI marshaler: force the controller menu's native window handle to exist
        // on the current (UI) thread so BeginInvoke from background threads (toast
        // activation, etc.) works even when the user never opens the menu. Without
        // this, Control.BeginInvoke silently fails for overlay-only users whose
        // ContextMenuStrip is never shown.
        _ = _controllerIcon.ContextMenuStrip!.Handle;

        _overlayMenu = OverlayMenuBuilder.Create(GetControllerState, OnConfigChanged, _logger);
        _ = _overlayMenu.Handle;

        StartIpcServer();

        InitializeDirectories();
        LoadSoundConfig();
        InitializeWatchers();
        InitializeTimers();

        var startupConfig = ConfigReader.Read();
        _trayEnabled = startupConfig.Tray.Enabled;
        InitializePublishing(startupConfig);
        var overlayConfig = startupConfig.Overlay;
        _overlayEnabled = overlayConfig.Enabled;
        _overlayConfig = overlayConfig;
        if (_overlayEnabled)
        {
            try
            {
                _overlayPanel = CreateOverlay(overlayConfig);
                _overlayPanel.Show();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create overlay window — overlay disabled");
                _overlayPanel = null;
                _overlayEnabled = false;
            }
        }

        if (_overlayPanel is not null)
        {
            _hoverController = new SessionHoverDashboardController(
                _overlayPanel,
                _desktopManager,
                _loggerFactory,
                _hookAccumulationStore,
                () => _sessions.Values.ToList(),
                _gitCache);
            _loggedNullHoverControllerWarning = false; // F6: reset so next absence is reported
            _workspaceHoverController = new WorkspaceHoverDashboardController(
                _overlayPanel,
                _desktopManager,
                _loggerFactory,
                _workspaceStore,
                GetWorkspaceLastSeen,
                () => _desktopManager?.GetCurrentDesktopIndex(),
                _gitCache);
            _overlayPanel.SurfaceInteracted += _hoverController.HandleSurfaceInteraction;
            _overlayPanel.SurfaceInteracted += _workspaceHoverController.HandleSurfaceInteraction;
            _overlayPanel.DragCompleted += HandleOverlayDragCompleted;
            _hoverController.FormShown += _workspaceHoverController.HideIfVisible;
            _workspaceHoverController.FormShown += _hoverController.HideIfVisible;
            _logger.LogDebug("TrayApp: subscribed _hoverController and _workspaceHoverController to _overlayPanel.SurfaceInteracted + DragCompleted + cross-controller FormShown");
        }

        // Initial load to pick up existing sessions
        BootstrapSessions();
        IsBootstrapping = false;

        _logger.LogInformation("TrayApp started — monitoring {Dir} (NotifyIcon reflection: {Avail})",
            ImrdyPaths.Sessions, NotifyIconMenuHost.ReflectionAvailable);
    }

    private void InitializeDirectories()
    {
        Directory.CreateDirectory(ImrdyPaths.Sessions);
    }

    private void InitializeWatchers()
    {
        // Watch session state files
        _sessionWatcher = new FileSystemWatcher(ImrdyPaths.Sessions, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 65536, // 64KB — default 8KB can overflow with rapid writes
            EnableRaisingEvents = true,
        };
        _sessionWatcher.Changed += OnSessionFileChanged;
        _sessionWatcher.Created += OnSessionFileChanged;
        _sessionWatcher.Deleted += OnSessionFileDeleted;
        _sessionWatcher.Error += (_, err) =>
            _logger.LogWarning(err.GetException(), "Session FileSystemWatcher error");

        // Watch workspaces.json
        _workspaceWatcher = new FileSystemWatcher(ImrdyPaths.Home, "workspaces.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _workspaceWatcher.Changed += OnWorkspaceFileChanged;
        _workspaceWatcher.Created += OnWorkspaceFileChanged;

        // Watch config.json for live reload (Created+Changed only — NOT Deleted)
        Directory.CreateDirectory(ImrdyPaths.Home);
        _configWatcher = new FileSystemWatcher(ImrdyPaths.Home, "config.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        _configWatcher.Changed += OnConfigFileChanged;
        _configWatcher.Created += OnConfigFileChanged;
        // NO _configWatcher.Deleted subscription — atomic write briefly deletes the file
    }

    private void InitializeTimers()
    {
        // Drain timer: processes FSW events on UI thread (100ms)
        _drainTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _drainTimer.Tick += OnDrainTimerTick;
        _drainTimer.Tick += OnHoverDrainTick;
        _drainTimer.Start();

        // Cleanup timer: removes sessions whose state files are gone (10s)
        _sweepTimer = new System.Windows.Forms.Timer { Interval = 10_000 };
        _sweepTimer.Tick += OnCleanupTimerTick;
        _sweepTimer.Start();

        // Stale timer: removes old sessions (60s)
        _staleTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _staleTimer.Tick += OnStaleTimerTick;
        _staleTimer.Start();

        // One read before the first aging tick, so a session icon created in the next five
        // seconds is not blind to a publisher that is already gone.
        _heartbeats.Refresh();

        // Aging timer: fades icons over time (5s)
        _agingTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _agingTimer.Tick += OnAgingTimerTick;
        _agingTimer.Start();
    }

    // --- Cross-machine publish / receive ---

    /// <summary>
    /// Wires the tray as this machine's publisher (D30) and, when configured, as a receiver (D9).
    /// <para>
    /// Deliberately no <see cref="SessionDirectoryWatcher"/> and no second timer: the tray already
    /// watches <see cref="ImrdyPaths.Sessions"/> and drains every 100ms, and a second watcher on
    /// that directory would double every event. The Core watcher exists for the daemon, which has
    /// none of this.
    /// </para>
    /// </summary>
    private void InitializePublishing(ImrdyConfig config)
    {
        _networkConfig = config.Network;
        _machineName = MachineNameResolver.Resolve(config.Network.MachineName, Environment.MachineName, null);

        // AuthKey and the snapshot are resolved lazily so a config edit or a session that started
        // after the tray did is visible to the next dial rather than to the next restart.
        var sinkContext = new SinkContext(
            () => _machineName,
            () => ConfigReader.Read().Network.AuthKey,
            _stateReader,
            () => _stateReader.ReadAllStateFiles(ImrdyPaths.Sessions)
                .Where(SessionPublisher.IsLocallyOwned)
                .ToList());

        _sinkRegistry = new SinkRegistry(_publisherStore, sinkContext, _logger);
        _sessionPublisher = new SessionPublisher(ImrdyPaths.Sessions, _stateReader, _sinkRegistry.Current, _logger);

        StartWireListener(config.Network);

        // A TCP sink sends its own snapshot when it dials (D12); a file sink has no connect
        // moment, so this is what gets already-running sessions to a mount at tray start.
        if (_sinkRegistry.Current().Count > 0)
        {
            var publisher = _sessionPublisher;
            var token = _shutdownCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await publisher.PublishSnapshotAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Shutdown.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Publishing the startup session snapshot failed");
                }
            }, CancellationToken.None);
        }
    }

    private void StartWireListener(NetworkConfig network)
    {
        if (!network.ListenEnabled)
        {
            return;
        }

        // The listener writes into the very directory this tray watches, so every ingest comes
        // back round as a local Changed event — that is D14 working, not a loop. What stops it
        // re-publishing is D5's null-origin guard in SessionPublisher.IsLocallyOwned: an ingested
        // file carries origin_machine, so no sink ever sees it again.
        var listener = new WireListener(
            network.ListenPort,
            () => ConfigReader.Read().Network.AuthKey,
            new SessionIngest(ImrdyPaths.Sessions, _stateReader),
            _logger);

        if (listener.Start(_shutdownCts.Token))
        {
            _wireListener = listener;
        }
        else
        {
            listener.Dispose();
        }
    }

    /// <summary>
    /// Applies a live config reload to the listener. Only a change to the port or to whether we
    /// listen at all needs the socket rebound; the auth key is read per connection already.
    /// </summary>
    private void ApplyNetworkConfig(NetworkConfig network)
    {
        var rebind = network.ListenEnabled != _networkConfig.ListenEnabled
                     || network.ListenPort != _networkConfig.ListenPort;

        _networkConfig = network;

        // D25 covers the whole network section, machineName included. Every sink resolves the
        // origin through SinkContext rather than capturing it, so re-resolving here is the whole
        // change: without it a renamed machine kept stamping origin_machine with the old name
        // until restart, and the far end saw one machine under two.
        _machineName = MachineNameResolver.Resolve(network.MachineName, Environment.MachineName, null);

        if (!rebind)
        {
            return;
        }

        _wireListener?.Dispose();
        _wireListener = null;
        StartWireListener(network);

        _logger.LogInformation(
            "Inbound publishing {State} on port {Port}",
            network.ListenEnabled ? "enabled" : "disabled",
            network.ListenPort);
    }

    /// <summary>
    /// Emits one coalesced batch of local session changes off the UI thread. Called from the
    /// existing 100ms drain tick; a batch already in flight means this tick does nothing and the
    /// events wait for the next one.
    /// </summary>
    private void PumpPublishing()
    {
        var publisher = _sessionPublisher;
        if (publisher is null || _publishQueue.PendingCount == 0)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _publishDrainInFlight, 1, 0) != 0)
        {
            return;
        }

        var batch = _publishQueue.Drain();
        var token = _shutdownCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await publisher.DrainAsync(batch, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Publishing a session change batch failed");
            }
            finally
            {
                Interlocked.Exchange(ref _publishDrainInFlight, 0);
            }
        }, CancellationToken.None);
    }

    // --- Sound Config ---

    private void LoadSoundConfig()
    {
        try
        {
            var config = ConfigReader.Read();
            _soundConfig = config.Sound;
            _soundEnabled = _options.Silent ? false : _soundConfig.Enabled;
            _loadedPacks = _packLoader.LoadPacks(ImrdyPaths.PacksDir);
            _soundBags.Clear();
            _balloonTipManager.SuppressSystemSound = _loadedPacks.Count > 0 && _soundEnabled;
            _logger.LogDebug("Sound config loaded: enabled={Enabled}, packs={Count}",
                _soundEnabled, _loadedPacks.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load sound config");
        }
    }

    // --- FSW Callbacks (background thread → queue) ---

    private void OnSessionFileChanged(object sender, FileSystemEventArgs e)
    {
        _logger.LogDebug("FSW: {ChangeType} {Path}", e.ChangeType, e.Name);
        _pendingChanges.Enqueue(e.FullPath);
        _publishQueue.Enqueue(Path.GetFileNameWithoutExtension(e.FullPath), SessionChangeKind.Changed);
    }

    private void OnSessionFileDeleted(object sender, FileSystemEventArgs e)
    {
        _pendingChanges.Enqueue($"DELETE:{e.FullPath}");
        _publishQueue.Enqueue(Path.GetFileNameWithoutExtension(e.FullPath), SessionChangeKind.Removed);
    }

    private void OnWorkspaceFileChanged(object sender, FileSystemEventArgs e)
    {
        _pendingChanges.Enqueue("WORKSPACE_RELOAD");
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        _pendingChanges.Enqueue("CONFIG_RELOAD");
    }

    // --- Timer Callbacks (UI thread) ---

    private void OnDrainTimerTick(object? sender, EventArgs e)
    {
        try
        {
            // Piggyback the continuous foreground-restore-candidate sampler on this existing
            // 100ms tick rather than adding a dedicated timer (same reuse pattern as
            // NotificationDwellState's dwell check). Runs first, unconditionally, every tick.
            SampleForegroundForRestoreTracking();

            // Cross-machine publish rides this same tick rather than a timer of its own; the
            // emit itself runs off-thread (see PumpPublishing).
            PumpPublishing();

            if (_overlayReloadDeferred && _overlayPanel?.IsDragging != true)
            {
                _pendingChanges.Enqueue("CONFIG_RELOAD");
                _overlayReloadDeferred = false;
            }

            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (_pendingChanges.TryDequeue(out var item))
            {
                if (!processed.Add(item))
                {
                    continue; // Debounce: skip duplicate events in same drain cycle
                }

                if (item == "CONFIG_RELOAD")
                {
                    try
                    {
                        OnConfigChanged(ConfigReader.Read());
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to live-reload config from file change");
                    }
                }
                else if (item == "WORKSPACE_RELOAD")
                {
                    ReloadWorkspaces();
                }
                else if (item.StartsWith("DELETE:", StringComparison.Ordinal))
                {
                    var path = item[7..];
                    HandleSessionFileDeleted(path);
                }
                else
                {
                    HandleSessionFileChanged(item);
                }
            }

            var now = DateTimeOffset.UtcNow;

            // Effective-status transitions. An idle lead whose stored roster still lists running
            // work displays as "done" (teal); it becomes "idle" (green) once a hook writes an empty
            // roster. Resolve is pure now, so this loop fires only on genuine state changes rather
            // than polling a clock — but it stays, because it is the sole dwell driver for status
            // changes, which keeps teal silent and makes the toast/sound fire on the teal -> green
            // edge.
            foreach (var (sessionId, entry) in _sessions)
            {
                var effective = DisplayStatus.Resolve(entry.State.Status, entry.State.RunningTasks);
                var previousEffective = entry.LastEffectiveStatus;
                if (previousEffective == effective)
                {
                    continue;
                }

                entry.LastEffectiveStatus = effective;
                UpdateSessionIcon(entry);

                if (previousEffective is not null && !IsBootstrapping)
                {
                    _logger.LogDebug("Effective status for {SessionId}: {Previous} → {Effective} (lead={Lead})",
                        sessionId, previousEffective, effective, entry.State.Status);
                    _dwellState.OnStatusChanged(sessionId, effective, previousEffective, now);
                }
            }

            // Dwell notification dispatch
            var fired = _dwellState.GetFiredSessions(now);
            foreach (var notification in fired)
            {
                try
                {
                    if (_sessions.TryGetValue(notification.SessionId, out var firedEntry))
                    {
                        // D22: remote sessions go through exactly the same dwell gate as local
                        // ones — the mute is the escape valve applied here, after the dwell has
                        // already run, so muting silences a machine without changing when its
                        // sessions would have notified.
                        if (IsPublisherMuted(firedEntry))
                        {
                            _logger.LogDebug(
                                "Notification for {SessionId} suppressed — publisher {Machine} is muted",
                                notification.SessionId,
                                firedEntry.State.OriginMachine);
                            continue;
                        }

                        _logger.LogInformation("Dwell fired for {SessionId}: {PreviousStatus} → {Status} (type={NotificationType})",
                            notification.SessionId, notification.PreviousStatus, notification.Status, notification.NotificationType ?? "status-change");

                        // No icon update and no write-back here. State.Status is the lead's
                        // readiness and must stay that way — storing a display value ("done")
                        // would make Resolve stop treating the session as idle, freezing it at
                        // teal forever. Icons are driven by EffectiveStatus in the loop above.

                        // Toast: status-change entries + idle_prompt (the authoritative "genuinely idle" signal)
                        if (notification.NotificationType is null
                            || string.Equals(notification.NotificationType, "idle_prompt", StringComparison.OrdinalIgnoreCase))
                        {
                            _balloonTipManager.OnStatusTransition(
                                firedEntry, notification.PreviousStatus, notification.Status,
                                IsBootstrapping, _desktopManager.GetCurrentDesktopIndex());
                        }

                        // Sound: dispatch via correct path based on entry origin
                        if (notification.NotificationType is not null)
                            TriggerNotificationSound(firedEntry, notification.NotificationType!); // null-forgiving: guarded by is not null check; avoids CS8604 with TreatWarningsAsErrors
                        else
                            TriggerStatusChangeSound(firedEntry, notification.PreviousStatus, notification.Status);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Dwell dispatch failed for {SessionId}", notification.SessionId);
                }
            }

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in drain timer");
        }
    }

    private void OnHoverDrainTick(object? sender, EventArgs e)
    {
        if (_hoverController is null)
        {
            // F6: log once when the tick fires but controller is absent (overlay disabled or
            // between config-change races). Guard prevents repeat spam.
            // Paired-lifecycle invariant: _workspaceHoverController is always created and
            // destroyed alongside _hoverController. When _hoverController is null,
            // _workspaceHoverController is also null, so the early return here correctly
            // skips both controllers. No separate null guard for the workspace controller
            // is needed inside this branch (YAGNI per step spec).
            if (!_loggedNullHoverControllerWarning)
            {
                _loggedNullHoverControllerWarning = true;
                _logger.LogDebug("TrayApp: OnHoverDrainTick fired but _hoverController is null (overlay disabled or between config-change races)");
            }
            return;
        }

        // Drag-in-flight guard (Risk 10): a grip drag shares the mouse surface with the
        // dwell/grace state machine — without this, the cursor sweeping across chips
        // mid-drag could dwell-trigger a dashboard pop. Mirrors the _overlayReloadDeferred
        // pattern — a drag-in-flight flag the hover drain tick honors — wired here per the
        // P6 subscription-ownership rule (controllers/base ctor never self-subscribe).
        if (_overlayPanel is { IsDragging: true })
            return;

        _hoverController.OnDrainTick();
        _workspaceHoverController?.OnDrainTick();

        // Hover highlight poll: push the hovered chip id into the panel so OnPaint
        // can draw the highlight ring. Guard IsHandleCreated — no HWND until Show().
        if (_overlayPanel is { IsHandleCreated: true })
        {
            var screenPt = Cursor.Position;
            int cx = screenPt.X, cy = screenPt.Y;
            PInvokeOverlay.ScreenToClientPoint(_overlayPanel.Handle, ref cx, ref cy);
            _overlayPanel.TryHitTestAtClient(cx, out var hoveredItem, out _);
            _overlayPanel.SetHoveredChipId(hoveredItem?.Id);
        }
    }

    private void OnCleanupTimerTick(object? sender, EventArgs e)
    {
        try
        {
            CleanupGoneSessions();

            // Update controller tooltip with session count
            var count = _sessions.Count;
            _controllerIcon.Text = count > 0 ? $"imrdy \u2014 {count} sessions" : "imrdy";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in cleanup timer");
        }
    }

    private void OnStaleTimerTick(object? sender, EventArgs e)
    {
        try
        {
            CleanupStaleSessions();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in stale timer");
        }
    }

    private void OnAgingTimerTick(object? sender, EventArgs e)
    {
        try
        {
            // The one disk read behind IsPublisherDisconnected's heartbeat branch. It rides
            // this existing 5s tick rather than a timer of its own, and 5s is well inside
            // PublisherHeartbeat.StaleAfter, so nothing is missed by refreshing here.
            _heartbeats.Refresh();

            foreach (var (_, entry) in _sessions)
            {
                if (entry.Icon is null) continue;

                var agingSince = DateTimeOffset.UtcNow - entry.LastSeenAt;
                var newTier = StatusMap.GetAgingTier(agingSince);
                var disconnected = IsPublisherDisconnected(entry);

                // Only update icon if aging tier or link state changed (avoid GDI churn)
                if (newTier != entry.LastAgingTier || disconnected != entry.LastDisconnected)
                {
                    entry.LastAgingTier = newTier;
                    entry.LastDisconnected = disconnected;
                    entry.Icon.Icon = GetRendererForStyle(ResolveSessionIconStyle(entry)).GetIcon(entry.EffectiveStatus, newTier, disconnected);
                }

                // Always update tooltip (status age changes every tick)
                entry.Icon.Text = FormatSessionTooltip(entry);
            }

            RefreshOverlay();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in aging timer");
        }
    }

    // --- Session Lifecycle ---

    private void HandleSessionFileChanged(string filePath)
    {
        var state = _stateReader.ReadStateFile(filePath);
        if (state is null)
        {
            return;
        }

        if (_sessions.TryGetValue(state.SessionId, out var entry))
        {
            // Skip re-processing when sweep re-reads an unchanged state file
            if (entry.LastProcessedTimestamp == state.Timestamp)
            {
                return;
            }

            entry.LastProcessedTimestamp = state.Timestamp;

            var previousStatus = entry.State.Status;
            var statusChanged = previousStatus != state.Status;
            var previousNotificationType = entry.State.NotificationType;
            var notificationChanged = previousNotificationType != state.NotificationType;
            entry.State = state;
            entry.DesktopIndex = state.DesktopIndex;

            if (statusChanged)
            {
                entry.StatusSince = DateTimeOffset.UtcNow;

                // Auto-restore dismissed sessions on attention-worthy status changes
                if (entry.Dismissed && state.Status is "busy" or "attention" or "permission" or "error")
                {
                    entry.Dismissed = false;
                    if (entry.Icon is not null)
                    {
                        entry.Icon.Visible = true;
                    }
                    _logger.LogInformation("Restored dismissed session: {SessionId} (status → {Status})",
                        entry.SessionId, state.Status);
                }

                // Dwell entry is driven by the effective-status loop in OnDrainTimerTick, so
                // that a lead going idle while subagents run settles at teal instead of pinging.
            }

            // Notification-type sounds (only when notification type actually changes)
            if (notificationChanged && !IsBootstrapping && !string.IsNullOrEmpty(state.NotificationType))
            {
                // Map notification type to status for dwell duration lookup
                var mappedStatus = state.NotificationType switch
                {
                    "permission_prompt" or "elicitation_dialog" => "permission",
                    // idle_prompt confirms the lead is waiting, but stays silent while the stored
                    // roster still lists running work — Resolve returns "done" there, which is not
                    // a toast event. idle_prompt carries no roster of its own; the preserved one is
                    // the roster the Stop seconds earlier wrote, in the same turn boundary.
                    "idle_prompt" => DisplayStatus.Resolve("idle", state.RunningTasks),
                    _ => (string?)null,
                };
                if (mappedStatus is not null and not "done")
                {
                    _dwellState.OnStatusChanged(entry.SessionId, mappedStatus, entry.EffectiveStatus,
                        DateTimeOffset.UtcNow, notificationType: state.NotificationType);
                }
            }

            // SessionEnd → start grace period (only once; sweep re-reads must not reset it)
            if (string.Equals(state.HookEvent, "SessionEnd", StringComparison.OrdinalIgnoreCase))
            {
                entry.RemoveAfter ??= DateTimeOffset.UtcNow + GracePeriod;
            }
            else
            {
                entry.RemoveAfter = null;
            }

            UpdateSessionIcon(entry);
            var latencyMs = (int)(DateTimeOffset.UtcNow - state.Timestamp).TotalMilliseconds;
            _logger.LogInformation("Session {SessionId} → {Status} (latency: {Latency}ms)", state.SessionId, state.Status, latencyMs);
        }
        else
        {
            // New session — resolve pack from config/project rules, then persist
            var resolvedPack = state.SoundPack;
            if (string.IsNullOrEmpty(resolvedPack))
            {
                var assignment = new PackAssignment(_loadedPacks, _soundConfig);
                resolvedPack = assignment.Resolve(null, PathNormalizer.DeriveProject(state.Cwd));
            }

            // Map null (no pack resolved) to "" (explicitly none) so it persists
            resolvedPack ??= "";

            // Normalize and validate icon style — reject unknown values (fall back to null = global default)
            var normalizedStyle = StyleNames.NormalizeStyleName(state.IconStyle);
            if (normalizedStyle is not null
                && !StyleNames.BuiltInStyles.Contains(normalizedStyle, StringComparer.OrdinalIgnoreCase)
                && !normalizedStyle.StartsWith("pack:", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Unknown icon style '{Style}' in state file for {SessionId} — ignoring", normalizedStyle, state.SessionId);
                normalizedStyle = null;
            }

            entry = new SessionEntry
            {
                SessionId = state.SessionId,
                State = state,
                SoundPack = resolvedPack,
                DesktopIndex = state.DesktopIndex,
                IconStyle = normalizedStyle,
                LastProcessedTimestamp = state.Timestamp,
                StartedAt = state.StartedAt ?? state.Timestamp,
            };

            // Write resolved pack and icon style to state file so the hook preserves them
            PersistSessionSoundPack(entry);
            PersistSessionIconStyle(entry);

            // WT auto-lock: capture the user's currently active desktop as this session's pinned
            // desktop, but only on the moment of true session launch (SessionStart hook event) and
            // only for Windows Terminal. WT shares one PID/HWND across all tabs/windows in v1.23+,
            // so we cannot dynamically determine which desktop a specific WT session lives on at
            // click time. The user's active desktop at launch is the most reliable proxy.
            // See scratch/wt-desktop-routing/decisions.md (D2, D3) for rationale.
            // Residual race: a concurrent hook write that read DesktopIndex==null before this write
            // lands can clobber the auto-locked value; see .claude/skills/imrdy-expert/tray-hook-write-race.md (D4).
            if (string.Equals(state.HookEvent, "SessionStart", StringComparison.OrdinalIgnoreCase)
                && entry.DesktopIndex is null
                && IsWindowsTerminal(entry))
            {
                var currentDesktop = _desktopManager.GetCurrentDesktopIndex();
                if (currentDesktop.HasValue)
                {
                    entry.DesktopIndex = currentDesktop.Value;
                    PersistSessionDesktopIndex(entry);
                    _logger.LogDebug("Auto-locked WT session {SessionId} to desktop {Desktop} on SessionStart",
                        entry.SessionId, currentDesktop.Value);
                }
            }

            // Session first observed as SessionEnd (tray started mid-session, or hook fired
            // before FSW picked up earlier writes) — start grace period so cleanup removes it.
            // Without this, FSW won't fire again and CleanupGoneSessions never sets RemoveAfter,
            // so the stale state file lingers forever.
            if (string.Equals(state.HookEvent, "SessionEnd", StringComparison.OrdinalIgnoreCase))
            {
                entry.RemoveAfter = DateTimeOffset.UtcNow + GracePeriod;
            }

            _sessions[state.SessionId] = entry;
            CreateSessionIcon(entry);

            // Toast for new session (suppressed during bootstrap)
            _balloonTipManager.OnNewSession(entry, IsBootstrapping);

            _logger.LogInformation("Session started: {SessionId} ({Project})",
                state.SessionId, state.Project);
        }

        // Feed the hook event into the accumulation store so the hover dashboard
        // sees live sparkline/chip/turn-count data. Runs synchronously on the UI thread
        // (same FSW callback). BootstrapSessions intentionally does NOT call Apply —
        // accumulators start empty and refill organically from new events.
        if (!IsBootstrapping && entry is not null)
        {
            var evt = new HookEventModel
            {
                HookEventName = state.HookEvent,
                SessionId = state.SessionId,
                ToolName = state.ToolName,
                NotificationType = state.NotificationType,
            };
            _hookAccumulationStore.Apply(evt, derivedStatus: entry.State.Status);
        }

        // Update workspace visibility after any session change (D11)
        UpdateWorkspaceVisibility();
    }

    private void HandleSessionFileDeleted(string filePath)
    {
        var sessionId = Path.GetFileNameWithoutExtension(filePath);
        if (_sessions.TryGetValue(sessionId, out var entry))
        {
            entry.RemoveAfter = DateTimeOffset.UtcNow + GracePeriod;
            _logger.LogDebug("Session file deleted, grace period started: {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// One-time startup load: reads all existing state files and creates session entries.
    /// Called once during initialization before FSW takes over real-time updates.
    /// </summary>
    private void BootstrapSessions()
    {
        if (!Directory.Exists(ImrdyPaths.Sessions))
        {
            return;
        }

        // Load workspaces BEFORE sessions so ResolveSessionIconStyle has workspace data
        ReloadWorkspaces();

        var stateFiles = _stateReader.ReadAllStateFiles(ImrdyPaths.Sessions);
        foreach (var state in stateFiles)
        {
            HandleSessionFileChanged(Path.Combine(ImrdyPaths.Sessions, $"{state.SessionId}.json"));
        }
    }

    /// <summary>
    /// Periodic cleanup: removes sessions whose state files no longer exist on disk.
    /// Defense-in-depth for FSW Deleted events that may be missed.
    /// Does NOT re-read state files — FSW handles all real-time updates.
    /// </summary>
    private void CleanupGoneSessions()
    {
        var toRemove = new List<string>();
        var now = DateTimeOffset.UtcNow;

        foreach (var (sessionId, entry) in _sessions)
        {
            var stateFile = Path.Combine(ImrdyPaths.Sessions, $"{sessionId}.json");
            if (!File.Exists(stateFile))
            {
                if (entry.RemoveAfter is null)
                {
                    entry.RemoveAfter = now + GracePeriod;
                    _logger.LogDebug("State file gone, grace period started: {SessionId}", sessionId);
                }

                if (now >= entry.RemoveAfter)
                {
                    toRemove.Add(sessionId);
                }
            }

            // Also remove sessions that have passed their grace period (e.g. SessionEnd)
            if (entry.RemoveAfter.HasValue && now >= entry.RemoveAfter.Value)
            {
                toRemove.Add(sessionId);
            }
        }

        foreach (var sessionId in toRemove.Distinct())
        {
            RemoveSession(sessionId);
        }
    }

    private void CleanupStaleSessions()
    {
        var now = DateTimeOffset.UtcNow;
        var toRemove = new List<string>();

        foreach (var (sessionId, entry) in _sessions)
        {
            if (now - entry.State.Timestamp > TimeSpan.FromMinutes(_options.StaleMinutes))
            {
                // Keep session alive if its state file still exists on disk
                var stateFile = Path.Combine(ImrdyPaths.Sessions, $"{sessionId}.json");
                if (File.Exists(stateFile))
                {
                    continue;
                }

                toRemove.Add(sessionId);
                _logger.LogInformation("Removing stale session: {SessionId} (last update {Ago} ago)",
                    sessionId, now - entry.State.Timestamp);
            }
        }

        foreach (var sessionId in toRemove)
        {
            RemoveSession(sessionId);
        }
    }

    private void RemoveSession(string sessionId)
    {
        if (_sessions.Remove(sessionId, out var entry))
        {
            Commands.ProcessResolver.ClearSession(sessionId);
            _dwellState.RemoveSession(sessionId);
            _cooldownTracker.RemoveSession(sessionId);
            entry.Dispose();
            RefreshOverlay();

            // The publisher's removal guard has to answer "was this ours?" after the file is
            // gone, and the watcher event that asks carries only an id. Hand it the origin we
            // are still holding, right here, before the file goes: a remote session the
            // publisher never read — one left on disk across a tray restart and then cleared
            // from the connections window — would otherwise be an unknown, and D4 forbids
            // guessing "ours" on an unknown.
            _sessionPublisher?.RecordOwnership(entry.State);

            // Delete state file so sweep doesn't resurrect the session
            var statePath = Path.Combine(ImrdyPaths.Sessions, $"{sessionId}.json");
            try
            {
                File.Delete(statePath);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Could not delete state file: {Path}", statePath);
            }

            _logger.LogDebug("Session removed: {SessionId}", sessionId);

            // Workspace white dot may reappear now (D11)
            UpdateWorkspaceVisibility();
        }
    }

    /// <summary>
    /// Writes the session's sound_pack to its state file so the hook preserves it.
    /// </summary>
    private void PersistSessionSoundPack(SessionEntry entry)
    {
        PersistSessionField(entry, current => current with { SoundPack = entry.SoundPack });
    }

    /// <summary>
    /// Writes the session's icon_style to its state file so the hook preserves it.
    /// </summary>
    private void PersistSessionIconStyle(SessionEntry entry)
    {
        PersistSessionField(entry, current => current with { IconStyle = entry.IconStyle });
    }

    /// <summary>
    /// Writes the session's desktop_index to its state file so the hook preserves it.
    /// </summary>
    private void PersistSessionDesktopIndex(SessionEntry entry)
    {
        PersistSessionField(entry, current => current with { DesktopIndex = entry.DesktopIndex });
    }

    private void PersistSessionField(SessionEntry entry, Func<StateFileModel, StateFileModel> update)
    {
        var statePath = Path.Combine(ImrdyPaths.Sessions, $"{entry.SessionId}.json");
        try
        {
            var current = _stateReader.ReadStateFile(statePath);
            if (current is not null)
            {
                _stateReader.WriteStateFile(statePath, update(current));
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not persist session field for {SessionId}", entry.SessionId);
        }
    }

    private void ClearAllSessions()
    {
        var sessionIds = _sessions.Keys.ToList();
        foreach (var sessionId in sessionIds)
        {
            var stateFile = Path.Combine(ImrdyPaths.Sessions, $"{sessionId}.json");
            try { if (File.Exists(stateFile)) File.Delete(stateFile); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete state file for {SessionId}", sessionId); }
            RemoveSession(sessionId);
        }
        _logger.LogInformation("Cleared all sessions ({Count} removed)", sessionIds.Count);
    }

    private void DumpState()
    {
        var lines = new List<string>();
        var ts = DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff");
        lines.Add($"{ts} [DUMP] ===== State Dump ({_sessions.Count} sessions) =====");
        foreach (var (sid, entry) in _sessions)
        {
            var d = entry.State;
            var age = (int)(DateTimeOffset.UtcNow - entry.StatusSince).TotalSeconds;
            var seenAge = (int)(DateTimeOffset.UtcNow - entry.LastSeenAt).TotalSeconds;
            var agingTier = entry.LastAgingTier;
            lines.Add($"{ts} [DUMP] {sid[..Math.Min(8, sid.Length)]} project={d.Project} status={d.Status} hook={d.HookEvent} desktop={entry.DesktopIndex} pack={entry.SoundPack} dismissed={entry.Dismissed} statusAge={age}s seenAge={seenAge}s agingTier={agingTier} pid={d.ClaudePid}");

            // Check state file consistency
            var stateFile = Path.Combine(ImrdyPaths.Sessions, $"{sid}.json");
            if (File.Exists(stateFile))
            {
                try
                {
                    var fileState = _stateReader.ReadStateFile(stateFile);
                    if (fileState is not null && fileState.Status != d.Status)
                        lines.Add($"{ts} [DUMP] {sid[..Math.Min(8, sid.Length)]}   FILE MISMATCH: file.status={fileState.Status} vs memory.status={d.Status}");
                }
                catch (Exception ex)
                {
                    lines.Add($"{ts} [DUMP] {sid[..Math.Min(8, sid.Length)]}   FILE READ ERROR: {ex.Message}");
                }
            }
            else
            {
                lines.Add($"{ts} [DUMP] {sid[..Math.Min(8, sid.Length)]}   NO STATE FILE");
            }
        }
        lines.Add($"{ts} [DUMP] ===== End Dump =====");

        try
        {
            File.AppendAllText(ImrdyPaths.MonitorLog, string.Join(Environment.NewLine, lines) + Environment.NewLine);
            _logger.LogInformation("State dumped to {LogPath} ({Count} sessions)", ImrdyPaths.MonitorLog, _sessions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write state dump to {LogPath}", ImrdyPaths.MonitorLog);
        }
    }

    // --- Workspace Lifecycle ---

    private void ReloadWorkspaces()
    {
        try
        {
            var config = _workspaceStore.Load();
            var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var workspace in config.Workspaces)
            {
                var key = workspace.Path.ToUpperInvariant();
                activeKeys.Add(key);

                if (_workspaces.TryGetValue(key, out var existing))
                {
                    existing.Workspace = workspace;
                    existing.IconStyle = StyleNames.NormalizeStyleName(workspace.IconStyle);
                }
                else
                {
                    var wsEntry = new WorkspaceSessionEntry
                    {
                        Workspace = workspace,
                    };
                    wsEntry.IconStyle = StyleNames.NormalizeStyleName(workspace.IconStyle);
                    _workspaces[key] = wsEntry;
                    CreateWorkspaceIcon(wsEntry);
                    _logger.LogDebug("Workspace added: {Name} ({Path})", workspace.Name, workspace.Path);
                }
            }

            // Remove workspaces no longer in config
            var toRemove = _workspaces.Keys.Where(k => !activeKeys.Contains(k)).ToList();
            foreach (var key in toRemove)
            {
                if (_workspaces.Remove(key, out var wsEntry))
                {
                    wsEntry.Dispose();
                    _logger.LogDebug("Workspace removed: {Name}", wsEntry.Workspace.Name);
                }
            }
            UpdateWorkspaceVisibility();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reloading workspaces");
        }
    }

    /// <summary>
    /// Evaluates workspace visibility based on active sessions.
    /// White dots hide when a session is active in that workspace path (D11).
    /// Desktop auto-tracked from latest session; persisted on hidden→visible transition (D22).
    /// </summary>
    private void UpdateWorkspaceVisibility()
    {
        if (_workspaces.Count == 0)
        {
            return;
        }

        var workspaceEntries = _workspaces.Values.Select(ws => ws.Workspace).ToList();
        var activeSessions = _sessions.Values
            .Where(s => !s.Dismissed && s.State.Status != "end")
            .Select(s => s.State)
            .ToList();

        var results = _workspaceVisibility.Evaluate(workspaceEntries, activeSessions);

        foreach (var result in results)
        {
            var key = result.Workspace.Path.ToUpperInvariant();
            if (!_workspaces.TryGetValue(key, out var wsEntry))
            {
                continue;
            }

            var wasVisible = wsEntry.Visible;
            wsEntry.Visible = result.IsVisible;

            if (wsEntry.Icon is not null)
            {
                wsEntry.Icon.Visible = _trayEnabled && result.IsVisible;
            }

            // Persist desktop on hidden→visible transition (D22)
            if (result.DesktopChanged)
            {
                wsEntry.Workspace = wsEntry.Workspace with { Desktop = result.TrackedDesktop };
                try
                {
                    _workspaceStore.Pin(result.Workspace.Path, result.Workspace.Name, result.TrackedDesktop);
                    _logger.LogDebug("Workspace {Name} desktop updated to {Desktop} on reappear",
                        result.Workspace.Name, result.TrackedDesktop);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to persist workspace desktop for {Name}", result.Workspace.Name);
                }
            }
        }
        RefreshOverlay();
    }

    // --- Icon Management ---

    private ITrayIconRenderer GetRendererForStyle(string styleName)
    {
        if (_rendererCache.TryGetValue(styleName, out var cached))
            return cached;
        var renderer = _rendererFactory.Create(styleName);
        _rendererCache[styleName] = renderer;
        return renderer;
    }

    /// <summary>
    /// Resolves the effective icon style for a session: session override → workspace override → global default.
    /// </summary>
    private string ResolveSessionIconStyle(SessionEntry entry)
    {
        var sessionStyle = StyleNames.NormalizeStyleName(entry.IconStyle);
        if (sessionStyle is not null)
        {
            _logger.LogDebug("IconStyle for {SessionId}: session override → {Style}", entry.SessionId, sessionStyle);
            return sessionStyle;
        }

        // Inherit from workspace if session has no override
        var cwd = entry.State?.Cwd;
        if (!string.IsNullOrEmpty(cwd))
        {
            var key = PathNormalizer.Normalize(cwd).ToUpperInvariant();
            if (_workspaces.TryGetValue(key, out var ws))
            {
                var wsStyle = StyleNames.NormalizeStyleName(ws.IconStyle);
                if (wsStyle is not null)
                {
                    _logger.LogDebug("IconStyle for {SessionId}: workspace override → {Style}", entry.SessionId, wsStyle);
                    return wsStyle;
                }
            }
        }

        _logger.LogDebug("IconStyle for {SessionId}: global default → {Style}", entry.SessionId, _currentIconStyle);
        return _currentIconStyle;
    }

    private DateTimeOffset? GetWorkspaceLastSeen(string path)
    {
        var key = path.ToUpperInvariant();
        return _workspaces.TryGetValue(key, out var entry) ? entry.LastSeenAt : (DateTimeOffset?)null;
    }

    private void CreateSessionIcon(SessionEntry entry)
    {
        var icon = GetRendererForStyle(ResolveSessionIconStyle(entry)).GetIcon(entry.EffectiveStatus, 0, IsPublisherDisconnected(entry));

        entry.Icon = new NotifyIcon
        {
            Visible = _trayEnabled,
            Icon = icon,
            Text = FormatSessionTooltip(entry),
        };

        entry.Menu = SessionMenuBuilder.Create(
            entry,
            new SessionMenuCallbacks(
                OnSwitchDesktop: () => ActivateSession(entry.SessionId),
                OnAssignDesktop: () =>
                {
                    var currentDesktop = _desktopManager.GetCurrentDesktopIndex();
                    if (currentDesktop.HasValue)
                    {
                        entry.DesktopIndex = currentDesktop.Value;
                        PersistSessionDesktopIndex(entry);
                        _logger.LogDebug("Assigned session {SessionId} to desktop {Desktop}", entry.SessionId, currentDesktop.Value);
                    }
                },
                OnSetDesktop: index =>
                {
                    entry.DesktopIndex = index;
                    PersistSessionDesktopIndex(entry);
                    if (index.HasValue)
                    {
                        _desktopManager.SwitchToDesktop(index.Value);
                    }
                },
                OnSetPack: packName =>
                {
                    entry.SoundPack = packName ?? "";
                    PersistSessionSoundPack(entry);
                    _logger.LogDebug("Set session {SessionId} sound pack to {Pack}", entry.SessionId, entry.SoundPack);
                },
                OnSetIconStyle: styleName =>
                {
                    entry.IconStyle = styleName;
                    PersistSessionIconStyle(entry);
                    UpdateSessionIcon(entry);
                },
                OnPinWorkspace: () =>
                {
                    var cwd = entry.State.Cwd;
                    if (string.IsNullOrEmpty(cwd)) return;
                    var name = entry.State.Project ?? Path.GetFileName(cwd) ?? cwd;
                    var desktop = entry.DesktopIndex ?? _desktopManager.GetCurrentDesktopIndex() ?? 0;
                    try
                    {
                        _workspaceStore.Pin(cwd, name, desktop);
                        ReloadWorkspaces();
                        _logger.LogInformation("Pinned workspace from session: {Name} ({Path}) desktop={Desktop}",
                            name, cwd, desktop);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to pin workspace for session {SessionId}", entry.SessionId);
                    }
                },
                OnUnpinWorkspace: () =>
                {
                    var cwd = entry.State.Cwd;
                    if (string.IsNullOrEmpty(cwd)) return;
                    try
                    {
                        _workspaceStore.Unpin(cwd);
                        ReloadWorkspaces();
                        _logger.LogInformation("Unpinned workspace from session: {Path}", cwd);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to unpin workspace for session {SessionId}", entry.SessionId);
                    }
                },
                OnClear: () =>
                {
                    var stateFile = Path.Combine(ImrdyPaths.Sessions, $"{entry.SessionId}.json");
                    try { if (File.Exists(stateFile)) File.Delete(stateFile); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete state file for {SessionId}", entry.SessionId); }
                    RemoveSession(entry.SessionId);
                },
                OnClearAll: () => ClearAllSessions(),
                OnDumpState: () => DumpState(),
                OnExit: () => ExitThread()),
            getInstalledPacks: () => _loadedPacks.Select(p => p.Name).ToList(),
            getInstalledGraphicsPacks: () => _graphicsPackLoader.LoadPacks(ImrdyPaths.GraphicsPacksDir).Select(p => p.Name).ToList(),
            getDesktopCount: () => _desktopManager.GetDesktopCount(),
            getDesktopAvailable: () => _desktopManager.IsAvailable,
            getIsPinned: () => !string.IsNullOrEmpty(entry.State.Cwd) && _workspaceStore.IsPinned(entry.State.Cwd),
            logger: _logger);
        entry.Icon.ContextMenuStrip = entry.Menu;

        entry.Icon.MouseClick += (_, e) =>
        {
            // Route through the same interface as overlay clicks — age reset and icon
            // refresh live inside the router, not duplicated per surface.
            if (e.Button == MouseButtons.Left)
                ActivateSession(entry.SessionId);
            else if (e.Button == MouseButtons.Right)
                OpenSessionMenu(entry.SessionId, MenuAnchor.AtTrayIcon(entry.Icon));
        };

        RefreshOverlay();
    }

    /// <summary>
    /// Called from the toast activation background thread — marshals to UI thread
    /// and routes through the interaction router for age-reset parity with every
    /// other surface. The controller menu's handle is force-created in the ctor
    /// so <c>BeginInvoke</c> works even when the user never opens the menu (see
    /// "UI marshaler" in the ctor).
    /// </summary>
    private void OnToastClicked(string sessionId)
    {
        if (_disposed) return;

        if (_controllerIcon.ContextMenuStrip?.InvokeRequired == true)
        {
            try
            {
                _controllerIcon.ContextMenuStrip.BeginInvoke(() => OnToastClicked(sessionId));
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            return;
        }

        ActivateSession(sessionId);
    }

    /// <summary>
    /// Opens D26's connections window, creating it once and reusing it after. A second open
    /// activates the existing window rather than building another — the window is created
    /// once and shown, activated and hidden, which is the opposite of the hover dashboards'
    /// recreate-and-dispose-per-show lifecycle and is why this holds a field.
    /// </summary>
    private void ShowConnectionsWindow()
    {
        // Recreated when it has been disposed rather than reused blindly: showing a disposed
        // Form throws ObjectDisposedException from Control.CreateHandle, which is a crash in
        // the menu handler instead of a window. Observed live before ConnectionsForm learned
        // to cancel a programmatic close as well as a user one.
        if (_connectionsForm is null || _connectionsForm.IsDisposed)
        {
            _connectionsForm = new ConnectionsForm(this, _logger);
        }

        if (!_connectionsForm.Visible)
        {
            _connectionsForm.Show();
        }

        // A hidden-then-shown window can come back minimized; the operator asked for it, so
        // it is restored and given focus rather than blinking in the taskbar.
        if (_connectionsForm.WindowState == FormWindowState.Minimized)
        {
            _connectionsForm.WindowState = FormWindowState.Normal;
        }

        _connectionsForm.Activate();
    }

    /// <inheritdoc/>
    ConnectionsViewModel IConnectionsHost.BuildViewModel()
    {
        var publishers = _publisherStore.Load();

        // f-filesink-no-socket: the listener knows only TCP publishers, so a file-sink
        // publisher delivering into this machine right now would show nothing here. Names come
        // from the records first and from the sessions those publishers already ingested
        // second — the beat filename is a lossy token and cannot be reversed into a name.
        var heartbeats = HeartbeatMachines.Resolve(
            _heartbeats.Beats,
            publishers.Publishers.Select(entry => (string?)entry.Name)
                .Concat(_sessions.Values.Select(session => session.State?.OriginMachine)));

        return ConnectionsViewModelBuilder.Build(
            publishers,
            _sinkRegistry?.Health() ?? [],
            _wireListener?.Health() ?? [],
            heartbeats,
            _machineName,
            _networkConfig.ListenEnabled,
            _networkConfig.ListenPort,
            !string.IsNullOrEmpty(_networkConfig.AuthKey),
            DateTimeOffset.UtcNow);
    }

    /// <inheritdoc/>
    void IConnectionsHost.SavePublisher(PublisherEntry entry, string? previousName)
    {
        // The dialog hands back a complete record, so this is one write. It used to be an Add
        // plus a setter per field — five atomic write-and-fsync cycles for one operator click,
        // each one a moment where publishers.json held a half-saved record.
        _publisherStore.Upsert(entry);

        // A rename is an upsert under the new name plus a removal of the old one; without the
        // second half the operator ends up with two records for one machine, the stale one still
        // holding its own endpoint, desktop mapping, mute and sink. The sessions that arrived
        // under the old name are left alone — they are still on disk and still that machine's.
        if (previousName is not null
            && !string.Equals(previousName, entry.Name, StringComparison.OrdinalIgnoreCase))
        {
            _publisherStore.Remove(previousName);
            _logger.LogInformation("Connections: renamed link {Previous} → {Name}", previousName, entry.Name);
        }

        _logger.LogInformation("Connections: saved link {Name} → {Endpoint}", entry.Name, entry.Endpoint ?? "(receive-only)");

        // D25 and the acceptance outcome: no restart. The record changed, so the sink set is
        // brought in step now rather than whenever something else happens to reconcile.
        ReconcileSinksOffThread();
    }

    /// <inheritdoc/>
    void IConnectionsHost.RemovePublisher(string name)
    {
        _publisherStore.Remove(name);
        ClearSessionsFromMachine(name);
        _logger.LogInformation("Connections: removed link {Name}", name);
        ReconcileSinksOffThread();
    }

    /// <summary>
    /// Rebuilds the sink set after a publishers.json change, off the UI thread.
    /// <para>
    /// Both callers run on the UI thread, from a connections-window button. Reconciling inline
    /// would dispose every evicted <see cref="TcpSink"/> there, and each of those blocks for up
    /// to its two-second grace window — so removing a handful of links would freeze the tray
    /// icons, the overlay and every menu for as many multiples of two seconds. Nothing waits on
    /// the result: the window's own refresh tick renders the records immediately and picks up
    /// the sinks on a later tick.
    /// </para>
    /// </summary>
    private void ReconcileSinksOffThread()
    {
        var registry = _sinkRegistry;
        if (registry is null)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                registry.Reconcile();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reconciling sinks after a publishers.json change failed");
            }
        });
    }

    /// <inheritdoc/>
    void IConnectionsHost.ClearMachineSessions(string name) => ClearSessionsFromMachine(name);

    /// <summary>
    /// D21's cleanup, in place of the time-based expiry the design refuses: drop every session
    /// this machine delivered. Routed through <see cref="RemoveSession"/> so the icon, the
    /// dwell state, the cooldown and the state file all go together — a bare file delete would
    /// leave the tray icon behind until the next sweep.
    /// </summary>
    private void ClearSessionsFromMachine(string machineName)
    {
        var doomed = _sessions.Values
            .Where(s => string.Equals(s.State?.OriginMachine, machineName, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.SessionId)
            .ToList();

        foreach (var sessionId in doomed)
        {
            RemoveSession(sessionId);
        }

        _logger.LogInformation("Connections: cleared {Count} session(s) from {Machine}", doomed.Count, machineName);
    }

    /// <summary>
    /// D22's escape valve: whether this session's publisher is muted. A local session has no
    /// publisher and is never muted by this route.
    /// </summary>
    private bool IsPublisherMuted(SessionEntry entry) =>
        entry.State.OriginMachine is string origin && _publisherStore.Find(origin)?.Muted == true;

    /// <summary>
    /// D20: whether this session's publisher has lost its link. Two signals, one per
    /// transport, in that order.
    /// <para>
    /// <b>A TCP publisher answers from its link.</b> <see cref="WireListener"/> flips a
    /// publisher to <see cref="SinkState.Failed"/> when its connection drops and leaves the
    /// sessions on disk, so a session whose machine reads failed is exactly a session with a
    /// stale last-known state. Where a link exists it stays authoritative — the socket is a
    /// continuous liveness signal and nothing beats it.
    /// </para>
    /// <para>
    /// <b>A file-sink publisher answers from its heartbeat (r-3).</b> It opens no connection,
    /// so it never appears in that table (D27) and for the whole of this build a terminated
    /// WSL distro rendered exactly like a merely-quiet one. It now writes a beat on its own
    /// interval instead, and a stale beat is the file sink's equivalent of a dropped socket.
    /// See <see cref="PublisherHeartbeat"/> for why that is an explicit positive signal rather
    /// than inference from silence, and <see cref="HeartbeatWatch"/> for why a machine that
    /// has never beaten reads as connected rather than as gone.
    /// </para>
    /// <para>
    /// Nothing here is persisted. The answer is recomputed per render, the shape
    /// <c>DisplayStatus.Resolve</c> established: writing it into session state is what froze
    /// sessions at an un-re-derivable value the last time this project inferred liveness.
    /// </para>
    /// </summary>
    private bool IsPublisherDisconnected(SessionEntry entry)
    {
        if (entry.State?.OriginMachine is not string origin) return false;

        if (_wireListener is not null)
        {
            foreach (var link in _wireListener.Health())
            {
                if (string.Equals(link.Name, origin, StringComparison.OrdinalIgnoreCase))
                {
                    return link.State == SinkState.Failed;
                }
            }
        }

        return _heartbeats.IsDisconnected(origin, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// D18: a session that arrived from another machine activates to that publisher's mapped
    /// desktop and stops there. Nothing travels back to the publisher (D4), and the session's own
    /// <c>desktop_index</c> is never the source — the ingest merge already discarded the incoming
    /// number, and the receiver's value means a desktop on *this* box.
    /// <para>
    /// A publisher on the receiver's own box is not remote (D19): a WSL distro here has a real
    /// local terminal worth focusing, so it falls through to the ordinary local path.
    /// </para>
    /// </summary>
    /// <returns>True when the activation was handled as remote and the local path must not run.</returns>
    private bool TrySwitchToRemoteSessionDesktop(SessionEntry entry)
    {
        var origin = entry.State.OriginMachine;
        if (origin is null || MachineNameResolver.IsSameMachine(origin, Environment.MachineName))
        {
            return false;
        }

        var mapped = _publisherStore.Find(origin)?.DesktopIndex;
        if (mapped is null)
        {
            _logger.LogInformation(
                "Focus: session={Sid} is on {Machine}, which has no desktop mapping — nothing to switch to",
                entry.SessionId[..8],
                origin);
            return true;
        }

        if (_desktopManager.IsAvailable)
        {
            _logger.LogInformation(
                "Focus: session={Sid} target=desktop {Target} (source=publisher {Machine})",
                entry.SessionId[..8],
                mapped.Value,
                origin);
            _desktopManager.SwitchToDesktop(mapped.Value);
        }

        return true;
    }

    private void SwitchToSessionDesktop(SessionEntry entry)
    {
        try
        {
            if (TrySwitchToRemoteSessionDesktop(entry))
            {
                return;
            }

            // 1. Resolve target desktop index.
            // Pinned index wins; fall through to dynamic lookup only when unset.
            int? target = entry.DesktopIndex;
            string targetSource = "pinned";

            if (target is null && entry.State.ClaudePid is int claudePidForDesktop)
            {
                var terminalPidForDesktop = Commands.ProcessResolver.ResolveTerminalPid(claudePidForDesktop, entry.SessionId);
                if (terminalPidForDesktop.HasValue)
                {
                    try
                    {
                        var procForDesktop = System.Diagnostics.Process.GetProcessById(terminalPidForDesktop.Value);
                        using (procForDesktop)
                        {
                            var hwndForDesktop = procForDesktop.MainWindowHandle;
                            if (hwndForDesktop != IntPtr.Zero && !IsWindowsTerminal(entry))
                            {
                                target = _desktopManager.GetDesktopForWindow(hwndForDesktop);
                                targetSource = "dynamic";
                            }
                        }
                    }
                    catch (ArgumentException)
                    {
                        Commands.ProcessResolver.ClearSession(entry.SessionId);
                        _logger.LogWarning("Focus: terminal process {Pid} died, cache cleared", terminalPidForDesktop.Value);
                    }
                }
            }

            // 2. Switch desktop first — before any focus attempt.
            if (target.HasValue && _desktopManager.IsAvailable)
            {
                _logger.LogInformation("Focus: session={Sid} target=desktop {Target} (source={Source})",
                    entry.SessionId[..8], target.Value, targetSource);
                _desktopManager.SwitchToDesktop(target.Value);
            }

            // 3. Attempt terminal focus (best-effort; no early return on success).
            if (entry.State.ClaudePid is int claudePid)
            {
                var terminalPid = Commands.ProcessResolver.ResolveTerminalPid(claudePid, entry.SessionId);
                _logger.LogInformation("Focus: session={Sid} claudePid={Claude} terminalPid={Terminal}",
                    entry.SessionId[..8], claudePid, terminalPid);

                if (terminalPid.HasValue)
                {
                    try
                    {
                        var proc = System.Diagnostics.Process.GetProcessById(terminalPid.Value);
                        using (proc)
                        {
                            var hwnd = proc.MainWindowHandle;
                            _logger.LogInformation("Focus: terminal={Name}({Pid}) hwnd={Hwnd}",
                                proc.ProcessName, terminalPid.Value, hwnd);

                            if (hwnd != IntPtr.Zero)
                            {
                                // Guard against WT ping-pong: if we already switched to a target
                                // desktop, only call ForceForeground when the window lives on that
                                // same desktop. WT v1.23+ runs a single HWND across all tabs; its
                                // HWND lives on one desktop, and calling ForceForeground on a
                                // cross-desktop HWND pulls Windows back to that desktop.
                                bool shouldFocus = true;
                                if (target.HasValue)
                                {
                                    var hwndDesktop = _desktopManager.GetDesktopForWindow(hwnd);
                                    if (hwndDesktop.HasValue && hwndDesktop.Value != target.Value)
                                    {
                                        _logger.LogInformation(
                                            "Focus: suppressed ForceForeground to avoid ping-pong — hwnd={Hwnd} hwndDesktop={HwndDesktop} target={Target}",
                                            hwnd, hwndDesktop.Value, target.Value);
                                        shouldFocus = false;
                                    }
                                    else if (!hwndDesktop.HasValue)
                                    {
                                        // COM unavailable or window gone — skip focus; desktop switch
                                        // already landed the user on the target desktop.
                                        _logger.LogDebug(
                                            "Focus: GetDesktopForWindow returned null — skipping ForceForeground for session={Sid} hwnd={Hwnd}",
                                            entry.SessionId[..8], hwnd);
                                        shouldFocus = false;
                                    }
                                }

                                if (shouldFocus)
                                {
                                    var result = PInvokeWindow.ForceForeground(hwnd);
                                    _logger.LogInformation("Focus: ForceForeground result={Result}", result);
                                    // ForceForeground fails from balloon-tip notification context
                                    // (Windows blocks SetForegroundWindow). Desktop switch already
                                    // fired above, so the user lands on the right desktop regardless.
                                }
                            }
                        }
                    }
                    catch (ArgumentException)
                    {
                        // Terminal process died — clear cache so next click re-walks
                        Commands.ProcessResolver.ClearSession(entry.SessionId);
                        _logger.LogWarning("Focus: terminal process {Pid} died, cache cleared", terminalPid.Value);
                    }
                }
            }
            else
            {
                _logger.LogInformation("Focus: session={Sid} has no ClaudePid", entry.SessionId[..8]);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to switch to desktop for session {SessionId}",
                entry.SessionId);
        }
    }

    private void SwitchToWorkspaceDesktop(WorkspaceSessionEntry entry)
    {
        try
        {
            if (_desktopManager.IsAvailable)
            {
                _desktopManager.SwitchToDesktop(entry.Workspace.Desktop);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to switch to desktop for workspace {Name}",
                entry.Workspace.Name);
        }
    }

    private bool IsWindowsTerminal(SessionEntry entry)
    {
        try
        {
            if (entry.State.ClaudePid is not int claudePid)
                return false;

            var terminalPid = Commands.ProcessResolver.ResolveTerminalPid(claudePid, entry.SessionId);
            if (terminalPid is null)
                return false;

            var proc = System.Diagnostics.Process.GetProcessById(terminalPid.Value);
            using (proc)
            {
                return proc.ProcessName == "WindowsTerminal";
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "IsWindowsTerminal probe failed for session {SessionId}", entry.SessionId);
            return false;
        }
    }

    // --- ISessionInteractionRouter ---
    //
    // Single entry point for every user-initiated session/workspace interaction.
    // Every public method here follows the same two-phase shape:
    //   1. Mark interaction (reset age, refresh icon — uniform across all surfaces)
    //   2. Dispatch the intent-specific action (switch desktop / show menu)
    //
    // Callers must NOT call SwitchToSessionDesktop / SwitchToWorkspaceDesktop /
    // menu.Show / NotifyIconMenuHost.Show directly from event handlers.

    public void ActivateSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
        {
            _logger.LogDebug("ActivateSession: unknown session {SessionId}", sessionId);
            return;
        }
        MarkSessionInteracted(entry);
        SwitchToSessionDesktop(entry);
    }

    public void ActivateWorkspace(string workspacePath)
    {
        // _workspaces is keyed by path.ToUpperInvariant() (see workspace load logic)
        var key = workspacePath.ToUpperInvariant();
        if (!_workspaces.TryGetValue(key, out var entry))
        {
            _logger.LogDebug("ActivateWorkspace: unknown workspace {Path}", workspacePath);
            return;
        }
        MarkWorkspaceInteracted(entry);
        SwitchToWorkspaceDesktop(entry);
    }

    public void OpenSessionMenu(string sessionId, MenuAnchor anchor)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
        {
            _logger.LogDebug("OpenSessionMenu: unknown session {SessionId}", sessionId);
            return;
        }
        MarkSessionInteracted(entry);
        ShowContextMenuAt(entry.Menu, anchor);
    }

    public void OpenWorkspaceMenu(string workspacePath, MenuAnchor anchor)
    {
        var key = workspacePath.ToUpperInvariant();
        if (!_workspaces.TryGetValue(key, out var entry))
        {
            _logger.LogDebug("OpenWorkspaceMenu: unknown workspace {Path}", workspacePath);
            return;
        }
        MarkWorkspaceInteracted(entry);
        ShowContextMenuAt(entry.Menu, anchor);
    }

    public void OpenOverlayMenu(MenuAnchor anchor) => ShowContextMenuAt(_overlayMenu, anchor);

    // Dispatches a menu to the right mechanism for the anchor:
    //   - Tray NotifyIcon (shell-delivered click): NotifyIconMenuHost (reflects the
    //     NotifyIcon's private ShowContextMenu via AttachThreadInput).
    //   - Control owner (overlay form click): vanilla menu.Show(owner, location).
    // Callers always use MenuAnchor.AtTrayIcon / MenuAnchor.AtControl, so exactly one
    // branch fires.
    //
    // Instance method (not static) so it can use the shared _logger — the smaller-diff
    // choice over threading an ILogger parameter through every call site, and consistent
    // with the rest of this file, where every other private method reads _logger directly
    // rather than accepting it as a parameter.
    //
    // Menu lifecycle diagnostic (permanent, Debug-level, prod-silent via the same log-level
    // gating as every other LogDebug call in this file): logs which anchor mode is used, the
    // owner/location for AtControl, whether the menu was actually left visible after the
    // Show call returns, and — via a Closed subscription wired at most once per menu instance
    // — the ToolStripDropDownCloseReason when it eventually closes. This exists to answer,
    // from the next live test, whether an overlay right-click menu opens-then-immediately-
    // closes (see hover-dashboard-state-machine.md's SurfaceInteracted right-click history)
    // or never opens at all — those two failure modes look identical to a user but have very
    // different CloseReason signatures.
    //
    // Step 07 addendum: the foreground-refusal hypothesis above is now the WELL-TESTED half of
    // the picture — this method also now logs menu.Items.Count and the menu's GetHashCode()
    // identity immediately before and after the AtControl Show() call, and wires a once-per-
    // instance Closing subscription (same _menuLifecycleWired guard as Closed) alongside it.
    // This discriminates the untested hypothesis: ToolStripDropDown.Show() silently refuses to
    // become visible when Items.Count is 0, when the menu builders' Opening handler (which
    // rebuilds Items from scratch every time — see SessionMenuBuilder/OverlayMenuBuilder/
    // WorkspaceMenuBuilder/ControllerMenuBuilder) sets e.Cancel = true, or when that handler
    // throws (previously swallowed by its own try/catch with no item-count evidence). Read
    // together with the builders' new "Opening: start"/"Opening: end" log lines (same MenuId),
    // the ordering answers: did Opening fire at all between before/after? Did it produce zero
    // items? Did it cancel? Was a prior Closing still in flight? None of those require any
    // further Win32 investigation to rule in or out.
    //
    // Foreground grant + restore: the AtControl branch (overlay right-click) no longer relies
    // on any OverlayPanel activation exception — OverlayPanel.WndProc returns MA_NOACTIVATE
    // unconditionally (see its docs). Instead this method captures the real prior-foreground
    // window via CaptureForegroundForRestore() immediately before granting the overlay
    // foreground explicitly (PInvokeWindow.SetForegroundWindow wrapped in
    // PInvokeWindow.InvokeWithForegroundAttached — the same AttachThreadInput dance
    // NotifyIconMenuHost uses for the tray-icon path). The same Closed subscription that logs
    // the diagnostic above also restores whatever held foreground before this Show call, via
    // RestorePendingForeground, so the focus theft lasts only for the menu's lifetime.
    private void ShowContextMenuAt(ContextMenuStrip? menu, MenuAnchor anchor)
    {
        if (menu is null) return;

        if (!_menuLifecycleWired.TryGetValue(menu, out _))
        {
            _menuLifecycleWired.Add(menu, s_menuWiredMarker);
            menu.Closed += (_, closedArgs) =>
            {
                _logger.LogDebug("ShowContextMenuAt: menu closed, reason={CloseReason}", closedArgs.CloseReason);
                RestorePendingForeground();
            };

            // Diagnostic-only (Step 07): Closing fires BEFORE Closed, while the drop-down is
            // still tearing down. If a Closing from a PRIOR show is still in flight when the
            // next right-click calls Show() again on this same instance, that overlap is the
            // leading hypothesis for why a rebuild inside Opening could hand back an empty or
            // refused menu — this line makes that overlap visible in the log by timestamp
            // ordering against the "before menu.Show"/"after menu.Show" lines below.
            menu.Closing += (_, closingArgs) =>
            {
                _logger.LogDebug(
                    "ShowContextMenuAt: menu closing, menu={MenuId}, reason={CloseReason}, cancel={Cancel}",
                    menu.GetHashCode(), closingArgs.CloseReason, closingArgs.Cancel);
            };
        }

        if (anchor.TrayIcon is { } icon)
        {
            _logger.LogDebug("ShowContextMenuAt: showing via AtTrayIcon anchor (NotifyIconMenuHost)");
            NotifyIconMenuHost.Show(icon);
            _logger.LogDebug("ShowContextMenuAt: NotifyIconMenuHost.Show returned, menu.Visible={Visible}", menu.Visible);
        }
        else if (anchor.Owner is { } owner)
        {
            // Capture BEFORE granting foreground below — the last point GetForegroundWindow()
            // still reports the window genuinely about to be replaced (typically the user's
            // terminal), not the overlay's own (about-to-be-activated) handle.
            CaptureForegroundForRestore();

            _logger.LogDebug(
                "ShowContextMenuAt: showing via AtControl anchor (owner={OwnerType}, location={Location})",
                owner.GetType().Name, anchor.Location);

            var attach = PInvokeWindow.InvokeWithForegroundAttached(() =>
            {
                PInvokeWindow.SetForegroundWindow(owner.Handle);

                // Diagnostic-only (Step 07): bracket the Show() call itself with item-count
                // snapshots. If Show() returns Visible=false and item count went from N>0
                // before to N>0 after with NO "Opening: start/end" line in between (see the
                // menu builders' Opening handler logs), that is decisive evidence WinForms
                // refused to display the menu for a reason unrelated to content — i.e. a
                // Win32-level refusal, not the zero-items/cancelled-Opening hypothesis this
                // step exists to rule in or out.
                _logger.LogDebug(
                    "ShowContextMenuAt: before menu.Show, menu={MenuId}, items={ItemCount}",
                    menu.GetHashCode(), menu.Items.Count);

                menu.Show(owner, anchor.Location);

                _logger.LogDebug(
                    "ShowContextMenuAt: after menu.Show, menu={MenuId}, items={ItemCount}, visible={Visible}",
                    menu.GetHashCode(), menu.Items.Count, menu.Visible);

                // KB135788 (Microsoft, documented notify-icon/ContextMenuStrip foreground
                // bug — see https://learn.microsoft.com/en-us/answers/questions/1125620/resolved-maui-trayicon-with-contextmenustrip-not-c):
                // "if the window is already in the foreground, the menu appears and
                // immediately disappears on the second display." That precondition is
                // exactly our steady state here — the overlay is very often ALREADY
                // foreground by this point (SetForegroundWindow above is then a no-op),
                // which is why this bug presented as "roughly every other right-click opens
                // nothing." PostMessage(WM_NULL) is Microsoft's fix: it "forces a task
                // switch by posting a benign message, preventing the menu from immediately
                // disappearing on subsequent displays." Required sequence per the KB is
                // SetForegroundWindow → show-the-menu → PostMessage(WM_NULL), all three
                // together — kept inside this same InvokeWithForegroundAttached lambda
                // rather than after it returns, so the whole sequence runs while our
                // thread's input is still attached to the (possibly-about-to-be-replaced)
                // foreground thread, exactly mirroring the KB's un-interrupted ordering.
                // THIS CALL WAS REMOVED ONCE ALREADY (see MenuAnchor.AtControl's doc
                // comment) by a reader who didn't know the KB existed — do not remove it
                // again without re-reading that KB.
                PInvokeWindow.PostMessage(owner.Handle, PInvokeWindow.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            });

            _logger.LogDebug(
                "ShowContextMenuAt: menu.Show returned, menu={MenuId}, items={ItemCount}, menu.Visible={Visible}, attached={Attached}",
                menu.GetHashCode(), menu.Items.Count, menu.Visible, attach.Attached);
        }
    }

    // Samples the current foreground window every 100ms drain tick and records it as
    // _lastGoodForegroundWindow when it passes PInvokeWindow.IsAcceptableForegroundCandidate
    // (valid window, another process, has WS_CAPTION — a "real" top-level app window, not a
    // borderless popup). This is what actually fixes stale focus restore: capturing only at
    // menu-show time (CaptureForegroundForRestore below) is too late once some other
    // imrdy-owned window has already taken foreground between clicks, which is the common
    // case in practice (every observed live capture logged ownProcess=true). A rejected
    // sample is simply skipped — it never overwrites a previously-captured good value, same
    // contract as CaptureForegroundForRestore's own fallback.
    private void SampleForegroundForRestoreTracking()
    {
        var candidate = PInvokeWindow.GetForegroundWindow();
        if (candidate == IntPtr.Zero || !PInvokeWindow.IsWindow(candidate))
            return;

        PInvokeWindow.GetWindowThreadProcessId(candidate, out var pid);
        var isOwnProcess = pid == (uint)Environment.ProcessId;
        var hasCaption = PInvokeWindow.HasCaptionStyle(candidate);

        if (PInvokeWindow.IsAcceptableForegroundCandidate(isValidWindow: true, isOwnProcess, hasCaption))
        {
            _lastGoodForegroundWindow = candidate;
        }
    }

    // Captures the current foreground window as this menu invocation's restore target,
    // validating it at capture time rather than trusting it blindly until restore. Rejects a
    // candidate that is not a window, belongs to imrdy's own process (every transient
    // ToolStripDropDown/ContextMenuStrip popup this app itself creates — the observed failure
    // mode where right-clicking while a previous menu is still closing captures that dying
    // popup instead of the user's real window), or lacks WS_CAPTION (a borderless popup even
    // from another process). A rejected capture falls back to _lastGoodForegroundWindow — the
    // most recent capture that DID pass validation — rather than overwriting it with garbage,
    // so RestorePendingForeground still restores the user's actual prior window.
    private void CaptureForegroundForRestore()
    {
        var candidate = PInvokeWindow.GetForegroundWindow();
        var isValidWindow = candidate != IntPtr.Zero && PInvokeWindow.IsWindow(candidate);
        var isOwnProcess = false;
        var hasCaption = false;
        if (isValidWindow)
        {
            PInvokeWindow.GetWindowThreadProcessId(candidate, out var pid);
            isOwnProcess = pid == (uint)Environment.ProcessId;
            hasCaption = PInvokeWindow.HasCaptionStyle(candidate);
        }

        if (PInvokeWindow.IsAcceptableForegroundCandidate(isValidWindow, isOwnProcess, hasCaption))
        {
            _lastGoodForegroundWindow = candidate;
            _pendingForegroundRestore = candidate;
            _logger.LogDebug("CaptureForegroundForRestore: captured {Candidate} as restore target", candidate);
        }
        else
        {
            _pendingForegroundRestore = _lastGoodForegroundWindow;
            _logger.LogDebug(
                "CaptureForegroundForRestore: rejected {Candidate} (validWindow={ValidWindow}, " +
                "ownProcess={OwnProcess}, hasCaption={HasCaption}); falling back to {Previous}",
                candidate, isValidWindow, isOwnProcess, hasCaption, _lastGoodForegroundWindow);
        }
    }

    // Restores the foreground window captured just before an AtControl-anchored menu was
    // shown (see ShowContextMenuAt and _pendingForegroundRestore). Always clears the field
    // first so a later Closed firing — a different anchor mode, or no capture at all — can't
    // resurrect a stale HWND. Guards against three no-op/invalid cases, each logged so the
    // live log can distinguish them:
    //   - IntPtr.Zero: no capture happened (e.g. this Closed belongs to an AtTrayIcon show).
    //   - the overlay's own handle: nothing to restore — the overlay already held foreground
    //     before this menu opened (the common case for an overlay right-click).
    //   - no longer a valid window (IsWindow false): the captured target was closed/destroyed
    //     while the menu was open.
    // A false SetForegroundWindow result is expected under some conditions (e.g. the restore
    // target's process doesn't currently hold input-focus rights) and is logged, not thrown.
    private void RestorePendingForeground()
    {
        var target = _pendingForegroundRestore;
        _pendingForegroundRestore = IntPtr.Zero;

        if (target == IntPtr.Zero)
        {
            _logger.LogDebug("RestorePendingForeground: skipped, no captured target");
            return;
        }

        if (_overlayPanel is { IsHandleCreated: true } overlay && target == overlay.Handle)
        {
            _logger.LogDebug("RestorePendingForeground: skipped, captured target is the overlay's own handle");
            return;
        }

        if (!PInvokeWindow.IsWindow(target))
        {
            _logger.LogDebug(
                "RestorePendingForeground: skipped, captured target {Target} is no longer a valid window", target);
            return;
        }

        // SetForegroundWindow needs the same AttachThreadInput dance as the initial grant in
        // ShowContextMenuAt — the calling thread doesn't natively own foreground input here
        // either, since by this point the menu (not this thread) has been holding it.
        var restored = false;
        var attach = PInvokeWindow.InvokeWithForegroundAttached(() =>
        {
            restored = PInvokeWindow.SetForegroundWindow(target);
        });
        _logger.LogDebug(
            "RestorePendingForeground: attempted restore of {Target}, SetForegroundWindow returned {Restored}, attached={Attached}",
            target, restored, attach.Attached);
    }

    // Sentinel value for _menuLifecycleWired — ConditionalWeakTable only needs presence of
    // the key, but the value type must be a reference type; this is never inspected.
    private static readonly object s_menuWiredMarker = new();

    // Resets interaction age and refreshes icon/tooltip/overlay to brightest tier.
    // Shared by every ActivateSession / OpenSessionMenu path.
    private void MarkSessionInteracted(SessionEntry entry)
    {
        entry.LastSeenAt = DateTimeOffset.UtcNow;
        UpdateSessionIcon(entry);
    }

    private void MarkWorkspaceInteracted(WorkspaceSessionEntry entry)
    {
        // Workspace icons always render at tier 0 (never dim), so no icon refresh.
        entry.LastSeenAt = DateTimeOffset.UtcNow;
    }

    private void UpdateSessionIcon(SessionEntry entry)
    {
        if (entry.Icon is null)
        {
            return;
        }

        var agingSince = DateTimeOffset.UtcNow - entry.LastSeenAt;
        var tier = StatusMap.GetAgingTier(agingSince);
        entry.LastAgingTier = tier;
        entry.LastDisconnected = IsPublisherDisconnected(entry);
        entry.Icon.Icon = GetRendererForStyle(ResolveSessionIconStyle(entry)).GetIcon(entry.EffectiveStatus, tier, entry.LastDisconnected);
        entry.Icon.Text = FormatSessionTooltip(entry);
        RefreshOverlay();
    }

    private void CreateWorkspaceIcon(WorkspaceSessionEntry entry)
    {
        var icon = GetRendererForStyle(StyleNames.NormalizeStyleName(entry.IconStyle) ?? _currentIconStyle).GetIcon("workspace", 0, disconnected: false);

        entry.Icon = new NotifyIcon
        {
            Visible = _trayEnabled,
            Icon = icon,
            Text = TooltipFormatter.FormatWorkspace(entry.Workspace.Name, entry.Workspace.Desktop),
        };

        entry.Menu = WorkspaceMenuBuilder.Create(
            entry,
            onUnpin: path =>
            {
                _workspaceStore.Unpin(path);
                ReloadWorkspaces();
            },
            onAssignDesktop: () =>
            {
                var currentDesktop = _desktopManager.GetCurrentDesktopIndex();
                if (currentDesktop.HasValue)
                {
                    _workspaceStore.SetDesktop(entry.Workspace.Path, currentDesktop.Value);
                    ReloadWorkspaces();
                }
            },
            onSetDesktop: desktop =>
            {
                _workspaceStore.SetDesktop(entry.Workspace.Path, desktop);
                ReloadWorkspaces();
            },
            getDesktopCount: () => _desktopManager.GetDesktopCount(),
            getDesktopAvailable: () => _desktopManager.IsAvailable,
            onSetIconStyle: styleName =>
            {
                entry.IconStyle = styleName;
                _workspaceStore.SetIconStyle(entry.Workspace.Path, styleName);
                if (entry.Icon is not null)
                    entry.Icon.Icon = GetRendererForStyle(
                        StyleNames.NormalizeStyleName(entry.IconStyle) ?? _currentIconStyle)
                        .GetIcon("workspace", 0, disconnected: false);
                // Refresh sessions that inherit from this workspace
                RefreshAllSessionIcons();
            },
            getInstalledGraphicsPacks: () => _graphicsPackLoader.LoadPacks(ImrdyPaths.GraphicsPacksDir).Select(p => p.Name).ToList(),
            logger: _logger);
        entry.Icon.ContextMenuStrip = entry.Menu;

        entry.Icon.MouseClick += (_, e) =>
        {
            // Route through the same interface as overlay clicks.
            if (e.Button == MouseButtons.Left)
                ActivateWorkspace(entry.Workspace.Path);
            else if (e.Button == MouseButtons.Right)
                OpenWorkspaceMenu(entry.Workspace.Path, MenuAnchor.AtTrayIcon(entry.Icon));
        };
    }

    private static string FormatSessionTooltip(SessionEntry entry)
    {
        var age = DateTimeOffset.UtcNow - entry.StatusSince;
        return TooltipFormatter.FormatSession(
            entry.State.Project,
            entry.State.SessionName,
            entry.EffectiveStatus,
            age,
            entry.DesktopIndex,
            entry.SoundPack,
            entry.State.OriginMachine);
    }

    // --- Controller State ---

    private ControllerMenuState GetControllerState()
    {
        var config = ConfigReader.Read();
        var devBuild = BuildDevState();

        return new ControllerMenuState
        {
            Sessions = _sessions.Values.Select(e => new SessionMenuState
            {
                SessionId = e.SessionId,
                Status = e.EffectiveStatus,
                Project = e.State.Project,
                DesktopIndex = e.DesktopIndex,
            }).ToList(),
            Workspaces = _workspaces.Values.Select(w => new WorkspaceMenuState
            {
                WorkspaceName = w.Workspace.Name,
                WorkspacePath = w.Workspace.Path,
                DesktopIndex = w.Workspace.Desktop,
            }).ToList(),
            InstalledPacks = _loadedPacks.Select(p => p.Name).ToList(),
            InstalledGraphicsPacks = _graphicsPackLoader.LoadPacks(ImrdyPaths.GraphicsPacksDir)
                .Select(p => p.Name).ToList(),
            Monitors = Screen.AllScreens.Select((s, i) => $"Monitor {i + 1} ({s.Bounds.Width}×{s.Bounds.Height})").ToList(),
            Config = config,
            LogPath = ImrdyPaths.MonitorLog,
            DevBuild = devBuild,
            OverlayWorkingArea = ResolveScreenForMonitor(config.Overlay.Monitor).WorkingArea,
            OverlayPanelSize = _overlayPanel?.Size ?? EstimateOverlayPanelSize(config.Overlay),
        };
    }

    /// <summary>
    /// Resolves the <see cref="Screen"/> for a persisted monitor index, falling back to the
    /// primary (or first) screen when out of range — same convention as
    /// <c>OverlayPanel.ResolveTargetScreen</c>. Used by <see cref="GetControllerState"/> to
    /// supply <see cref="ControllerMenuState.OverlayWorkingArea"/> so the overlay position
    /// presets (D7) can resolve/check offsets without Core taking a Screen dependency.
    /// </summary>
    private static Screen ResolveScreenForMonitor(int monitor)
    {
        var screens = Screen.AllScreens;
        if (monitor >= 0 && monitor < screens.Length)
            return screens[monitor];
        return Screen.PrimaryScreen ?? screens[0];
    }

    /// <summary>
    /// Estimates the overlay panel's Form size (logical px, unscaled — DeviceDpi is only
    /// meaningful once a Form handle exists) for the rare case the overlay is currently
    /// disabled (<c>_overlayPanel is null</c>) and the menu is opened anyway. Mirrors
    /// <c>OverlayPanel.MinimumPanelWidth</c>'s formula at the single-chip (count=1) minimum,
    /// using the same <see cref="OverlayPanel.PanelPadding"/>/<see cref="OverlayPanel.GripWidthLogical"/>
    /// constants (internal, same assembly) so the estimate stays in sync with the real layout.
    /// </summary>
    private static Size EstimateOverlayPanelSize(OverlayConfig overlay)
    {
        var width = 2 * OverlayPanel.PanelPadding + OverlayPanel.GripWidthLogical + overlay.Size;
        var height = 2 * OverlayPanel.PanelPadding + overlay.Size;
        return new Size(width, height);
    }

    // --- Dev-build fixtures & preview-dashboard processes ---

    private DevBuildState? BuildDevState()
    {
        if (!File.Exists(ImrdyPaths.DevBuildMarker))
            return null;

        var fixtures = new List<DevFixture>();
        try
        {
            var repoPath = File.ReadAllText(ImrdyPaths.DevBuildMarker).Trim();
            if (!string.IsNullOrEmpty(repoPath) && Directory.Exists(repoPath))
            {
                var dir = Path.Combine(repoPath, "tests", "fixtures", "dashboards");
                if (Directory.Exists(dir))
                {
                    foreach (var path in Directory.EnumerateFiles(dir, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                        fixtures.Add(new DevFixture(Path.GetFileNameWithoutExtension(path), path));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to enumerate dev fixtures");
        }

        return new DevBuildState
        {
            Fixtures = fixtures,
            RunningPreviewCount = CountAndPruneAlivePreviews(),
        };
    }

    private void LaunchPreview(string fixturePath)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                _logger.LogWarning("Cannot launch preview: Environment.ProcessPath is null");
                return;
            }
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = false,
            };
            psi.ArgumentList.Add("preview-dashboard");
            psi.ArgumentList.Add(fixturePath);
            var proc = System.Diagnostics.Process.Start(psi);
            if (proc is not null)
            {
                lock (_previewProcessesLock)
                    _previewProcesses.Add(proc);
                _logger.LogInformation("Launched preview: pid={Pid} fixture={Fixture}", proc.Id, fixturePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to launch preview for {Path}", fixturePath);
        }
    }

    private void CloseAllPreviews()
    {
        List<System.Diagnostics.Process> snapshot;
        lock (_previewProcessesLock)
        {
            snapshot = new List<System.Diagnostics.Process>(_previewProcesses);
            _previewProcesses.Clear();
        }
        var killed = 0;
        foreach (var proc in snapshot)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    killed++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to kill preview pid={Pid}", SafePid(proc));
            }
            finally
            {
                proc.Dispose();
            }
        }
        _logger.LogInformation("Closed all previews: killed={Killed}, tracked={Tracked}", killed, snapshot.Count);
    }

    private int CountAndPruneAlivePreviews()
    {
        lock (_previewProcessesLock)
        {
            for (var i = _previewProcesses.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (_previewProcesses[i].HasExited)
                    {
                        _previewProcesses[i].Dispose();
                        _previewProcesses.RemoveAt(i);
                    }
                }
                catch
                {
                    _previewProcesses.RemoveAt(i);
                }
            }
            return _previewProcesses.Count;
        }
    }

    private static int SafePid(System.Diagnostics.Process proc)
    {
        try { return proc.Id; }
        catch { return -1; }
    }

    private void OnConfigChanged(ImrdyConfig config)
    {
        _soundConfig = config.Sound;
        _soundEnabled = _options.Silent ? false : config.Sound.Enabled;
        _loadedPacks = _packLoader.LoadPacks(ImrdyPaths.PacksDir);
        _soundBags.Clear();
        _balloonTipManager.SuppressSystemSound = _loadedPacks.Count > 0 && _soundEnabled;
        _logger.LogDebug("Config reloaded: enabled={Enabled}, default={Default}",
            config.Sound.Enabled, config.Sound.DefaultPack);

        var newIconStyle = StyleNames.NormalizeStyleName(config.Tray.IconStyle) ?? "circles";
        if (!string.Equals(newIconStyle, _currentIconStyle, StringComparison.OrdinalIgnoreCase))
        {
            _currentIconStyle = newIconStyle;
            RefreshAllSessionIcons();
            var overlayForInvalidate = _overlayPanel;
            if (overlayForInvalidate is not null)
                overlayForInvalidate.BeginInvoke(() => overlayForInvalidate.InvalidateStyleCache());
            _logger.LogInformation("Icon style changed to {IconStyle}", newIconStyle);
        }

        ApplyNetworkConfig(config.Network);

        var newTrayEnabled = config.Tray.Enabled;
        if (newTrayEnabled != _trayEnabled)
        {
            _trayEnabled = newTrayEnabled;
            ApplyTrayEnabledToAll();
            _logger.LogInformation("Tray icons {State}", _trayEnabled ? "enabled" : "disabled");
        }

        var newOverlay = config.Overlay;
        var oldOverlay = _overlayConfig;
        var enabledChanged = newOverlay.Enabled != _overlayEnabled;

        if (newOverlay != oldOverlay || enabledChanged)
        {
            // Drag-in-flight guard: defer the whole overlay block until dragging ends.
            if (_overlayPanel is { IsDragging: true })
            {
                _overlayReloadDeferred = true;
                // fall through — do NOT touch the overlay this tick
            }
            else
            {
                // Structural-delta classification: zero the non-structural quintuple
                // (Position/Monitor/Locked/OffsetX/OffsetY) so the generated record comparer
                // tests only structural fields (Enabled/Size/Spacing).
                // Never pass these zeroed records to CalculatePosition / OverlayAnchor.Parse.
                var sOld = oldOverlay with { Position = "", Monitor = 0, Locked = false, OffsetX = null, OffsetY = null };
                var sNew = newOverlay with { Position = "", Monitor = 0, Locked = false, OffsetX = null, OffsetY = null };
                var structuralDelta = sOld != sNew;

                if (!structuralDelta && _overlayPanel is not null && !enabledChanged)
                {
                    // Non-structural fast path: in-place re-place (no flash, no dispose+recreate).
                    _overlayPanel.ApplyPositionConfig(newOverlay.Position, newOverlay.Monitor, newOverlay.Locked,
                        newOverlay.OffsetX, newOverlay.OffsetY);
                    _overlayConfig = newOverlay;
                }
                else
                {
                    // Structural / enabled-toggle / panel-null: existing dispose+recreate path.
                    // Invariant unsubscribe order: workspace ctrl → session ctrl → cross-ctrl FormShown
                    // → dispose controllers → dispose panel.
                    if (_overlayPanel is not null)
                    {
                        if (_workspaceHoverController is not null)
                            _overlayPanel.SurfaceInteracted -= _workspaceHoverController.HandleSurfaceInteraction;
                        if (_hoverController is not null)
                            _overlayPanel.SurfaceInteracted -= _hoverController.HandleSurfaceInteraction;
                        _overlayPanel.DragCompleted -= HandleOverlayDragCompleted;
                        _logger.LogDebug("TrayApp: unsubscribed hover controllers from _overlayPanel.SurfaceInteracted + DragCompleted");
                    }
                    if (_hoverController is not null && _workspaceHoverController is not null)
                    {
                        _hoverController.FormShown -= _workspaceHoverController.HideIfVisible;
                        _workspaceHoverController.FormShown -= _hoverController.HideIfVisible;
                    }
                    _workspaceHoverController?.Dispose();
                    _workspaceHoverController = null;
                    _hoverController?.Dispose();
                    _hoverController = null;
                    _overlayPanel?.Dispose();
                    _overlayPanel = null;
                    _overlayConfig = config.Overlay;

                    var overlayNowEnabled = newOverlay.Enabled;
                    if (overlayNowEnabled)
                    {
                        try
                        {
                            _overlayPanel = CreateOverlay(config.Overlay);
                            _overlayPanel.Show();
                            RefreshOverlay();
                            _logger.LogInformation("Overlay enabled with position={Position}, size={Size}",
                                config.Overlay.Position, config.Overlay.Size);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to create overlay window during config change");
                            _overlayPanel = null;
                            overlayNowEnabled = false;
                        }

                        if (_overlayPanel is not null)
                        {
                            _hoverController = new SessionHoverDashboardController(
                                _overlayPanel,
                                _desktopManager,
                                _loggerFactory,
                                _hookAccumulationStore,
                                () => _sessions.Values.ToList(),
                                _gitCache);
                            _loggedNullHoverControllerWarning = false; // F6: reset so next absence is reported
                            _workspaceHoverController = new WorkspaceHoverDashboardController(
                                _overlayPanel,
                                _desktopManager,
                                _loggerFactory,
                                _workspaceStore,
                                GetWorkspaceLastSeen,
                                () => _desktopManager?.GetCurrentDesktopIndex(),
                                _gitCache);
                            _overlayPanel.SurfaceInteracted += _hoverController.HandleSurfaceInteraction;
                            _overlayPanel.SurfaceInteracted += _workspaceHoverController.HandleSurfaceInteraction;
                            _overlayPanel.DragCompleted += HandleOverlayDragCompleted;
                            _hoverController.FormShown += _workspaceHoverController.HideIfVisible;
                            _workspaceHoverController.FormShown += _hoverController.HideIfVisible;
                            _logger.LogDebug("TrayApp: subscribed _hoverController and _workspaceHoverController to _overlayPanel.SurfaceInteracted + DragCompleted + cross-controller FormShown");
                        }
                    }
                    else
                    {
                        _logger.LogInformation("Overlay disabled");
                    }
                    _overlayEnabled = overlayNowEnabled;
                }
            }
        }
    }

    private OverlayPanel CreateOverlay(OverlayConfig config)
    {
        return new OverlayPanel(config, this, _desktopManager, _loggerFactory, _graphicsPackLoader);
    }

    /// <summary>
    /// Subscribed to <see cref="OverlayPanel.DragCompleted"/> (P6 — TrayApp owns the wiring).
    /// A grip-drag drop never dispatches a session/workspace activation, so
    /// <see cref="OverlayPanel.SurfaceInteracted"/> does not fire for it. Reuses
    /// <c>HandleSurfaceInteraction</c> on both hover controllers — idempotent
    /// force-hide plus the post-interaction cooldown — so a chip left under the cursor
    /// after the drop does not ghost-reshow its dashboard via the normal dwell timer
    /// (Risk 10).
    /// </summary>
    private void HandleOverlayDragCompleted()
    {
        _hoverController?.HandleSurfaceInteraction();
        _workspaceHoverController?.HandleSurfaceInteraction();
    }

    private void RefreshAllSessionIcons()
    {
        foreach (var entry in _sessions.Values)
        {
            if (entry.Icon is null) continue;
            var agingSince = DateTimeOffset.UtcNow - entry.LastSeenAt;
            var tier = StatusMap.GetAgingTier(agingSince);
            entry.LastAgingTier = tier;
            entry.LastDisconnected = IsPublisherDisconnected(entry);
            entry.Icon.Icon = GetRendererForStyle(ResolveSessionIconStyle(entry)).GetIcon(entry.EffectiveStatus, tier, entry.LastDisconnected);
        }
        foreach (var ws in _workspaces.Values)
        {
            if (ws.Icon is null) continue;
            ws.Icon.Icon = GetRendererForStyle(StyleNames.NormalizeStyleName(ws.IconStyle) ?? _currentIconStyle).GetIcon("workspace", 0, disconnected: false);
        }
    }

    private BuiltDisplayItems BuildDisplayItems()
    {
        // Pre-compute workspace visibility via the existing _workspaceVisibility instance (D11).
        // Do NOT create a second WorkspaceVisibility — that would split hidden→visible tracking state.
        var workspaceEntries = _workspaces.Values.Select(ws => ws.Workspace).ToList();
        var activeSessions = _sessions.Values
            .Where(s => !s.Dismissed && s.State.Status != "end")
            .Select(s => s.State)
            .ToList();
        var visibilityResults = _workspaceVisibility.Evaluate(workspaceEntries, activeSessions);
        var workspaceVisible = visibilityResults.ToDictionary(
            r => r.Workspace.Path.ToUpperInvariant(),
            r => r.IsVisible,
            StringComparer.OrdinalIgnoreCase);

        var inputs = new List<DisplayItemInput>(_sessions.Count + _workspaces.Count);

        foreach (var s in _sessions.Values)
        {
            if (s.Icon is null || s.State is null) continue;
            // Logical visibility — independent of the tray god toggle. The tray toggle
            // is applied separately by DisplayItemCollection.Build via the trayEnabled
            // parameter (ForTray is empty when off; ForOverlay is unaffected). Using
            // s.Icon.Visible here would cause tray-off to also blank the overlay,
            // because Step 8's ApplyTrayEnabledToAll ties Icon.Visible to _trayEnabled.
            var sessionVisible = !s.Dismissed
                && (s.RemoveAfter is null || s.RemoveAfter > DateTimeOffset.UtcNow)
                && s.State.Status != "end";
            inputs.Add(new DisplayItemInput(
                Id: s.SessionId,
                ItemType: DisplayItemType.Session,
                Status: s.EffectiveStatus,
                DesktopIndex: s.DesktopIndex,
                IconStyle: ResolveSessionIconStyle(s),
                AgingTier: s.LastAgingTier,
                IsVisible: sessionVisible,
                Label: s.State.Project ?? s.SessionId,
                IsDisconnected: IsPublisherDisconnected(s)));
        }

        foreach (var ws in _workspaces.Values)
        {
            var key = ws.Workspace.Path.ToUpperInvariant();
            var isVisible = workspaceVisible.TryGetValue(key, out var v) ? v : ws.Visible;
            inputs.Add(new DisplayItemInput(
                Id: ws.Workspace.Path,
                ItemType: DisplayItemType.Workspace,
                Status: "workspace",
                DesktopIndex: ws.Workspace.Desktop,
                IconStyle: StyleNames.NormalizeStyleName(ws.IconStyle) ?? _currentIconStyle,
                AgingTier: 0,
                IsVisible: isVisible,
                Label: ws.Workspace.Name));
        }

        // Use cached _trayEnabled — NEVER call ConfigReader.Read() here (hot path; called every drain tick).
        return DisplayItemCollection.Build(inputs, _trayEnabled);
    }

    private void ApplyTrayEnabledToAll()
    {
        foreach (var entry in _sessions.Values)
        {
            if (entry.Icon is null) continue;
            // Off: all session icons hidden regardless of other state.
            // On: restore per the existing visibility rules — dismissed sessions stay hidden,
            // sessions pending removal stay hidden, all other sessions become visible.
            // Mirrors CreateSessionIcon's Visible=_trayEnabled default plus the explicit "not
            // dismissed and not past RemoveAfter" guard that governs steady-state visibility.
            // Unconditionally setting Visible = _trayEnabled would resurrect dismissed sessions
            // or those inside RemoveAfter grace (regression against sweep-removal fix 4702e86).
            var shouldShow = !entry.Dismissed
                && (entry.RemoveAfter is null || entry.RemoveAfter > DateTimeOffset.UtcNow);
            entry.Icon.Visible = _trayEnabled && shouldShow;
        }
        // Workspaces honor both the god toggle and the per-workspace visibility rule.
        // UpdateWorkspaceVisibility already reads _trayEnabled in the gated assignment.
        UpdateWorkspaceVisibility();
    }

    private void RefreshOverlay()
    {
        if (_overlayPanel is null || !_overlayEnabled) return;
        var items = BuildDisplayItems();
        _overlayPanel.UpdateItems(items.ForOverlay);
    }

    // --- Sound Triggers (port of PS1:700-750) ---

    private void TriggerStatusChangeSound(SessionEntry entry, string previousStatus, string newStatus)
    {
        if (IsBootstrapping)
        {
            return;
        }

        if (_cooldownTracker.IsOnCooldown(entry.SessionId, DateTimeOffset.UtcNow))
        {
            return;
        }

        // Map status transitions to sound events
        SoundEvent? soundEvent = (previousStatus, newStatus) switch
        {
            (_, "busy") when previousStatus != "busy" => SoundEvent.GettingToWork,
            (_, "error") when previousStatus != "error" => SoundEvent.NeedsYou,
            (_, "idle") when previousStatus is "busy" or "done" => SoundEvent.Finished,
            (_, "end") => SoundEvent.SessionEnd,
            _ => null,
        };

        // SessionStart is triggered by new session creation, not status change
        if (soundEvent is null)
        {
            return;
        }

        PlaySoundEvent(entry, soundEvent.Value);
    }

    private void TriggerNotificationSound(SessionEntry entry, string notificationType)
    {
        if (IsBootstrapping || string.IsNullOrEmpty(notificationType))
        {
            return;
        }

        if (_cooldownTracker.IsOnCooldown(entry.SessionId, DateTimeOffset.UtcNow))
        {
            return;
        }

        SoundEvent? soundEvent = notificationType switch
        {
            "permission_prompt" or "elicitation_dialog" => SoundEvent.NeedsYou,
            "idle_prompt" => SoundEvent.Forgotten,
            _ => null,
        };

        if (soundEvent is null)
        {
            return;
        }

        PlaySoundEvent(entry, soundEvent.Value);
    }

    private void PlaySoundEvent(SessionEntry entry, SoundEvent soundEvent)
    {
        if (!_soundEnabled)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var isCombo = _cooldownTracker.RecordAndCheckCombo(entry.SessionId, now);
        var effectiveEvent = isCombo ? SoundEvent.Combo : soundEvent;

        if (isCombo)
        {
            _logger.LogDebug("Combo detected for session {SessionId}, playing Combo instead of {Original}",
                entry.SessionId, soundEvent);
        }

        // Use the session's assigned pack — resolved at creation, sticky until changed
        var packName = entry.SoundPack;
        if (string.IsNullOrEmpty(packName))
        {
            return;
        }

        // Find the loaded pack
        var pack = _loadedPacks.FirstOrDefault(p =>
            string.Equals(p.Name, packName, StringComparison.OrdinalIgnoreCase));

        if (pack is null || !pack.WavFiles.TryGetValue(effectiveEvent, out var wavPaths) || wavPaths.Length == 0)
        {
            _logger.LogDebug("No WAV files for {Event} in pack {Pack}", effectiveEvent, packName);
            return;
        }

        // Get or create shuffle bag for this pack+event combination
        var bagKey = $"{packName}:{effectiveEvent}";
        if (!_soundBags.TryGetValue(bagKey, out var bag))
        {
            bag = new ShuffleBag<string>(wavPaths);
            _soundBags[bagKey] = bag;
        }

        var wavPath = bag.Draw();
        if (wavPath is null)
        {
            return;
        }

        _soundPlayer.Play(wavPath);
        _logger.LogDebug("Playing {Event} from {Pack}: {File}",
            effectiveEvent, packName, Path.GetFileName(wavPath));
    }

    // --- IPC server ---

    private void StartIpcServer()
    {
        var config = ConfigReader.Read();
        var ipcEnabled = config.Diagnostics.IpcEnabled ?? File.Exists(ImrdyPaths.DevBuildMarker);
        if (!ipcEnabled)
        {
            return;
        }

        var handlers = new Dictionary<string, Func<InspectRequest, InspectResponse>>(StringComparer.Ordinal)
        {
            ["inspect-live"] = req => InspectLiveHandler.Handle(
                req, _sessions.Values.ToList(), _hookAccumulationStore, _gitCache, _loggerFactory),
            ["render-live"] = req => RenderLiveHandler.Handle(
                req, _sessions.Values.ToList(), _hookAccumulationStore, _gitCache, _loggerFactory),

            // r-2: `imrdy links` is a CLI process and holds no sinks, so the only place live
            // link health exists is here. This is the same join the connections window renders
            // — one call, no dial, no file walk — which is what keeps it inside the handler's
            // 2-second UI-thread budget.
            ["links-live"] = _ => new InspectResponse(
                "1", "links-live", null, null, null, ((IConnectionsHost)this).BuildViewModel()),
        };

        // Test-only: when IMRDY_TEST_HOLD_HANDLE is set, register a "ping" verb whose handler
        // blocks on the named system event until signalled. Used by StressTests.ConcurrentConnections
        // to hold 4 server slots open and verify the concurrency cap. Never populated in production
        // (the env var is never set outside test harnesses).
        var holdHandleName = Environment.GetEnvironmentVariable("IMRDY_TEST_HOLD_HANDLE");
        if (!string.IsNullOrEmpty(holdHandleName))
        {
            handlers["ping"] = _ =>
            {
                if (EventWaitHandle.TryOpenExisting(holdHandleName, out var ev))
                {
                    using (ev)
                        ev.WaitOne();
                }
                return new InspectResponse("1", "ping", null, null, null);
            };
        }

        _inspectIpcServer = new InspectIpcServer(_loggerFactory, _controllerIcon.ContextMenuStrip!, handlers);
        _inspectIpcServer.Start(_shutdownCts.Token);
    }

    // --- Stop signal ---

    private void ListenForStopSignal()
    {
        var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, ImrdyPaths.StopEventName);
        var token = _shutdownCts.Token;
        Task.Run(() =>
        {
            try
            {
                WaitHandle.WaitAny([stopEvent, token.WaitHandle]);
                if (!token.IsCancellationRequested)
                {
                    _logger.LogInformation("Stop signal received — exiting");
                    // Application.Exit() is thread-safe and posts WM_QUIT to the UI pump.
                    // It triggers Application.ApplicationExit → OnApplicationExit → Shutdown().
                    // Do NOT use ContextMenuStrip.BeginInvoke — that silently fails when the
                    // strip's native handle hasn't been created (i.e. the user has never
                    // right-clicked the tray to open the menu), leaving the tray unable to exit.
                    Application.Exit();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stop listener failed");
            }
            finally
            {
                stopEvent.Dispose();
            }
        }, token);
    }

    // --- Shutdown ---

    protected override void ExitThreadCore()
    {
        Shutdown();
        base.ExitThreadCore();
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        Shutdown();
    }

    private void OnApplicationExit(object? sender, EventArgs e)
    {
        Shutdown();
    }

    private void Shutdown()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _logger.LogInformation("TrayApp shutting down");

        _shutdownCts.Cancel();

        // Stop timers
        _drainTimer?.Stop();
        _drainTimer?.Dispose();
        _sweepTimer?.Stop();
        _sweepTimer?.Dispose();
        _staleTimer?.Stop();
        _staleTimer?.Dispose();
        _agingTimer?.Stop();
        _agingTimer?.Dispose();


        // Stop watchers
        if (_sessionWatcher is not null)
        {
            _sessionWatcher.EnableRaisingEvents = false;
            _sessionWatcher.Dispose();
        }

        if (_workspaceWatcher is not null)
        {
            _workspaceWatcher.EnableRaisingEvents = false;
            _workspaceWatcher.Dispose();
        }

        if (_configWatcher is not null)
        {
            _configWatcher.EnableRaisingEvents = false;
            _configWatcher.Dispose();
        }

        // Dispose hover dashboard controllers before overlay and session entries.
        // Unsubscribe BEFORE dispose; invariant order: workspace → session → overlay.
        if (_overlayPanel is not null)
        {
            if (_workspaceHoverController is not null)
                _overlayPanel.SurfaceInteracted -= _workspaceHoverController.HandleSurfaceInteraction;
            if (_hoverController is not null)
                _overlayPanel.SurfaceInteracted -= _hoverController.HandleSurfaceInteraction;
            _overlayPanel.DragCompleted -= HandleOverlayDragCompleted;
            _logger.LogDebug("TrayApp: unsubscribed hover controllers from _overlayPanel.SurfaceInteracted + DragCompleted");
        }
        if (_hoverController is not null && _workspaceHoverController is not null)
        {
            _hoverController.FormShown -= _workspaceHoverController.HideIfVisible;
            _workspaceHoverController.FormShown -= _hoverController.HideIfVisible;
        }
        _workspaceHoverController?.Dispose();
        _workspaceHoverController = null;
        _hoverController?.Dispose();
        _hoverController = null;

        // Close any dev-build preview windows we launched, so they don't linger after tray exit.
        CloseAllPreviews();

        // Dispose controller icon
        _controllerIcon.Visible = false;
        _controllerIcon.Icon?.Dispose();
        _controllerIcon.ContextMenuStrip?.Dispose();
        _overlayMenu?.Dispose();
        _controllerIcon.Dispose();

        // Dispose overlay panel
        _overlayPanel?.Dispose();
        _overlayPanel = null;

        // Dispose all session icons
        foreach (var entry in _sessions.Values)
        {
            entry.Dispose();
        }

        _sessions.Clear();

        // Dispose all workspace icons
        foreach (var entry in _workspaces.Values)
        {
            entry.Dispose();
        }

        _workspaces.Clear();

        foreach (var r in _rendererCache.Values)
            r.Dispose();
        _rendererCache.Clear();
        _balloonTipManager.Dispose();
        _soundPlayer.Dispose();
        _desktopManager.Dispose();
        _inspectIpcServer?.Dispose();

        _connectionsForm?.Dispose();
        _connectionsForm = null;

        // A TCP sink owns a socket and a dial loop; the registry disposes every sink it holds.
        _wireListener?.Dispose();
        _wireListener = null;
        _sinkRegistry?.Dispose();
        _sinkRegistry = null;

        _shutdownCts.Dispose();

        _logger.LogInformation("TrayApp shutdown complete");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Shutdown();
        }

        base.Dispose(disposing);
    }

    // --- Console Ctrl Handler (catches Ctrl+C, console close, logoff, shutdown) ---

    private delegate bool ConsoleCtrlDelegate(int ctrlType);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate? handler, bool add);

    // Must be a static field to prevent GC collection of the delegate
    private static readonly ConsoleCtrlDelegate OnConsoleCtrl = ctrlType =>
    {
        // ctrlType: 0=Ctrl+C, 1=Ctrl+Break, 2=Close, 5=Logoff, 6=Shutdown
        Application.Exit();
        return true;
    };
}
