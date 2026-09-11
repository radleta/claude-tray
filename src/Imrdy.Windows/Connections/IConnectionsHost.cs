using Imrdy.Core.Publishing;

namespace Imrdy.Windows.Connections;

/// <summary>
/// Everything <see cref="ConnectionsForm"/> needs from the running tray, behind one seam so
/// the form can also be rendered from a fixture. Same shape and same reason as
/// <c>ISessionInteractionRouter</c> and its <c>NullSessionInteractionRouter</c>: the render
/// component substitutes an implementation that answers with fixture data and performs
/// nothing.
/// </summary>
internal interface IConnectionsHost
{
    /// <summary>
    /// The complete render contract, rebuilt on demand. Called on the UI thread each refresh
    /// tick, so it must not block — the tray's implementation reads two in-memory health
    /// tables and one small JSON file.
    /// </summary>
    ConnectionsViewModel BuildViewModel();

    /// <summary>
    /// Adds or replaces one record in <c>publishers.json</c>, matched by name.
    /// </summary>
    /// <param name="previousName">
    /// The name this record had before the edit, when the operator renamed it, so the old
    /// record goes away instead of surviving beside the new one. Null when adding.
    /// </param>
    void SavePublisher(PublisherEntry entry, string? previousName);

    /// <summary>Removes one record and, per D21, the sessions that arrived under its name.</summary>
    void RemovePublisher(string name);

    /// <summary>
    /// D21's clear-this-machine: deletes every session file this machine delivered, leaving
    /// the record in place. The publisher's next event repopulates whatever is still live,
    /// which is the same repopulation D16 already expects.
    /// </summary>
    void ClearMachineSessions(string name);
}
