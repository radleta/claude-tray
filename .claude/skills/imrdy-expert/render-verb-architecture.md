---
tags: [imrdy-expert/rendering]
summary: "imrdy render verb: in-process PNG capture of WinForms surfaces without a screen — layer split, Program.cs placement, DrawToBitmap caveats, sequential STA execution"
code-cites: []
---

# Render Verb Architecture

## Overview

`imrdy render <component> [--output <path> | --output-dir <dir>]` produces deterministic PNG artifacts of WinForms UI surfaces without a live screen or running tray process. Four registered components (`RenderRegistry.Components`), all via `Form.DrawToBitmap`:

| Component | Form | Fixture directory |
|-----------|------|-------------------|
| `dashboard` | `SessionDashboardForm` | `tests/fixtures/dashboards` (14) |
| `workspace-dashboard` | `WorkspaceDashboardForm` | `tests/fixtures/workspace-dashboards` (2) |
| `overlay` | `OverlayPanel` | `tests/fixtures/overlays` (5) |
| `connections` | `ConnectionsForm` via `NullConnectionsHost` | `tests/fixtures/connections` (2) |

`imrdy render --all` therefore produces **23** PNGs. `ConnectionsRenderer` pins an explicit `ClientSize` because that window is resizable, unlike the other three.

Key commands:
- `imrdy render <component> <fixture.json>` — render a single fixture
- `imrdy render --list` — enumerate registered components
- `imrdy render --all [--output-dir <dir>]` — render every fixture of every component

**Adding a fixture or a component is a two-place change.** `RenderCommandAllTests` hardcodes the per-component fixture counts *and* a summary-line prefix filter. Miss the filter and the PNG assertion passes while the summary-line assertion fails by exactly the new fixture count — which reads as "the renders did not run" when they did.

## Layer Split (D1)

Pure contracts live in `Imrdy.Core/Rendering/`:
- `IRenderableSurface` — interface a form implements to be renderable
- `RenderContext` — input (fixture path, output path, options)
- `RenderResult` — output (image, timing, success/failure)

No WinForms types cross into Core. Concrete renderer implementations and `RenderRegistry` live in `Imrdy.Windows/Rendering/` (WinForms-dependent). `RenderCommand` and all concrete `IRenderableSurface` impls (e.g., `DashboardSurface`) live in `Imrdy.Windows/`.

This mirrors the existing Core/Windows split everywhere else in the project — Core is the stable API surface; Windows is the platform-specific implementation.

## Program.cs Branch Placement

The `"render"` branch is placed BETWEEN `preview-dashboard` and the bare-tray fallback:

1. `hook` — fast path, no WinForms
2. `CommandRouter` — Spectre CLI, no WinForms
3. `preview-dashboard` — WinForms dev tool, bypasses mutex
4. `render` — WinForms dev tool, bypasses mutex  ← here
5. Tray — full app, mutex-gated

The Spectre CLI branches skip WinForms init; render needs it (STA thread + visual styles + `Application.SetHighDpiMode`). The render branch re-uses the same three WinForms init lines as preview-dashboard.

## Mutex Bypass Rationale

`Global\ImrdyMonitor` is NOT checked for render (same as preview-dashboard). Render is a dev tool that must run while the live tray is running — after `build-dev.sh` deploys a new binary, the dev immediately runs `imrdy render --all` to inspect PNG output before filing a verdict. Requiring the tray to stop first would break the dev workflow.

## DrawToBitmap Caveats

`Form.DrawToBitmap` has several non-obvious requirements:

- **`CreateControl()` required** — the form handle must be created even though the form is never shown
- **`PerformLayout()` required** — must be called after `CreateControl()` or child controls have zero-size bounds and render as blank
- **`Size` must be set** — the form's client size is used as the bitmap dimensions; default is 300×300
- **DWM mica/acrylic does NOT render** — `DrawToBitmap` captures only GDI+ content; the backdrop is applied via `DwmSetWindowAttribute` which targets the compositor, not the GDI layer; rendered PNGs show the standard WinForms background color instead of mica
- **Font rendering uses GDI+ metrics, not ClearType** — output is representative but not pixel-identical to on-screen rendering

## Sequential STA Execution (D5)

All fixtures for all components render sequentially on the main STA thread. No parallelism. `Form.DrawToBitmap` is not thread-safe, and STA-affinity of WinForms controls cannot be bypassed. SIGINT between fixtures cancels with exit code 130 (standard Unix convention for SIGINT cancellation).

## Default Output Directory Resolution

When `--output-dir` is not specified:

1. If `~/.imrdy/.dev-build` marker exists → `{repoRoot}/scratch/views/{component}/` (dev build path — keeps PNGs in scratch where they're visible without polluting cwd)
2. Otherwise → `./` (current working directory fallback)

`repoRoot` is detected by walking up from the imrdy binary location looking for a `.git` directory.

## Inline DI (D3)

`RenderCommand.Run` uses an inline `ServiceCollection` (same as `PreviewDashboardCommand`) rather than a shared service builder. The extract-on-third-caller rule applies: `HookServiceBuilder` and `MonitorServiceBuilder` exist because the tray and hook are distinct long-running processes. Preview-dashboard and render are both short-lived dev tools — extracting a shared builder for two callers would be premature abstraction.

## Visual Seal Protocol

For any UI-bearing change (SessionDashboardForm, WorkspaceDashboardForm, overlay, tray icons, menus):

1. Build succeeds
2. Unit and integration tests pass
3. Verifier wave APPROVED (completeness/quality/security)
4. **Run `imrdy render --all` and inspect every PNG** — mandatory fourth seal

A passing verifier wave is NOT a substitute for visual inspection. Layout-collapse bugs (controls rendered at zero size) pass all three verifier gates cleanly. See the user-scoped `verify-fix-loop-expert` wiki for the full four-gate protocol.

### PATH Binary Staleness Gotcha

The bare `imrdy` command on PATH resolves to `~/.local/bin/imrdy.exe`, the deployed binary from the last `build-dev.sh` run. `dotnet build` only rebuilds the Debug assembly in `bin/Debug/.../imrdy.exe` — it does NOT touch the PATH binary. This means:

- **`dotnet build` + `imrdy render --all`** = renders with yesterday's (or older) binary
- **`./build-dev.sh` + `imrdy render --all`** = renders with today's source

If a visual-seal step runs `imrdy render` via the bare PATH command without first running `./build-dev.sh`, the render will silently use a stale deployed binary, producing false-positive "defects" (visual artifacts from old code that are already fixed in source).

**Solution:** Always run `./build-dev.sh` immediately before `imrdy render --all` during visual-seal verification. Alternatively, invoke the just-built Debug exe by its full bin path: `src/Imrdy.Windows/bin/Debug/net10.0-windows/win-x64/imrdy.exe render --all`.

**Discovery:** During step 3 iteration 2, the visual seal reported a missing grip handle and wrong panel widths (72×72 / 360×72). The source in `OverlayPanel.cs` already had the grip correctly implemented. Running `./build-dev.sh` to redeploy the binary fixed the PATH binary; re-running `imrdy render --all` then showed the correct output (86×72 / 374×72) with the grip visible — no source change was needed.

## Deferred

Currently omitted:
- `--json` output (machine-readable metadata)
- `--quiet` / `--verbose` / `--version` flags

These defer per D8 (add on first external consumer, not speculatively).

## Related

- [Dev Build Marker & Logging](dev-build-marker-logging.md) — `.dev-build` controls both default output dir and debug logging
- [Hover Dashboard Form Lifecycle](hover-dashboard-form-lifecycle.md) — `SessionDashboardForm` is the Phase 1 render target; form lifecycle constraints apply equally to preview and render paths
