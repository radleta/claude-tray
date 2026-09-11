using Imrdy.Core.Publishing;
using Imrdy.Windows.Connections;

namespace Imrdy.Windows.Rendering;

/// <summary>
/// Answers <see cref="ConnectionsForm"/>'s refresh with fixture data and performs none of its
/// verbs, so the window renders deterministically with no tray, no store and no socket. Same
/// role as <see cref="NullSessionInteractionRouter"/>.
/// </summary>
internal sealed class NullConnectionsHost(ConnectionsViewModel viewModel) : IConnectionsHost
{
    public ConnectionsViewModel BuildViewModel() => viewModel;

    public void SavePublisher(PublisherEntry entry, string? previousName)
    {
        // No-op: the render path never dispatches a verb.
    }

    public void RemovePublisher(string name)
    {
        // No-op: the render path never dispatches a verb.
    }

    public void ClearMachineSessions(string name)
    {
        // No-op: the render path never dispatches a verb.
    }
}
