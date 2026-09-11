using Imrdy.Core.Publishing;

namespace Imrdy.Core.Diagnostics;

/// <summary>
/// Top-level IPC response envelope for every verb the tray's inspect pipe serves.
/// </summary>
/// <param name="SchemaVersion">Schema version string, e.g. <c>"v1"</c>.</param>
/// <param name="Verb">Echo of the request verb (<c>"render-live"</c>, <c>"inspect-live"</c> or <c>"links-live"</c>).</param>
/// <param name="Error">Non-null when the request failed; null on success.</param>
/// <param name="Render">Populated for <c>render-live</c> success responses; null otherwise.</param>
/// <param name="Inspect">Populated for <c>inspect-live</c> success responses; null otherwise.</param>
/// <param name="Links">
/// Populated for <c>links-live</c> success responses; null otherwise. It carries the tray's own
/// <see cref="ConnectionsViewModel"/> — the live join the connections window renders — so
/// <c>imrdy links</c> shows real link health rather than re-deriving one from records it has no
/// sinks behind (the user's ruling r-2). Defaulted so the other verbs' construction sites are
/// untouched: a new nullable member is additive within schema major 1.
/// </param>
public record InspectResponse(
    string SchemaVersion,
    string Verb,
    string? Error,
    RenderResult? Render,
    InspectResult? Inspect,
    ConnectionsViewModel? Links = null);

/// <summary>
/// Render artifact metadata returned by render-live.
/// </summary>
/// <param name="Width">Width of the rendered PNG in pixels.</param>
/// <param name="Height">Height of the rendered PNG in pixels.</param>
/// <param name="OutputPath">Absolute path to the written PNG file.</param>
public record RenderResult(int Width, int Height, string OutputPath);

/// <summary>
/// Full layout tree and diagnostic analysis returned by inspect-live.
/// </summary>
/// <param name="Form">Screen geometry of the inspected form.</param>
/// <param name="Tree">
/// Flat list of all controls in the form, depth-first pre-order.
/// <see cref="LayoutNode.ChildIndexes"/> reference positions within this list.
/// </param>
/// <param name="Diagnostics">Zero or more findings from the layout analyzer.</param>
/// <param name="DiagnosticTimestamp">ISO 8601 UTC timestamp of when the analysis was captured.</param>
public record InspectResult(
    FormGeometry Form,
    IReadOnlyList<LayoutNode> Tree,
    IReadOnlyList<DiagnosticFinding> Diagnostics,
    string DiagnosticTimestamp);
