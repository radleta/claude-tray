# Dashboard Inspect Schema (v1)

`imrdy inspect-live` returns a JSON document describing the live SessionDashboardForm control tree
and any layout diagnostics. Agents use this for automated UI verification.

## Top-level shape

```json
{
  "schemaVersion": "1",
  "verb": "inspect-live",
  "error": null,
  "render": null,
  "inspect": { ... }
}
```

| Field | Type | Notes |
|---|---|---|
| `schemaVersion` | `"1"` | Literal string; use to feature-detect breaking changes |
| `verb` | `string` | Echo of the request verb (`"inspect-live"`) |
| `error` | `string \| null` | Non-null on failure; `inspect` is null when error is set |
| `render` | `null` | Reserved for `render-live`; always null from `inspect-live` |
| `inspect` | `InspectResult \| null` | Populated on success |

### InspectResult

| Field | Type | Notes |
|---|---|---|
| `form` | `FormGeometry` | Screen geometry of the form at capture time |
| `tree` | `LayoutNode[]` | Flat BFS pre-order list; index 0 is the form itself |
| `diagnostics` | `DiagnosticFinding[]` | Zero or more findings; empty array means no issues detected |
| `diagnosticTimestamp` | ISO 8601 string | UTC timestamp of when the analysis ran |

### FormGeometry

| Field | Type | Notes |
|---|---|---|
| `formX`, `formY` | `int` | Screen position of the form |
| `formWidth`, `formHeight` | `int` | Form outer dimensions |
| `clientWidth`, `clientHeight` | `int` | Client area (inside borders/title bar) |
| `regionRadius` | `int` | Rounded-rect clip radius applied to `Form.Region` (14 for SessionDashboardForm) |

## LayoutNode fields

Each node is a control in the form tree.

| Field | Type | Notes |
|---|---|---|
| `type` | `string` | WinForms class name (`"Label"`, `"TableLayoutPanel"`, etc.) |
| `name` | `string` | `Control.Name`; empty string when unset |
| `text` | `string` | `Control.Text` truncated to 200 chars (trailing `"..."` when cut) |
| `boundsX`, `boundsY` | `int` | Position relative to form client origin |
| `boundsWidth`, `boundsHeight` | `int` | Control dimensions |
| `foreColor`, `backColor` | `string` | `"#RRGGBB"` or `"transparent"` (see Color encoding) |
| `fontName` | `string` | Font family name |
| `fontSize` | `float` | Font size in points |
| `fontStyle` | `string` | `Font.Style.ToString()` (e.g. `"Bold"`, `"Regular"`) |
| `anchor` | `string` | `AnchorStyles.ToString()` (e.g. `"Top, Left"`) |
| `dock` | `string` | `DockStyle.ToString()` (e.g. `"None"`, `"Fill"`) |
| `visible` | `bool` | `Control.Visible` at capture time |
| `paddingLeft/Top/Right/Bottom` | `int` | `Control.Padding` per-edge |
| `marginLeft/Top/Right/Bottom` | `int` | `Control.Margin` per-edge |
| `childIndexes` | `int[]` | Indexes into `tree[]` for direct children; empty for leaf nodes |
| `details` | `{string: string}` | Extra metadata; for `TableLayoutPanel` carries `"row[N]"` computed heights |

### Color encoding

- Hex `"#RRGGBB"` uppercase — e.g. `"#1E1E2E"`.
- `"transparent"` for `Color.Empty` or `Color.Transparent`.
- Alpha is dropped; do not rely on transparency from this field.

### Text truncation

`text` is capped at 200 characters. When truncated, the last 3 characters are replaced with `"..."`.
The raw truncation point is at character 197.

### Child-index discriminator

`childIndexes` references positions in the flat `tree[]` array (BFS pre-order). To reconstruct
the tree, for each node `n`, `n.childIndexes` gives the indexes of its direct children.
Index 0 is always the form root.

## DiagnosticFinding fields

| Field | Type | Notes |
|---|---|---|
| `kind` | `string` | Discriminator — see kinds below |
| `severity` | `string` | `"info"`, `"warning"`, or `"error"` |
| `controlPath` | `string` | Slash-separated path from form root to the offending control (e.g. `SessionDashboardForm/Panel[header]/Label[title]`) |
| `message` | `string` | Human-readable description |
| `details` | `{string: string}` | Supplementary key-value data (pixel measurements, thresholds, etc.); always present, never null — empty `{}` when unused |

### Finding kinds

| Kind | Severity | Trigger | Meaning |
|---|---|---|---|
| `regionClipRisk` | `"warning"` (no text) or `"error"` (has text) | Control bounds intersect a rounded-corner box | Control may be visually clipped by the rounded-rect region; `details` carries `corner` and `clippedPixels` |
| `siblingOverlap` | `"warning"` | Two sibling controls with positive area share overlapping bounds (after allow-list exclusions) | Controls are visually overlapping — likely a layout bug; `details` carries `controlA`, `controlB`, `overlapArea` |
| `edgeProximity` | `"info"` | Control is within 4 px of the form client edge | Content may be clipped at the visible boundary; `details` carries `edges` (comma-separated list of `left`, `top`, `right`, `bottom`) |
| `collapsedRow` | `"info"` | A `TableLayoutPanel` row height is `0` in the node's `details` map | Row collapsed — possibly a hidden section; `details` carries `rowIndex` |

## Versioning policy

- **v1** (current): all fields in this document. Additive changes (new fields, new `kind` values)
  are non-breaking within v1.
- **Breaking changes** (field removal, type change, semantic change) increment to v2.
- v1 and v2 will be co-served for at least one release cycle.
- Agents SHOULD check `schemaVersion` before processing; unknown versions indicate a future schema.

## Agent invocation

```sh
# Print to stdout
imrdy inspect-live abc123def456

# Write to file (stdout confirms path; stderr carries errors)
imrdy inspect-live abc123def456 --output /tmp/inspect.json

# Pipe to jq
imrdy inspect-live abc123def456 | jq .inspect.diagnostics
```

Exit codes: `0` success / `1` user-input error (session not found, bad args) / `2` infra error (tray not running).

## Sample response

```json
{
  "schemaVersion": "1",
  "verb": "inspect-live",
  "error": null,
  "render": null,
  "inspect": {
    "form": {
      "formX": -32000, "formY": -32000,
      "formWidth": 520, "formHeight": 392,
      "clientWidth": 520, "clientHeight": 392,
      "regionRadius": 14
    },
    "tree": [
      {
        "type": "SessionDashboardForm",
        "name": "",
        "text": "",
        "boundsX": 0, "boundsY": 0,
        "boundsWidth": 520, "boundsHeight": 392,
        "foreColor": "#E0E0E0", "backColor": "#1E1E2E",
        "fontName": "Segoe UI", "fontSize": 9.0, "fontStyle": "Regular",
        "anchor": "Top, Left", "dock": "None",
        "visible": true,
        "paddingLeft": 0, "paddingTop": 0, "paddingRight": 0, "paddingBottom": 0,
        "marginLeft": 3, "marginTop": 3, "marginRight": 3, "marginBottom": 3,
        "childIndexes": [1],
        "details": {}
      }
    ],
    "diagnostics": [
      {
        "kind": "collapsedRow",
        "severity": "info",
        "controlPath": "SessionDashboardForm/TableLayoutPanel[mainLayout]",
        "message": "row 4 collapsed",
        "details": { "rowIndex": "4" }
      }
    ],
    "diagnosticTimestamp": "2026-04-27T14:30:00.000Z"
  }
}
```

Error response (session not found):

```json
{
  "schemaVersion": "1",
  "verb": "inspect-live",
  "error": "session not found",
  "render": null,
  "inspect": null
}
```

## render-live verb

`imrdy render-live <session-id> --output <path>` captures the live SessionDashboardForm for a session
as a PNG. The `--output` path is **required** and must be absolute (the tray validates this
server-side).

### Request fields

| Field | Type | Notes |
|---|---|---|
| `verb` | `"render-live"` | Fixed |
| `sessionId` | `string` | ID of the session to render |
| `outputPath` | `string` | Absolute path for the PNG; null is rejected |

### Response fields

On success `render` is populated and `inspect` is null:

| Field | Type | Notes |
|---|---|---|
| `schemaVersion` | `"1"` | Same as inspect-live |
| `verb` | `"render-live"` | Echo |
| `error` | `null` | Null on success |
| `render.width` | `int` | PNG width in pixels |
| `render.height` | `int` | PNG height in pixels |
| `render.outputPath` | `string` | Absolute path of the written PNG |
| `inspect` | `null` | Always null for render-live |

### Error cases

| Error string | Exit code | Cause |
|---|---|---|
| `session id is required` | 1 | Empty session-id |
| `output path is required` | 1 | OutputPath null or empty |
| `output path must be absolute` | 1 | Relative path supplied |
| `session not found` | 1 | No session matches the ID |
| `Tray not running...` | 2 | Named pipe not present |

### Atomic-write semantics

The PNG is written to `<outputPath>.tmp` first, then renamed via `File.Move(..., overwrite: true)`.
The target path never contains a partial file; a killed-mid-render process leaves at most a `.tmp`
orphan, never a corrupt PNG at the target path.

### Agent invocation

```sh
imrdy render-live abc123def456 --output /tmp/live.png
# stdout: render-live: live.png 520x392
```

## links-live verb

`imrdy links` asks the running tray for live cross-machine link health, rather than re-deriving one
from `publishers.json` records it has no sinks behind (the user's ruling r-2). This verb is not
reachable as its own CLI command — `imrdy links` is its only caller, and it falls back to
records-only with exit 0 when no tray answers.

### Request fields

| Field | Type | Notes |
|---|---|---|
| `verb` | `"links-live"` | Fixed |
| `sessionId` | `string` | Unused; send `""` |
| `outputPath` | `null` | Unused |

### Response fields

On success `links` is populated and `render` / `inspect` are null:

| Field | Type | Notes |
|---|---|---|
| `schemaVersion` | `"1"` | Same as the other verbs — a new verb and a new nullable member are additive within major 1 |
| `verb` | `"links-live"` | Echo |
| `error` | `null` | Null on success |
| `links` | `ConnectionsViewModel` | The same live join the connections window renders: `machineName`, `listenEnabled`, `listenPort`, `authKeyConfigured`, and one `rows[]` entry per machine carrying both directions' `SinkHealth` |
| `render` / `inspect` | `null` | Always null for links-live |

`links` is null on every other verb's response, which is why the member carries a default: the
existing construction sites are untouched.

### The fallback is the normal production path

The pipe is gated on `diagnostics.ipcEnabled ?? File.Exists(~/.imrdy/.dev-build)`, so a shipped
install has no server unless the operator sets `diagnostics.ipcEnabled: true`. `imrdy links` treats
the absence as a state, not an error: it prints the records from `publishers.json`, exits 0, and
says which of the two it did on its `health:` line (stderr under `--json`). Exit 1 for a `Failed`
link is reachable only from a live response.
