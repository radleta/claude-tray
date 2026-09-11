---
tags: [imrdy-expert/config, imrdy-expert/validation]
summary: "ConfigValidator keeps its own known-keys sets, compiler-unenforced — a new config.json section is a three-touch change (ImrdyConfig, EnsureDefaults, ConfigValidator) and step 3 was skipped twice before D33 closed it"
---

## ConfigValidator's Known-Keys Lists Are a Third, Compiler-Unenforced Touch

`ConfigValidator` (`src/Imrdy.Core/Validation/ConfigValidator.cs`) maintains its own
independent `KnownRootKeys` / `Known{Section}Keys` hash sets, used by `imrdy config validate`
to warn on unrecognized JSON keys in `config.json`. Nothing ties them to `ImrdyConfig` at
compile time.

**Adding a new top-level config section is a three-touch change**, mirroring the
`FieldPreservation.PreserveFields` symmetry contract for session state:

1. Add the record to `ImrdyConfig`
2. Handle it in `ConfigReader.EnsureDefaults` (defaults, clamps)
3. Add its key to `ConfigValidator.KnownRootKeys` **plus** a `Known{Section}Keys` set, wired
   through `TryValidateSection`

Step 3 is the one that gets skipped, because skipping it still compiles and still round-trips
correctly through `ConfigReader`. The only symptom is `imrdy config validate` reporting
`Unknown key: '<section>' (possible typo)` on a legitimate section.

**History:** it was skipped twice. `KnownRootKeys` held only `tray` and `sound` long after
`Overlay` and `Diagnostics` shipped, and `tray.iconStyle` was missing from `KnownTrayKeys`.
D33 (cross-machine-publish) closed all of it while adding `network`: `overlay`, `diagnostics`
and `network` all have key sets now, `iconStyle` joined the tray set, and the repeated
per-section unknown-key loop was extracted into one `TryValidateSection` helper — so the
fourth section costs one call plus one set, not another copy of the loop.

**Coverage:** `ConfigValidatorTests` now pins the contract — a config using every real section
warns about nothing, an unknown key inside a section warns, and a non-object section errors.
The first of those is the regression test for the gap itself: it fails the moment a new
`ImrdyConfig` section lands without its key set.

**Source:** [ConfigValidator.cs](../../../src/Imrdy.Core/Validation/ConfigValidator.cs)

**Discovered:** brainstorming/research — cross-machine session publishing layer investigation
(new config surfaces question). Resolved by D33 in the same build.

**Impact:** The gap that existed at discovery is closed, but the *mechanism* that produced it
is unchanged — there is still no compile-time link between `ImrdyConfig` and `ConfigValidator`.
Treat step 3 as part of the definition of "added a config section."
