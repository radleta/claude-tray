---
name: imrdy-expert
description: "imrdy project knowledge base — architecture decisions and behavior discovered from real usage."
---

You are an expert in the imrdy project — a Windows system tray monitor for Claude Code sessions (.NET 10, WinForms, single executable). Use the wiki below as your knowledge base. For deeper detail on any topic, use the Read tool on the linked pages.

## Pages

- [Architecture](architecture.md) — Seven entry points, timer interactions, field preservation, and state file lifecycle
- [State File Write Path](state-file-write-path.md) — Session state files use direct File.WriteAllBytes — not AtomicFileWriter — because delete-then-move suppresses FSW Changed events
- [Config Live Reload](config-live-reload.md) — config.json FSW routes through OnConfigChanged for full live reload (sound + icon style + tray god toggle + overlay); overlay structural-delta: Position/Monitor/Locked/OffsetX/OffsetY apply in-place, Enabled/Size/Spacing recreate; startup uses LoadSoundConfig separately
- [Tray vs Hook Write Race](tray-hook-write-race.md) — Hook and tray both RMW session state files with no coordination — tray-side field changes are silently dropped if the field isn't on the FieldPreservation list
- [Tray Persistence Verbs](tray-persistence-verbs.md) — Catalog of every place the tray process writes JSON state to disk — a debugging checklist for persistence loss
- [Field Preservation Catalog](field-preservation-catalog.md) — The 6 sticky fields in FieldPreservation.PreserveFields, the merge pattern, and the symmetry contract every new tray-owned field must satisfy
- [WT Desktop Routing](wt-desktop-routing.md) — SwitchToSessionDesktop 3-step routing: resolve target → switch desktop → guarded focus. WT skipped from dynamic lookup; ForceForeground guarded against ping-pong; auto-lock on SessionStart only
- [Hook Events](hook-events.md) — All 20 Claude Code hook events — what they send, status mapping, the background_tasks roster on Stop/SubagentStop and its type-dependent entry shape, how to grep the tasks= token without reading your own echo, and real-world behavior
- [Teammate Detection](teammate-detection.md) — How imrdy reads the background_tasks roster Claude Code sends: the agent_id gate keeps subagents from moving lead status, Stop/SubagentStop supply running_tasks, and DisplayStatus.Resolve renders an idle lead with a non-empty roster as teal
- [Notification Dwell](notification-dwell.md) — Dwell timer system that gates toast/sound behind status settling — prevents notification storms
- [Status Mapping](status-mapping.md) — Two-layer status mapping: hook event → base status → RGB color, with 9 base statuses
- [Overlay Interactivity](overlay-interactivity.md) — DragCompleted event fires at end of drag-to-reposition in OnMouseUp; companion to SurfaceInteracted with separate contract; subscription lifecycle identical (P6 TrayApp owns wiring)
- [Hover Dashboard Form Lifecycle](hover-dashboard-form-lifecycle.md) — HoverDashboardFormBase owns the shared shell (DWM, focus guard, pin/unpin, anchor placement); derived forms (SessionDashboardForm, WorkspaceDashboardForm) own their content panels — field-promote all dynamic controls for Update(vm) access
- [Hover Dashboard State Machine](hover-dashboard-state-machine.md) — HoverDashboardControllerBase owns the dwell/grace state machine; derived controllers plug in domain-specific dispatch (TryHitTestForOurDomain → BuildViewModel → CreateForm → ShowForm → ApplyViewModelUpdate); cross-controller hide protocol via FormShown event wired in TrayApp
- [Workspace Dashboard Architecture](workspace-dashboard-architecture.md) — WorkspaceDashboardForm + WorkspaceHoverDashboardController: BuildViewModel hit-index flow, VM-as-render-contract, live 'ago' refresh, Update-refresh-all-fields pattern, GitInfo Ahead/Behind, cross-controller hide via FormShown
- [VM-as-Complete-Render-Contract](vm-as-complete-render-contract.md) — VM-as-complete-render-contract: builders take explicit 'now' parameter; forms/renderers have zero clock reads; visual seal detected the clock-leak pattern when workspace ActivityText diverged hours after baseline capture
- [WinForms Update Field-Promote](winforms-update-field-promote.md) — Field-promote all dynamic WinForms controls for Update(vm) access; BuildLayout/Update split; SetRowVisible for conditional rows; chip list clear+rebuild — workspace→workspace stale-fields bug was caused by missing field promotion
- [Dev Build Marker & Logging](dev-build-marker-logging.md) — Touch ~/.imrdy/.dev-build marker after dev deploys to enable Debug logging on all imrdy processes; enables diagnostic traces without env var friction
- [Sparkline Reference Time](sparkline-reference-time.md) — SparklineControl requires a reference time anchor for correct rendering in live and fixture-preview paths
- [WinForms Custom Property Serialization](winforms-custom-property-serialization.md) — UserControl public properties of non-serializable types require DesignerSerializationVisibility attribute to avoid WFO1000 build error
- [xunit Parallel Console Redirects](xunit-parallel-console-redirect.md) — xunit v2 parallel test classes compete over Console.Out/Error redirects — use [Collection] attribute to serialize
- [Render Verb Architecture](render-verb-architecture.md) — imrdy render verb: in-process PNG capture of WinForms surfaces without a screen — layer split, Program.cs placement, DrawToBitmap caveats, sequential STA execution
- [TableLayoutPanel Row Toggle](tablelayoutpanel-row-toggle.md) — TableLayoutPanel row toggling via Absolute height 0; MinimumSize (not Width) pins fixed width with AutoSize=GrowAndShrink
- [Persona Chip Horizontal Budget](persona-chip-horizontal-budget.md) — WinForms Anchor-based layouts: invisible-but-present sibling controls reduce available width for Anchor=Left|Right peers
- [DrawToBitmap Alpha Compositing](drawtobitmap-alpha-compositing.md) — DrawToBitmap requires higher alpha for decorative lines than runtime DWM compositing
- [DisplayItem vs SessionEntry Identity](display-item-vs-session-identity.md) — DisplayItem uses Id + ItemType; SessionEntry has SessionId. Choose based on context. Full DisplayItem field reference included.
- [HookAccumulationStore Apply from FSW](hookaccumulationstore-apply-from-fsw.md) — Construct HookEventModel from StateFileModel when calling Apply from FSW path.
- [Hover-Preview Live-Switch Detection](hover-switch-live-update.md) — Detect session change via TryGetSessionIdAtScreenPoint while form is visible; apply live-update pattern
- [Z-Order Gate Obsoletes Overlay-Hide-on-Menu](z-order-gate-obsoletes-menu-paper-over.md) — WindowFromPoint z-order gating makes overlay-hide-on-menu paper-overs redundant; pure geometric containment is insufficient
- [Tray IPC: render-live and inspect-live](inspect-ipc.md) — Tray IPC: render-live and inspect-live verbs — pipe protocol, dev-default gate, walker+analyzer, threading model, ACL
- [WSL→Windows PATH Passthrough Baseline](wsl-interop-baseline.md) — WSL→Windows PATH passthrough varies per distro; explicit verification needed
- [WSLENV Distro Identity Gap](wslenv-distro-not-forwarded.md) — WSLENV doesn't auto-forward WSL_DISTRO_NAME; Windows binaries can't self-identify source distro
- [WSL_DISTRO_NAME Env Var Gotcha](wsl-distro-env-var-gotcha.md) — WSL_DISTRO_NAME env var requires explicit pickup via IHookEnvironment; code exists but fallback was never wired
- [HookServiceBuilder Relocation](hook-service-builder-relocation.md) — HookServiceBuilder relocated from Imrdy.Windows.DI to Imrdy.Core.Hooks to enable cross-platform consumer access
- [overlay-rendering-internals](overlay-rendering-internals.md) — OverlayPanel OnPaint rendering; bitmap cache keyed by (style,status); aging via chip-background opacity ladder in OnPaint; Form.Bounds reliability on non-layered forms; monitor/position placement reads mutable _monitor/_position fields, not config directly
- [displayitem-source-gen-gotcha](displayitem-source-gen-gotcha.md) — ImrdyJsonContext must explicitly register DisplayItem and List<DisplayItem> for source-gen serialization
- [stj-source-gen-interface-caveat](stj-source-gen-interface-caveat.md) — STJ source-gen registers concrete List<T> but callers must query by concrete type, not interface
- [render-fixture-offscreen-pattern](render-fixture-offscreen-pattern.md) — Offscreen-Show pattern for deterministic Form fixture rendering in imrdy tests
- [internals-visible-to-mechanism](internals-visible-to-mechanism.md) — Imrdy.Core grants InternalsVisibleTo to assembly named 'imrdy' (the Imrdy.Windows project) — not 'Imrdy.Windows'. Internal Core classes (e.g. ControllerMenuModel) are accessible from Windows code without making them public. Use 'imrdy' (lowercase) as the assembly-name key in any future InternalsVisibleTo grant from Core.
- [overlay-placement-taskbar-reserve](overlay-placement-taskbar-reserve.md) — OverlayPlacement applies bottom taskbar reserve unconditionally, unlike the original CalculatePosition
- [render-output-dir-flat-layout](render-output-dir-flat-layout.md) — `imrdy render --all --output-dir` writes files flat despite grouped console output — all PNGs land directly in output-dir, not in component subdirectories
- [overlay-context-menu-foreground-dance](overlay-context-menu-foreground-dance.md) — AtControl context menus get foreground from an explicit SetForegroundWindow + InvokeWithForegroundAttached dance, not a WM_MOUSEACTIVATE exception — WndProc still returns MA_NOACTIVATE unconditionally
- [overlay/mouseactivate-foreground-capture-timing](overlay/mouseactivate-foreground-capture-timing.md) — WM_MOUSEACTIVATE completes the foreground switch before the triggering button-down is delivered, so OnMouseDown/OnMouseUp run after activation — the reason a self-activating window cannot observe the user's prior foreground window
- [testing/winforms-menu-tests-need-real-message-loop](testing/winforms-menu-tests-need-real-message-loop.md) — MenuRenderer.Apply asserts Application.MessageLoop, which a bare STA thread does not satisfy — ContextMenuStrip.Opening tests need a real Application.Run pump or the assert is swallowed as a misleading zero-items failure
- [subagentstop-roster-usually-names-a-sibling](subagentstop-roster-usually-names-a-sibling.md) — SubagentStop rosters typically name sibling agents, not self-inclusion — compare agent_id carefully
- [overlay-empty-fixture-renders-a-placeholder-chip](overlay-empty-fixture-renders-a-placeholder-chip.md) — The overlay empty fixture renders a visible placeholder chip — intended by Decision 6
- [verbatim-capture-examples-leak-into-committed-docs](verbatim-capture-examples-leak-into-committed-docs.md) — Copying examples verbatim out of evidence captures can move real third-party content into committed pages
- [scoped-rederivation-leaves-stale-neighbours](scoped-rederivation-leaves-stale-neighbours.md) — Scoping artifact corrections to one source file leaves stale neighbor citations untouched
- [claude-local-md-is-gitignored-local-memory](claude-local-md-is-gitignored-local-memory.md) — CLAUDE.local.md is gitignored local working memory — it may or may not exist on any given machine, and git status --short will never list it, so any acceptance criterion asserting otherwise is unsatisfiable
- [mdite-heading-anchor-slugs-drop-inline-code](mdite-heading-anchor-slugs-drop-inline-code.md) — mdite strips inline-code spans (backticks and their contents) from heading text before slugifying, so its anchors diverge from GitHub's — a heading with inline code cannot satisfy both renderers
- [build-dev-cross-platform](build-dev-cross-platform.md) — build-dev.sh already OS-detects and publishes a Linux hook-only binary to ~/.local/bin/imrdy — no tray, atomic swap via temp-in-same-dir + mv
- [config-validator-known-keys-gap](config-validator-known-keys-gap.md) — ConfigValidator.KnownRootKeys only recognizes tray and sound — overlay and diagnostics, both real ImrdyConfig sections, are flagged as unknown-key warnings

## Meta

- [Operations Log](log.md) — Timestamped wiki operations log (ingest, lint, query filings)
- [Schema](schema.md) — Wiki conventions and page-type definitions
