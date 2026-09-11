namespace Imrdy.Core;

/// <summary>
/// Centralized path constants for ~/.imrdy/ directory structure.
/// All config, sessions, sounds, and logs live under this root.
/// Supports IMRDY_HOME override for testing.
/// </summary>
public static class ImrdyPaths
{
    public static string Home { get; }
    public static string Config { get; }
    public static string Sessions { get; }
    public static string Workspaces { get; }
    public static string Publishers { get; }

    /// <summary>
    /// Where file-sink publishers write their liveness beats — a sibling of
    /// <see cref="Sessions"/>, deliberately not inside it, since everything in the sessions
    /// directory is read as a session. See
    /// <see cref="Imrdy.Core.Publishing.PublisherHeartbeat"/>.
    /// </summary>
    public static string Heartbeats { get; }

    /// <summary>
    /// The Linux daemon's single-instance lock. Its PID file sits beside it as
    /// <c>daemon.pid</c> — see <see cref="Imrdy.Core.Publishing.DaemonLock"/>.
    /// </summary>
    public static string DaemonLock { get; }
    public static string SoundsDir { get; }
    public static string PacksDir { get; }
    public static string GraphicsDir { get; }
    public static string GraphicsPacksDir { get; }
    public static string LogsDir { get; }
    public static string MonitorLog { get; }
    public static string HookLog { get; }
    public static string DaemonLog { get; }
    public static string DevBuildMarker { get; }

    public const string MutexName = @"Global\ImrdyMonitor";
    public const string StopEventName = @"Local\ImrdyStop";
    public const string InspectPipeName = @"Local\ImrdyInspect";

    static ImrdyPaths()
    {
        Home = Environment.GetEnvironmentVariable("IMRDY_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".imrdy");

        Config = Path.Combine(Home, "config.json");
        Sessions = Path.Combine(Home, "sessions");
        Workspaces = Path.Combine(Home, "workspaces.json");
        Publishers = Path.Combine(Home, "publishers.json");
        // The name comes from PublisherHeartbeat, never a second literal: the publisher derives
        // its target from that constant and this is the receiver's end of the same rendezvous.
        // A second spelling that drifted would leave the tray reading an empty directory, and
        // since absence deliberately reads as connected, every file-sink publisher would report
        // healthy forever with no error and no failing test.
        Heartbeats = Path.Combine(Home, Publishing.PublisherHeartbeat.DirectoryName);
        DaemonLock = Path.Combine(Home, "daemon.lock");
        SoundsDir = Path.Combine(Home, "sounds");
        PacksDir = Path.Combine(Home, "sounds", "packs");
        GraphicsDir = Path.Combine(Home, "graphics");
        GraphicsPacksDir = Path.Combine(Home, "graphics", "packs");
        LogsDir = Path.Combine(Home, "logs");
        MonitorLog = Path.Combine(Home, "logs", "monitor.log");
        HookLog = Path.Combine(Home, "logs", "hook_.log");
        DaemonLog = Path.Combine(Home, "logs", "daemon_.log");
        DevBuildMarker = Path.Combine(Home, ".dev-build");
    }
}
