using System.Text;
using System.Text.Json;

namespace Imrdy.Core.Publishing;

/// <summary>
/// Framing and version rules for the TCP wire: one JSON object per line, UTF-8, newline
/// terminated. Both ends read and write through here so the limits and the version test have
/// one definition.
/// </summary>
public static class WireProtocol
{
    /// <summary>
    /// The schema major this build speaks. Additive changes stay in major 1; a field removal,
    /// a type change or a semantic change is major 2 (D28).
    /// </summary>
    public const string SchemaVersion = "1";

    /// <summary>
    /// Longest line either end will handle, newline included. A longer line is a corrupt or
    /// hostile peer rather than a large session, so the receiver closes the connection with a
    /// logged reason instead of growing a buffer to fit it.
    /// </summary>
    public const int MaxLineBytes = 64 * 1024;

    /// <summary>Serializes one frame to its wire line, newline included.</summary>
    public static byte[] Serialize(WireFrame frame)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(frame, ImrdyJsonContext.Default.WireFrame);
        var line = new byte[json.Length + 1];
        json.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        return line;
    }

    /// <summary>
    /// Parses one wire line. Returns false on anything that is not a JSON object carrying a
    /// <c>type</c>; the caller logs and skips rather than tearing the connection down, since a
    /// frame this build does not understand is the normal shape of a peer one version ahead.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> line, out WireFrame? frame)
    {
        try
        {
            frame = JsonSerializer.Deserialize(line, ImrdyJsonContext.Default.WireFrame);
            return frame is not null && !string.IsNullOrEmpty(frame.Type);
        }
        catch (JsonException)
        {
            frame = null;
            return false;
        }
    }

    /// <inheritdoc cref="TryParse(ReadOnlySpan{byte}, out WireFrame?)"/>
    public static bool TryParse(string line, out WireFrame? frame) =>
        TryParse(Encoding.UTF8.GetBytes(line), out frame);

    /// <summary>
    /// The major of a declared schema version, or null when it is absent or unparseable. A
    /// peer whose major differs from <see cref="SchemaVersion"/> is refused; one that only
    /// differs after the dot is speaking an additive revision of the same major and is kept.
    /// </summary>
    public static int? SchemaMajor(string? schemaVersion)
    {
        if (string.IsNullOrWhiteSpace(schemaVersion))
        {
            return null;
        }

        var dot = schemaVersion.IndexOf('.');
        var major = dot < 0 ? schemaVersion : schemaVersion[..dot];

        return int.TryParse(major, out var value) ? value : null;
    }

    /// <summary>True when the peer's declared version is one this build can speak.</summary>
    public static bool IsCompatible(string? schemaVersion) =>
        SchemaMajor(schemaVersion) == SchemaMajor(SchemaVersion);
}
