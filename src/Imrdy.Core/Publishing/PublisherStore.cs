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
