---
tags: [imrdy-expert/config, imrdy-expert/validation]
summary: "ConfigValidator.KnownRootKeys only recognizes tray and sound — overlay and diagnostics, both real ImrdyConfig sections, are flagged as unknown-key warnings"
---

## ConfigValidator Known-Keys List Lags ImrdyConfig's Actual Sections

`ConfigValidator` (`src/Imrdy.Core/Validation/ConfigValidator.cs:10-27`) maintains its own
independent `KnownRootKeys` / `KnownTrayKeys` / `KnownSoundKeys` hash sets used by `imrdy config
validate` to warn on unrecognized JSON keys in `config.json`. `KnownRootKeys` contains only `tray`
and `sound`:

```csharp
private static readonly HashSet<string> KnownRootKeys = new(StringComparer.OrdinalIgnoreCase)
{
    "tray",
    "sound",
};
```

But `ImrdyConfig` (`src/Imrdy.Core/ImrdyConfig.cs`) has grown two more top-level sections since —
`Overlay` and `Diagnostics` — neither added to `ConfigValidator`. A `config.json` with a legitimate
`"overlay": {...}` or `"diagnostics": {...}` section round-trips correctly through `ConfigReader`
(which knows about both — see `EnsureDefaults`), but `imrdy config validate` flags either key as
`Unknown key: 'overlay' (possible typo)` / `Unknown key: 'diagnostics' (possible typo)`. There is no
`KnownOverlayKeys` or `KnownDiagnosticsKeys` set at all, so even the individual fields inside those
sections (`enabled`, `position`, `size`, `spacing`, `monitor`, `locked`, `offsetX`, `offsetY`,
`ipcEnabled`) go unchecked. `ConfigValidatorTests.cs` has no test coverage for either section,
consistent with the gap being unaddressed rather than intentionally deferred.

**Impact:** Adding a new top-level config section (e.g. a publisher/connections surface) requires a
three-touch change to stay validated, mirroring the `FieldPreservation.PreserveFields` symmetry
contract for session state: (1) add the record to `ImrdyConfig`, (2) handle it in
`ConfigReader.EnsureDefaults`, and (3) add its key set to `ConfigValidator.KnownRootKeys` plus a new
`Known{Section}Keys` set — step 3 is easy to skip since nothing enforces it at compile time, and the
existing `overlay`/`diagnostics` gap shows it already has been skipped twice.

**Source:** [ConfigValidator.cs:10-27](../../../src/Imrdy.Core/Validation/ConfigValidator.cs)

**Discovered:** brainstorming/research — cross-machine session publishing layer investigation (new
config surfaces question)
**Impact:** A new config section for cross-machine publishing needs an explicit `ConfigValidator`
update or it will silently emit spurious "unknown key" warnings on `imrdy config validate` runs.
