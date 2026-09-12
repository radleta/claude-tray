using Imrdy.Core.State;

namespace Imrdy.Core.Publishing;

/// <summary>
/// One publisher's beat with its machine name recovered. <see cref="HeartbeatWatch"/> keys its
/// snapshot by the filename <em>token</em>, and a token is lossy, so this is the shape anything
/// that has to <em>name</em> a heartbeat-known publisher consumes.
/// </summary>
/// <param name="Name">The machine name, recovered per <see cref="HeartbeatMachines.Resolve"/>.</param>
/// <param name="BeatAt">When that publisher last beat.</param>
/// <param name="NameIsToken">
/// True when nothing on this machine could name this publisher and
/// <paramref name="Name"/> is the flattened filename token standing in. It is good enough to
/// render and <b>not</b> good enough to persist: every consumer of a
/// <see cref="PublisherEntry"/> joins on its name by plain case-insensitive equality, so a
/// record saved under <c>pc-excalibur-ubuntu-24_04</c> never matches the
/// <c>PC-Excalibur-Ubuntu-24.04</c> the publisher stamps into <c>origin_machine</c> — and the
/// desktop mapping, the mute and clear-this-machine would all silently do nothing forever.
/// This flag is what lets the surfaces tell the two cases apart.
/// </param>
public sealed record MachineBeat(string Name, DateTimeOffset BeatAt, bool NameIsToken);

/// <summary>
/// Turns the receiver's heartbeat directory into named publishers, so a file-sink publisher can
/// answer the question "which publishers exist?" — see <c>facts.md</c> <c>f-filesink-no-socket</c>.
/// A file sink opens no socket, sends no <c>hello</c> and never reaches
/// <c>WireListener.Health()</c>, so any receiver-side surface that enumerates publishers from the
/// listener alone is blind to it. The beat is what it has instead.
/// <para>
/// <b>Why a name has to be recovered at all.</b> <see cref="PublisherHeartbeat.TokenFor"/> lower-
/// cases and replaces every character outside <c>[A-Za-z0-9_-]</c>, so
/// <c>PC-Excalibur-Ubuntu-24.04</c> lands on disk as <c>pc-excalibur-ubuntu-24_04</c> and cannot
/// be reversed. The real name is therefore taken from a caller-supplied candidate list, matched
/// by comparing <see cref="PublisherHeartbeat.TokenFor"/> of each candidate against the token —
/// never by comparing the names, since the case-insensitive comparison used everywhere else does
/// not model the dot-to-underscore mapping.
/// </para>
/// <para>
/// <b>Two candidate sources, in that order, and the token as the floor.</b> A registered
/// <see cref="PublisherEntry.Name"/> comes first because it is what the operator typed and what
/// every other surface — the desktop mapping, the mute, the row itself — already keys on; a
/// beat matching one must produce that record's row rather than a second one beside it.
/// <c>origin_machine</c> off the ingested session files comes next: it is the publisher's own
/// name, stamped verbatim on the wire, and it is the only source at all for a delivering
/// publisher nobody registered — which is exactly the case this exists for. When neither
/// matches, the token stands in: a publisher beating with nothing registered and nothing yet
/// delivered has no other name anywhere on this machine, and a near-miss name is a far better
/// row than no row.
/// </para>
/// <para>
/// <b>A token name is for reading, never for saving.</b> That last arm sets
/// <see cref="MachineBeat.NameIsToken"/>, and the surfaces are required to carry it through: the
/// flattened name is a fine label and a silent trap as a <see cref="PublisherEntry"/> name, since
/// every other behaviour keyed on that record — D18's desktop mapping, D22's mute,
/// clear-this-machine — joins on plain case-insensitive equality with the publisher's real
/// <c>origin_machine</c> and would never match again. The connections window therefore refuses to
/// seed an editor with it, and both surfaces say on the row that the name is derived.
/// </para>
/// </summary>
public static class HeartbeatMachines
{
    /// <summary>
    /// Names every beat in a snapshot. Pure: the caller supplies both the beats and the
    /// candidate names, so the tray passes what it already holds in memory and a CLI process
    /// passes what it read off disk.
    /// </summary>
    public static IReadOnlyList<MachineBeat> Resolve(
        IReadOnlyDictionary<string, DateTimeOffset> beats,
        IEnumerable<string?> candidateNames)
    {
        if (beats.Count == 0)
        {
            return [];
        }

        var namesByToken = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidate in candidateNames)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var token = PublisherHeartbeat.TokenFor(candidate);

            // First candidate wins, which is what makes the caller's ordering the tie-break:
            // two names that differ only where TokenFor flattens them are one publisher as far
            // as the beat file is concerned, and the registered one is the one to show.
            if (beats.ContainsKey(token))
            {
                namesByToken.TryAdd(token, candidate.Trim());
            }
        }

        var resolved = new List<MachineBeat>(beats.Count);
        foreach (var (token, beat) in beats)
        {
            var recovered = namesByToken.TryGetValue(token, out var name);
            resolved.Add(new MachineBeat(recovered ? name! : token, beat, NameIsToken: !recovered));
        }

        return resolved;
    }

    /// <summary>
    /// The same thing for a process with no tray: reads the beat directory beside
    /// <paramref name="sessionsDirectory"/> and recovers names from
    /// <paramref name="registeredNames"/> plus the <c>origin_machine</c> stamped on the session
    /// files already ingested there.
    /// <para>
    /// <c>imrdy links</c> needs this because its records-only fallback (r-2) builds its view
    /// model from <c>publishers.json</c> alone, and a publisher the operator never registered is
    /// not in it. The session files are, and they are on disk in a process that already reads
    /// that directory's sibling.
    /// </para>
    /// </summary>
    public static IReadOnlyList<MachineBeat> Read(
        string sessionsDirectory,
        IEnumerable<string?> registeredNames)
    {
        var watch = new HeartbeatWatch(PublisherHeartbeat.DirectoryFor(sessionsDirectory));
        watch.Refresh();

        if (watch.Beats.Count == 0)
        {
            // Nothing beat here, so there is no name to recover and no reason to read every
            // session file to find out.
            return [];
        }

        var origins = new StateFileReader()
            .ReadAllStateFiles(sessionsDirectory)
            .Select(state => state.OriginMachine);

        return Resolve(watch.Beats, registeredNames.Concat(origins));
    }
}
