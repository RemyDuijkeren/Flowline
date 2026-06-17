---
date: 2026-06-17
topic: generate-lcg-udg-support
---

# flowline generate — LCG-UDG Generator Support

## Summary

Add `lcg` as a third value to `--generator` in `flowline generate`. When selected, Flowline auto-restores `LCGCmd.exe` from GitHub releases, generates an LCG settings XML file from the solution's entity list, connects to Dataverse, and invokes LCGCmd to produce C# string-constant classes — an alternative to early-bound typed classes for late-bound-first projects.

---

## Problem Frame

PAC and XrmContext generate early-bound typed entity classes. Some projects use late-bound Dataverse code instead — accessing attributes via `entity["name"]` — and benefit from typed string constants (entity/attribute name constants, option set enums) to eliminate magic strings without committing to full early-bound class generation. LCG-UDG (Jonas Rapp) produces exactly this output style. Without Flowline support, developers must run LCGCmd separately or use XrmToolBox as a prerequisite.

---

## Key Decisions

**LCG as a generator alternative, not a layer.** A project chooses one generator — `pac`, `xrmcontext`, or `lcg` — per solution. The choice is stored in `.flowline` and applies on every `flowline generate` run. Mixing generators within a solution is out of scope.

**Flowline generates the LCG settings XML.** `LCGCmd.exe` requires a pre-saved XML settings file. Flowline generates this file from solution context (solution entity list + extraTables from config) before each invocation — no XrmToolBox prerequisite. The generated XML includes all entities and all attributes by default; attribute-level filtering is deferred.

**Auto-restored from GitHub releases.** LCG-UDG does not publish to NuGet. Flowline downloads `LCGCmd.exe` from the GitHub releases API (`https://api.github.com/repos/rappen/LCG-UDG/releases/latest`) and caches it in the user profile. Same pattern as XrmContext binary caching.

**Connection string from PAC profile.** Same approach as XrmContext — Flowline builds a connection string from the active PAC profile for the target environment. No separate LCG auth configuration.

**Windows-only; guard before any work.** `LCGCmd.exe` is a .NET Framework exe. Flowline checks `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)` before restore or invocation and throws a clear error on non-Windows.

---

## Requirements

**Generator selection**

- R1. `flowline generate` accepts `--generator lcg`; the `lcg` value is added to the existing `GeneratorType` enum alongside `pac` and `xrmcontext`.
- R2. Generator choice persists to `.flowline` under `generate.generator` per solution on every run — including when defaulting to `pac`. *(same as R2 in XrmContext requirements)*

**LCG availability**

- R3. When `lcg` is selected and `LCGCmd.exe` is not cached locally, Flowline downloads it from the GitHub releases API before generation; no user action required.
- R4. `LCGCmd.exe` is cached in the user profile at `LocalApplicationData/Flowline/tools/lcg/{version}/LCGCmd.exe` — shared across all Flowline projects on the machine.
- R5. Flowline checks `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)` before any restore or invocation and fails with a clear message if not Windows.

**LCG settings XML generation**

- R6. Flowline generates a temporary LCG settings XML file from the solution's entity list (solution components from Dataverse + any `extraTables` from `.flowline` config) before each invocation. The XML is not persisted — regenerated on every run.
- R7. The generated settings XML includes all entities in scope and all of their attributes. Attribute-level filtering is not supported in v1.

**LCG generation behavior**

- R8. Flowline derives a connection string from the active PAC profile for the target environment and passes it to LCGCmd — same approach as XrmContext.
- R9. Namespace derivation follows the same chain as other generators: config value → `Plugins.csproj` fallback → `<SolutionName>.Models`.
- R10. Output writes to `Plugins/Models~` and is renamed to `Plugins/Models/` on success — same temp-swap pattern as other generators.
- R11. Custom API generation is skipped entirely when `lcg` is active.

---

## Scope Boundaries

**Deferred for later**
- Attribute-level filtering in the generated settings XML (include/exclude specific attributes)
- Persistent LCG settings XML — letting developers commit and hand-edit the generated file
- Support for importing an existing XrmToolBox-exported settings file as a starting point

**Out of scope**
- XrmToolBox settings file import/export UI parity
- Generating LCG output alongside early-bound classes in the same run
- Linux/macOS support (blocked by LCGCmd's .NET Framework dependency)

---

## Key Flows

- **F1. LCG generation — first run**
  Trigger: `flowline generate --generator lcg` with no cached binary.
  Steps: OS guard → download `LCGCmd.exe` from GitHub releases → cache in user profile → query solution entities → generate settings XML → build connection string from PAC profile → invoke LCGCmd with settings file + connection string + output path → rename `Models~` to `Models/` on success.

- **F2. LCG generation — subsequent run**
  Trigger: `flowline generate` with `generate.generator: lcg` in `.flowline`.
  Steps: OS guard → binary found in cache → query solution entities → generate settings XML → invoke LCGCmd → temp-swap on success.

---

## Dependencies / Assumptions

- **LCGCmd.exe CLI arguments**: The exact CLI argument names for the settings file path, connection string, and output path need verification by inspecting the LCG-UDG repo / release artifacts during planning. The settings file path is known (`--settingsFilePath=...`); others TBD.
- **LCG settings XML format**: The XML schema used by LCGCmd must be reverse-engineered from the XrmToolBox plugin source or existing settings files. This is a planning-time task — the format is not yet verified.
- **GitHub releases API**: Latest release assumed to contain `LCGCmd.exe` as a downloadable asset. Asset naming convention needs verification during planning.
- **LCGCmd output location**: LCGCmd likely writes to a directory or file path specified as a CLI argument. Verify during planning — the output path argument name is TBD.
- **Assumption**: All solution entities and attributes can be fetched via the same Dataverse query already used by `flowline generate` for entity filtering.
