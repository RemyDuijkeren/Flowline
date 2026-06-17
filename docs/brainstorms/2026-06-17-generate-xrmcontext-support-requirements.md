---
date: 2026-06-17
topic: generate-xrmcontext-support
---

# flowline generate — XrmContext Generator Support

## Summary

Extend `flowline generate` with a `--generator` flag that lets users select XrmContext as an alternative to PAC `modelbuilder build`. The choice persists in `.flowline` per-solution. XrmContext is auto-restored from NuGet on first use and receives Flowline's existing Dataverse connection — no separate auth setup required.

---

## Problem Frame

PAC `modelbuilder build` generates verbose early-bound classes that closely mirror the Dataverse SDK type system: OptionSetValue wrappers, Money types, INotifyPropertyChanged boilerplate. XrmContext generates the same coverage with cleaner C# idioms — option sets become enums directly, Money becomes decimal, files are compact. For teams already using XrmContext's style, maintaining PAC-generated code is friction without benefit.

---

## Key Decisions

**XrmContext over DLAB EBG V2.** DLAB EBG V2 wraps PAC's DataverseModelBuilder — its output is PAC-style with extras (parallel enum properties, attribute name constants). It doesn't deliver the type-abstraction philosophy that motivates this feature. XrmContext is the right alternative for clean-style generation, with the maintenance risk (last release 2022, .NET Framework only) noted as accepted.

**No generator abstraction yet.** A formal generator interface is deferred until a third generator materializes (e.g., a future Flowline-native generator). The change lands as a simple conditional branch with XrmContext logic in a dedicated class — structured so extraction is cheap later without adding abstraction now.

**Auto-restore from NuGet.** Flowline downloads and extracts `Delegate.XrmContext` on first use, cached locally. No manual installation required.

**Reuse Flowline's Dataverse connection.** XrmContext receives a connection string derived from Flowline's existing `ServiceClient` — single auth source, no duplicate configuration.

**Skip custom API generation for XrmContext.** XrmContext is entity-focused; PAC custom API message generation has no XrmContext equivalent. When XrmContext is active, custom API discovery runs but its results are discarded.

**`--generator` flag values leave room for `flowline`.** Allowed values are `pac` and `xrmcontext`. A future Flowline-native generator would use `flowline` — naming this now avoids a breaking rename later.

**XrmContext binary cached in user profile.** The restored binary is shared across all Flowline projects on the machine, not stored per-project. Avoids redundant downloads when multiple solutions use XrmContext.

**XrmContext version resolved to latest at restore time.** No version pinning — Flowline fetches the latest `Delegate.XrmContext` release when the cache is empty or the binary is absent.

---

## Requirements

**Generator selection**

- R1. `flowline generate` accepts a `--generator` flag with allowed values `pac` and `xrmcontext`; default is `pac` when neither flag nor config is present.
- R2. The generator choice is storable in `.flowline` under `generate.generator` per solution, written on first use with `--generator` (consistent with how namespace and extra-tables are saved today).
- R3. A CLI `--generator` flag overrides the `.flowline` config default for that run without modifying the stored value unless `--save` is also passed.

**XrmContext availability**

- R4. When XrmContext is selected and not yet available locally, Flowline auto-restores `Delegate.XrmContext` from NuGet before generation proceeds; no user action required.
- R5. The restored XrmContext binary is cached in a stable local path so subsequent runs do not re-download.

**XrmContext generation behavior**

- R6. Flowline passes its existing Dataverse connection to XrmContext as a connection string — no separate XrmContext auth configuration.
- R7. Flowline passes the solution name to XrmContext via `/solutions:` so XrmContext performs its own entity discovery; any `extraTables` from `.flowline` config are passed additionally via `/entities:` (both arguments are additive and use comma-separated values).
- R8. The namespace passed to XrmContext uses the same derivation logic as the PAC path: config value → `Plugins.csproj` fallback → `<SolutionName>.Models`.
- R9. Output is written to `Plugins/Models~` and renamed to `Plugins/Models/` on success — same temp-swap pattern as the PAC path.
- R10. When XrmContext is active, custom API generation is skipped entirely; no PAC fallback for message wrappers.

---

## Key Flows

- F1. **XrmContext generation — first run**
  - **Trigger:** User runs `flowline generate --generator xrmcontext` with no locally cached XrmContext binary.
  - **Steps:** Flowline checks cache — not found → restores `Delegate.XrmContext` NuGet package, extracts exe, writes to cache. Derives namespace. Builds connection string from Flowline's `ServiceClient`. Invokes XrmContext with `/solutions:<SolutionName>`, any `extraTables` via `/entities:`, namespace, connection string, and temp output path. On success, renames `Models~` to `Models/`.
  - **Covers:** R4, R5, R6, R7, R8, R9

- F2. **XrmContext generation — subsequent run**
  - **Trigger:** User runs `flowline generate` with `generate.generator: xrmcontext` in `.flowline`.
  - **Steps:** Flowline reads generator from config. Checks cache — binary present. Derives namespace, builds connection string. Invokes XrmContext with `/solutions:` and optional `/entities:`. Swaps temp folder on success.
  - **Covers:** R1, R2, R6, R7, R8, R9

---

## Scope Boundaries

**Deferred for later**
- Flowline-native generator (XrmContext philosophy, .NET 10) — the intended long-term successor to XrmContext
- Custom API generation support for XrmContext (no equivalent in XrmContext today)
- DLAB EBG V2 — deferred; PAC already covers PAC-style generation

**Out of scope**
- Formal generator abstraction interface — extract when a third generator lands, not before
- XrmContext configuration options beyond namespace, entity filter, and output path

---

## Dependencies / Assumptions

- XrmContext v3.0.1 targets .NET Framework 4.6.2+; runs on Windows as a standalone exe extracted from the `Delegate.XrmContext` NuGet package.
- **Assumption (verify in planning):** Flowline's `ServiceClient` exposes a connection string in a format XrmContext's `/connectionString:` argument accepts.
- XrmContext accepts `/connectionString:`, `/namespace:`, `/out:`, `/solutions:`, and `/entities:` arguments (confirmed from wiki).
- XrmContext uses comma-separated values for both `/solutions:` and `/entities:` filters; both arguments are additive.
