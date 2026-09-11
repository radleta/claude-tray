using System.Text.Json;

namespace Imrdy.Core.State;

/// <summary>
/// Reads and writes session state JSON files with BOM handling and error tolerance.
/// </summary>
public sealed class StateFileReader
{
    /// <summary>
    /// Reads a state file from disk. Returns null if the file doesn't exist,
    /// is corrupt, or is mid-write.
    /// </summary>
    public StateFileModel? ReadStateFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            bytes = StripBom(bytes);
            return JsonSerializer.Deserialize(bytes, ImrdyJsonContext.Default.StateFileModel);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes a state file directly. Uses UTF-8 without BOM.
    /// Direct write (not temp+rename) ensures FileSystemWatcher fires Changed events.
    /// The JSON reader handles partial reads gracefully, and files are small (~300 bytes).
    /// </summary>
    public void WriteStateFile(string path, StateFileModel model)
    {
        Write(path, JsonSerializer.SerializeToUtf8Bytes(model, ImrdyJsonContext.Default.StateFileModel));
    }

    /// <summary>
    /// Writes a state file only when its bytes would differ from what is already there.
    /// <para>
    /// For the remote-ingest seam, where a write is never free: it is a direct write by design
    /// (D15), so every one of them raises a <c>Changed</c> on the receiver's watcher and travels
    /// the whole drain pipeline — and a TCP publisher re-sends its entire snapshot on every
    /// dial, over a sessions directory nothing sweeps. Unconditional, that is N reads, N writes
    /// and N watcher events per reconnect for a receiver whose state did not move. The skip
    /// changes no delivery semantic: a session that genuinely changed still writes, byte-for-
    /// byte the same write it would have been, and a removal still removes.
    /// </para>
    /// <para>
    /// The comparison is against the file's own bytes rather than against a re-serialized model
    /// so that "unchanged" means what the watcher means by it. A file that cannot be read back
    /// is treated as different and written, which is the same tolerance
    /// <see cref="ReadStateFile"/> already has for a torn or corrupt file.
    /// </para>
    /// </summary>
    public void WriteStateFileIfChanged(string path, StateFileModel model)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(model, ImrdyJsonContext.Default.StateFileModel);

        try
        {
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(json))
            {
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Mid-write, locked, gone since the Exists check, or unreadable — fall through and
            // write. Same pair FileSink guards its own file operations with.
        }

        Write(path, json);
    }

    private static void Write(string path, byte[] json)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(path, json);
    }

    /// <summary>
    /// Reads all state files from a sessions directory.
    /// </summary>
    public IReadOnlyList<StateFileModel> ReadAllStateFiles(string sessionsDir)
    {
        if (!Directory.Exists(sessionsDir))
        {
            return [];
        }

        var results = new List<StateFileModel>();
        foreach (var file in Directory.GetFiles(sessionsDir, "*.json"))
        {
            var model = ReadStateFile(file);
            if (model is not null)
            {
                results.Add(model);
            }
        }

        return results;
    }

    /// <summary>
    /// Removes a state file and its associated PID cache file.
    /// </summary>
    public void RemoveStateFile(string sessionsDir, string sessionId)
    {
        var statePath = Path.Combine(sessionsDir, $"{sessionId}.json");
        TryDelete(statePath);

        var pidPath = Path.Combine(sessionsDir, $".pid-{sessionId}");
        TryDelete(pidPath);
    }

    /// <summary>
    /// Strips UTF-8 BOM bytes (0xEF, 0xBB, 0xBF) from the start of a byte array.
    /// Legacy PS1-touched files may have BOM bytes.
    /// </summary>
    private static byte[] StripBom(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return bytes[3..];
        }

        return bytes;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup — file may be locked
        }
    }
}
