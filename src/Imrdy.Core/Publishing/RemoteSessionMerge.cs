using Imrdy.Core.State;

namespace Imrdy.Core.Publishing;

/// <summary>
/// The ingest seam: the only writer of remote session files on the receiver.
/// <para>
/// This and <see cref="Imrdy.Core.Hooks.FieldPreservation"/> are two seams solving one
/// problem and must not be collapsed into each other. <c>FieldPreservation</c> protects
/// tray-owned fields from a hook write on the same machine; this protects receiver-owned
/// fields from a publisher write arriving from another machine. They share a shape and a
/// field list that overlaps but does not match.
/// </para>
/// <para>
/// D34's list also names <c>Dismissed</c> and <c>RemoveAfter</c>. Those are in-memory
/// fields on the receiver's <c>SessionEntry</c> and are not on the state file at all, so a
/// publisher write cannot reach them and this merge has nothing to do for them.
/// </para>
/// </summary>
public static class RemoteSessionMerge
{
    /// <summary>
    /// Merges an incoming remote payload over what is already on the receiver's disk.
    /// Receiver-owned fields keep their existing values; everything else is taken as sent.
    /// </summary>
    /// <param name="incoming">The payload as the publisher emitted it.</param>
    /// <param name="existing">What the receiver already has, or null on first sight.</param>
    /// <param name="originMachine">
    /// The publisher this payload arrived from. Always wins over any <c>origin_machine</c>
    /// the payload carried, so a relayed or misconfigured publisher cannot claim to be
    /// another machine.
    /// </param>
    public static StateFileModel Merge(
        StateFileModel incoming,
        StateFileModel? existing,
        string originMachine)
    {
        // Incoming desktop_index is discarded outright, not merged: the publisher's desktop
        // number means nothing on this machine, and D18's per-publisher mapping is the only
        // source of a remote session's desktop. Preserving the receiver's own value keeps
        // TrayApp's desktop_index write-back paths harmless rather than a race.
        return incoming with
        {
            OriginMachine = originMachine,
            SoundPack = existing?.SoundPack,
            IconStyle = existing?.IconStyle,
            DesktopIndex = existing?.DesktopIndex,
        };
    }
}
