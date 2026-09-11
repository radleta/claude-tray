namespace Imrdy.Core.Publishing;

/// <summary>
/// Resolves the name this publisher stamps as <c>origin_machine</c> and registers under.
/// Each WSL distro is its own publisher (D19), so a distro on the same box as the receiver
/// still gets a distinct name — what makes it *local* to the receiver is that its hostname
/// half matches, not that its name does.
/// </summary>
public static class MachineNameResolver
{
    /// <summary>
    /// Config wins outright. Otherwise the default is the hostname, or
    /// <c>&lt;hostname&gt;-&lt;distro&gt;</c> when running inside a WSL distro.
    /// </summary>
    /// <param name="configured">`network.machineName`, or null to derive one.</param>
    /// <param name="hostName">This machine's hostname.</param>
    /// <param name="wslDistro">`WSL_DISTRO_NAME`, or null when not inside WSL.</param>
    public static string Resolve(string? configured, string hostName, string? wslDistro)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        return string.IsNullOrWhiteSpace(wslDistro)
            ? hostName
            : $"{hostName}-{wslDistro.Trim()}";
    }

    /// <summary>
    /// True when a publisher's sessions belong to the receiver's own box, which earns them
    /// real terminal focus and no desktop mapping (D19). A distro publisher named
    /// <c>&lt;hostname&gt;-&lt;distro&gt;</c> is local to the receiver whose hostname is that
    /// prefix; another machine, and any distro on it, is not.
    /// </summary>
    public static bool IsSameMachine(string publisherName, string receiverHostName)
    {
        if (string.Equals(publisherName, receiverHostName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The separator must be present, or "desk2" would read as local to "desk".
        return publisherName.StartsWith(receiverHostName + "-", StringComparison.OrdinalIgnoreCase);
    }
}
