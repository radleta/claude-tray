using System.Text.Json;

namespace Imrdy.Core.Publishing;

/// <summary>
/// Reads/writes ~/.imrdy/publishers.json with atomic writes, following
/// <see cref="Imrdy.Core.Workspace.WorkspaceStore"/> exactly: one file, one record type,
/// Load-mutate-Save per-field setters, and a corrupt or unreadable file loading as an empty
/// list without being overwritten.
/// <para>
/// Unlike session state files, this is a config file and so does use
/// <see cref="AtomicFileWriter"/>, like every other config file.
/// </para>
/// </summary>
public sealed class PublisherStore
{
    private readonly string _filePath;

    public PublisherStore(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>
    /// Loads the publisher list. Returns an empty config if the file is missing or corrupt;
    /// a read failure never causes a write, so a corrupt file is left for the operator.
    /// </summary>
    public PublisherConfig Load()
    {
        if (!File.Exists(_filePath))
        {
            return new PublisherConfig();
        }

        try
        {
            var bytes = File.ReadAllBytes(_filePath);
            return JsonSerializer.Deserialize(bytes, ImrdyJsonContext.Default.PublisherConfig)
                   ?? new PublisherConfig();
        }
        catch (JsonException)
        {
            return new PublisherConfig();
        }
        catch (IOException)
        {
            return new PublisherConfig();
        }
    }

    public void Save(PublisherConfig config)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(config, ImrdyJsonContext.Default.PublisherConfig);
        AtomicFileWriter.Write(_filePath, json);
    }

    /// <summary>Returns the entry with this name, or null.</summary>
    public PublisherEntry? Find(string name)
    {
        return Load().Publishers.FirstOrDefault(p => NameEquals(p.Name, name));
    }

    /// <summary>
    /// Registers a link, or updates an existing one's endpoint. Per-link settings
    /// (<c>DesktopIndex</c>, <c>Muted</c>, <c>Enabled</c>) survive a re-add.
    /// <para>
    /// A null <paramref name="endpoint"/> registers the machine receive-only (r-1): the record
    /// carries its desktop mapping and mute, and nothing dials it.
    /// </para>
    /// <para>
    /// Like the per-field setters below, this has no production caller left — <see cref="Upsert"/>
    /// replaced the save path — and the same warning applies: it is not the established route for
    /// a new mutation surface, and a surface that writes through it still owes the reconcile
    /// described there.
    /// </para>
    /// </summary>
    public void Add(string name, string? endpoint)
    {
        var config = Load();
        var index = config.Publishers.FindIndex(p => NameEquals(p.Name, name));

        if (index >= 0)
        {
            config.Publishers[index] = config.Publishers[index] with { Endpoint = endpoint };
        }
        else
        {
            config.Publishers.Add(new PublisherEntry { Name = name, Endpoint = endpoint });
        }

        Save(config);
    }

    /// <summary>
    /// Writes one whole record, replacing any existing link of the same name. For the caller
    /// that already holds every field — the connections window's save path, where the edit
    /// dialog produces a complete <see cref="PublisherEntry"/> — this is one Load-mutate-Save
    /// instead of an <see cref="Add"/> followed by a setter per field, each of which is its own
    /// atomic write and its own reconcile opportunity.
    /// <para>
    /// The per-field setters below are unaffected and remain the way to change one field
    /// without holding the rest, which is how <see cref="Imrdy.Core.Workspace.WorkspaceStore"/>
    /// is shaped. Since this method replaced the save path, they have no production caller left
    /// — only tests — so do not read them as the established route for a new surface.
    /// </para>
    /// <para>
    /// <b>Whatever a new mutation surface writes through, it must then trigger a sink
    /// reconcile.</b> Writing publishers.json is only half of a record change: reading link
    /// health no longer reconciles, so nothing else will notice. The connections window's save
    /// and remove paths are the working example — each ends in the tray's
    /// <c>ReconcileSinksOffThread</c>, and they are currently the only two triggers keeping
    /// D25's live reload alive. A surface that calls a setter here and returns leaves the sink
    /// set stale until something else happens to reconcile.
    /// </para>
    /// <para>
    /// The supplied entry wins outright, its <see cref="PublisherEntry.Name"/> included, so a
    /// case-only correction to a machine name takes effect. The rename of an actually different
    /// name is a separate <see cref="Remove"/> of the old record, not this call: two records are
    /// two writes because they are two records.
    /// </para>
    /// </summary>
    public void Upsert(PublisherEntry entry)
    {
        var config = Load();
        var index = config.Publishers.FindIndex(p => NameEquals(p.Name, entry.Name));

        if (index >= 0)
        {
            config.Publishers[index] = entry;
        }
        else
        {
            config.Publishers.Add(entry);
        }

        Save(config);
    }

    /// <summary>Removes a link by name. No-op if not found. Its sessions are the caller's to evict (D21).</summary>
    public void Remove(string name)
    {
        var config = Load();

        if (config.Publishers.RemoveAll(p => NameEquals(p.Name, name)) > 0)
        {
            Save(config);
        }
    }

    /// <summary>Sets the local virtual desktop this machine's sessions activate to. Null clears the mapping.</summary>
    public void SetDesktopIndex(string name, int? desktopIndex) =>
        Update(name, entry => entry with { DesktopIndex = desktopIndex });

    public void SetMuted(string name, bool muted) =>
        Update(name, entry => entry with { Muted = muted });

    public void SetEnabled(string name, bool enabled) =>
        Update(name, entry => entry with { Enabled = enabled });

    private void Update(string name, Func<PublisherEntry, PublisherEntry> mutate)
    {
        var config = Load();
        var index = config.Publishers.FindIndex(p => NameEquals(p.Name, name));

        if (index >= 0)
        {
            config.Publishers[index] = mutate(config.Publishers[index]);
            Save(config);
        }
    }

    // Machine names are operator-typed and land in a filesystem-and-hostname world where
    // case is not meaningful; two entries differing only in case are the same machine.
    private static bool NameEquals(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
