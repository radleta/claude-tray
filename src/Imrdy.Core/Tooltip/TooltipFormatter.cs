namespace Imrdy.Core.Tooltip;

/// <summary>
/// Formats tooltips for session and workspace tray icons.
/// NotifyIcon.Text is limited to 63 characters.
/// </summary>
public static class TooltipFormatter
{
    private const int MaxTooltipLength = 63;

    /// <summary>
    /// Formats a session tooltip.
    /// Unnamed: "project [status 2m] (d1) ~pack"
    /// Named:   "project: session-name [status 2m] (d1) ~pack"
    /// Remote:  "project@machine: session-name [status 2m] (d1) ~pack"
    /// </summary>
    /// <param name="originMachine">
    /// The publisher this session arrived from, or null when it originated here. Rendered right
    /// after the project so it survives the 63-character cap, which trims from the tail (D23).
    /// </param>
    public static string FormatSession(
        string project,
        string? sessionName,
        string status,
        TimeSpan age,
        int? desktopIndex,
        string? packName,
        string? originMachine = null)
    {
        var ageStr = FormatAge(age);
        var originStr = !string.IsNullOrEmpty(originMachine) ? $"@{originMachine}" : "";
        var desktopStr = desktopIndex.HasValue ? $" (d{desktopIndex.Value + 1})" : "";
        var packStr = !string.IsNullOrEmpty(packName) ? $" ~{packName}" : "";

        var tooltip = string.IsNullOrEmpty(sessionName)
            ? $"{project}{originStr} [{status} {ageStr}]{desktopStr}{packStr}"
            : $"{project}{originStr}: {sessionName} [{status} {ageStr}]{desktopStr}{packStr}";

        return Truncate(tooltip);
    }

    /// <summary>
    /// Formats a workspace tooltip: "name [workspace] (d1)"
    /// </summary>
    public static string FormatWorkspace(string name, int desktopIndex)
    {
        var tooltip = $"{name} [workspace] (d{desktopIndex + 1})";
        return Truncate(tooltip);
    }

    /// <summary>
    /// Formats a time span as a human-readable age string.
    /// Examples: "0s", "45s", "2m", "1h", "3d"
    /// </summary>
    public static string FormatAge(TimeSpan age)
    {
        if (age.TotalDays >= 1)
        {
            return $"{(int)age.TotalDays}d";
        }

        if (age.TotalHours >= 1)
        {
            return $"{(int)age.TotalHours}h";
        }

        if (age.TotalMinutes >= 1)
        {
            return $"{(int)age.TotalMinutes}m";
        }

        return $"{(int)age.TotalSeconds}s";
    }

    private static string Truncate(string text)
    {
        return text.Length <= MaxTooltipLength ? text : text[..MaxTooltipLength];
    }
}
