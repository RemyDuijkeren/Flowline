---
date: 2026-05-28
topic: generate-command
---

# `flowline generate` Command

## Summary

A new `flowline generate` command that wraps `pac modelbuilder build` with fixed opinionated flags. It auto-discovers the solution's tables from the live DEV environment, writes early-bound C# types into `Plugins/Models/`, and persists namespace and any extra tables per solution in `.flowline`.

---

## Problem Frame

Developers writing Dataverse plugins need early-bound C# types for compile-time safety. Generating them requires `pac modelbuilder build` with a specific set of flags, an entity filter derived from the solution, and a consistent namespace. The flags are hard to remember, the entity list grows as the solution evolves, and the namespace must match the plugin project. Flowline already knows the solution, the DEV environment, and the Plugins project — making it the right place to automate this step.

---

## Key Flows

- F1. **First-time generate**
  - **Trigger:** `flowline generate` with no namespace in `.flowline`
  - **Steps:** Read `Plugins/Plugins.csproj` for `<RootNamespace>`, append `.Models` to form the default namespace → query DEV for solution entity names and custom API message names (if any) → combine entities with `extraTables` from `.flowline` → run `pac modelbuilder build` into a temp folder (adding `--generatesdkmessages --messagenamesfilter` when custom APIs found) → on success: replace `Plugins/Models/` with temp folder; on failure: discard temp folder → save derived namespace to `.flowline`
  - **Outcome:** `Plugins/Models/` populated with generated types (stale files removed); namespace saved in `.flowline` for future runs
  - **Covered by:** R1, R2, R3, R4, R5, R10, R11, R12, R13, R17

- F2. **Subsequent generate**
  - **Trigger:** `flowline generate` with namespace already in `.flowline`
  - **Steps:** Read namespace + `extraTables` from `.flowline` → query DEV for solution entity names and custom APIs → combine entities with `extraTables` → run `pac modelbuilder build` into a temp folder (with `--generatesdkmessages --messagenamesfilter` when custom APIs found) → on success: replace `Plugins/Models/` with temp folder; on failure: discard temp folder
  - **Outcome:** `Plugins/Models/` refreshed with current entity metadata and custom API types from DEV (stale files removed)
  - **Covered by:** R1, R2, R3, R4, R10, R11, R12, R17

---

## Requirements

**Command behavior**

- R1. Runs `pac modelbuilder build` with fixed opinionated flags: `-sgca --suppressINotifyPattern --emitfieldsclasses`
- R2. Output is always `Plugins/Models/` relative to the solution folder (e.g., `solutions/<SolutionName>/Plugins/Models/`); not configurable
- R3. Entity filter (`-enf`) is constructed from solution entities discovered on DEV, combined with `extraTables` from `.flowline` (if any), joined with `;`
- R4. Namespace (`-n`) is read from `.flowline`; if absent, derived in order from `Plugins/Plugins.csproj`: (1) `<RootNamespace>` + `.Models`; (2) `<PackageId>` + `.Models` (set by `pac plugin init --name`); (3) csproj filename without extension + `.Models`; (4) if csproj is absent entirely, `<SolutionName>.Models`
- R5. When namespace is derived (not already in `.flowline`), it is saved to `.flowline` after `pac modelbuilder build` completes successfully
- R6. With `--verbose`, the exact `pac modelbuilder build` command is printed before execution

**Flags**

- R7. `--namespace <ns>` sets the namespace, saves it to `.flowline`, and uses it for this run
- R8. `--extra-tables <table1,table2>` sets the extra-tables list for this solution, saves it to `.flowline`, and includes those tables in the entity filter for this run; replaces the full `extraTables` list — not additive; to add a table, specify all existing entries plus the new one
- R9. `-s <solution-name>` selects the target solution; required when `.flowline` has more than one solution; auto-selected when there is exactly one (consistent with other commands)

**SDK message generation**

- R10. If the solution contains custom APIs (custom APIs registered as components of the target solution), `--generatesdkmessages` and `--messagenamesfilter` are added automatically with the custom API message names discovered from DEV
- R11. When no custom APIs are registered as solution components, message generation flags are omitted entirely

**Entity and message discovery**

- R12. Solution entities and custom API message names are both discovered by querying the configured DEV environment (same environment used by `push`)
- R13. If no DEV environment is configured, the command fails with a clear error message directing the user to run `flowline provision` or set the dev URL
- R14. Entity logical names from the solution and from `extraTables` are deduplicated before constructing the filter

**Config (`.flowline`)**

- R15. Per-solution config in `.flowline` stores `generate.namespace` (string) and `generate.extraTables` (string array)
- R16. Config is updated in-place; other solution fields (`includeManaged`, etc.) are preserved

**Output safety**

- R17. `pac modelbuilder build` writes into a temporary sibling folder (e.g., `Plugins/Models~`); on success, the temp folder replaces `Plugins/Models/` entirely via a directory rename — removing any files from previous runs that are no longer generated; on failure, the temp folder is discarded and `Plugins/Models/` is left unchanged

---

## Acceptance Examples

- AE1. **Covers R4, R5.** Given no namespace in `.flowline` and `Plugins/Plugins.csproj` has `<RootNamespace>Contoso.Plugins</RootNamespace>`, when `flowline generate` is run, namespace `Contoso.Plugins.Models` is derived, saved to `.flowline`, and passed as `-n Contoso.Plugins.Models` to pac.

- AE2. **Covers R4, R5.** Given no namespace in `.flowline` and `Plugins/Plugins.csproj` has no `<RootNamespace>` but has `<PackageId>Contoso.Plugins</PackageId>` (set via `pac plugin init --name Contoso.Plugins`), when `flowline generate` is run, namespace `Contoso.Plugins.Models` is derived and used.

- AE2b. **Covers R4.** Given no namespace in `.flowline` and `Plugins/Plugins.csproj` does not exist, when `flowline generate` is run on solution `MyApp`, namespace `MyApp.Models` is used.

- AE3. **Covers R3, R12.** Given solution has entities `lead` and `opportunity`, and `extraTables` contains `account` and `lead`, when `flowline generate` runs, the entity filter is `-enf "lead;opportunity;account"` (deduplicated).

- AE4. **Covers R13.** Given no dev URL in `.flowline`, when `flowline generate` is run, the command exits with an error: no DEV environment configured.

- AE5. **Covers R7.** Given namespace `OldNs.Models` in `.flowline`, when `flowline generate --namespace NewNs.Models` is run, `NewNs.Models` is used for this run and saved to `.flowline`.

- AE6. **Covers R17.** Given `pac modelbuilder build` fails mid-run (e.g., auth expired), `Plugins/Models/` retains its previous contents and the temporary output folder is discarded. No partial or empty output is written.

---

## Success Criteria

- Developer can run `flowline generate` after initial setup with no flags and get a working set of early-bound types in `Plugins/Models/`
- Namespace is configured once and never needs to be re-specified
- Adding new tables to the solution and re-running `flowline generate` picks them up without any config changes
- Generated command is visible with `--verbose` so users can reproduce it manually when needed

---

## Scope Boundaries

- No configurable output path — always `Plugins/Models/`; users needing a different location run `pac modelbuilder build` directly
- No `builderSettings.json` (`-wstf`) — Flowline owns the flags; no secondary settings mechanism
- No running `dotnet build` after generate — out of scope for this command
- No change-detection — generate always re-runs regardless of whether solution entities changed since the last run; stale output files are removed via the temp-folder swap (R17), not by skipping generation
- No support for non-DEV environments — always queries DEV
- Requires live DEV connection and an active `pac` auth session — CI pipelines and offline use are not supported; teams that need generated types in CI should commit `Plugins/Models/` to source control and re-run `flowline generate` locally when solution entities change
- Separate `Models.csproj` project not created — generated types live inside the Plugins project; each project that needs types generates independently
- Multi-project solutions (e.g., separate Workflows project alongside Plugins) not supported — `flowline generate` always targets the `Plugins` project; additional projects use `pac modelbuilder build` directly until a `--project` flag is added post-v1

---

## Key Decisions

- **Live DEV query over local source XML:** The generate-push-sync loop means local source is stale when generate runs; live query reflects current environment state
- **Types in `Plugins/Models/` (same assembly):** Separate project requires ILMerge on Dataverse plugin deploy; same-project avoids that entirely
- **Fully opinionated flags:** The value of `flowline generate` is knowing the right flags — exposing a settings escape hatch (`-wstf`) creates a second config mechanism alongside `.flowline`. Flags chosen: `-sgca --suppressINotifyPattern --emitfieldsclasses`; excluded: `--emitentityetc` (env-specific ETCs), `--emitvirtualattributes` (OData pattern), `--generateGlobalOptionSets` (noise), `--serviceContextName` (unreliable LINQ provider)
- **Custom API message types auto-detected:** When the solution contains custom APIs, `--generatesdkmessages --messagenamesfilter` is added automatically — no extra config needed
- **Temp-folder swap for safe output:** `pac modelbuilder build` only adds/updates files — it never deletes. Deleting `Plugins/Models/` before running pac would leave developers with no generated types if pac fails. Generating into a temp folder and renaming on success gives both stale-file cleanup (the replace is total) and failure safety (the rename only happens on success).
- **Independent generation per project:** Azure Functions and other consumers regenerate types themselves; no shared Models project

---

## Dependencies / Assumptions

- Requires an active `pac` auth session against the DEV environment (same as `flowline push`)
- `Plugins/Plugins.csproj` is read for namespace derivation; derivation tries `<RootNamespace>`, then `<PackageId>` (set by `pac plugin init --name`), then csproj filename without extension, then `<SolutionName>.Models` if the file is absent entirely
- `pac modelbuilder build` is available via the detected pac command (same resolution as other pac calls in `PacUtils`)
- In scope for v1.0 (2026-06-15)
