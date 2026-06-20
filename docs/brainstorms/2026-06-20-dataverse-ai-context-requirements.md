---
date: 2026-06-20
topic: dataverse-ai-context
---

# Dataverse AI Context File

## Summary

After `flowline clone` unpacks the solution XML, a `DataverseContextGenerator` class reads that XML to produce `solutions/<SolutionName>/DATAVERSE_CONTEXT.md` — a curated, token-efficient markdown snapshot of the solution's schema. Subsequent `flowline sync` runs keep the file current. After writing the file, the generator self-heals AGENTS.md: it appends the `@` import and markdown link to the end of the file if they are missing. `flowline clone` scaffolds AGENTS.md upfront. All major AI agents (Claude Code, Codex, GitHub Copilot) read AGENTS.md, so one file covers the full agent surface.

---

## Problem Frame

AI agents (Claude Code, Codex) assisting with Dataverse development need to understand the environment's entity structure, option set values, relationships, and automation inventory. Today, developers must explain this in chat, paste field names manually, or let the agent make repeated discovery queries. The unpacked solution XML already contains most of this information after `flowline sync` — but raw XML is too verbose and scattered across dozens of files for efficient AI consumption. A single curated markdown file bridges the gap: one file, auto-loaded, always current after sync.

---

## Key Flows

- F1. **First setup via `flowline clone`**
  - **Trigger:** `flowline clone` completes solution unpack (PAC has written `Package/src/`)
  - **Steps:** Call `DataverseContextGenerator` (reads `Package/src/` XML including `Other/Relationships/{EntityName}.xml`) → write `solutions/<SolutionName>/DATAVERSE_CONTEXT.md` → run self-healing check on AGENTS.md (which `ScaffoldAgentsFileAsync` already scaffolded, or creates it if absent) → append `@` import and markdown link to the end of AGENTS.md if missing
  - **Outcome:** After a single `flowline clone`, the developer has the full AI context chain — context file created, AGENTS.md references appended at end
  - **Covered by:** R1, R2, R3, R4, R9, R10, R11

- F2. **`flowline sync` regenerates context file**
  - **Trigger:** `flowline sync` completes solution unpack
  - **Steps:** `DataverseContextGenerator` reads `Package/src/` XML → writes `solutions/<SolutionName>/DATAVERSE_CONTEXT.md` (emits warning and overwrites if uncommitted changes exist) → runs self-healing check on AGENTS.md
  - **Outcome:** Context file updated to reflect latest schema; developer warned if there were uncommitted changes
  - **Covered by:** R1, R2, R3, R4, R5, R6, R9, R10, R11

- F3. **Self-healing reference check (existing project without references)**
  - **Trigger:** `flowline sync` generates DATAVERSE_CONTEXT.md in a project where AGENTS.md exists but lacks the `@` import or markdown link
  - **Steps:** After writing the file, check AGENTS.md for the `@` import line → append to end of file if missing; check AGENTS.md for a markdown link in a "Dataverse schema context" section → append to end of file if missing
  - **Outcome:** Reference chain is always consistent after sync regardless of how the project was set up
  - **Covered by:** R9, R10, R11

---

## Requirements

**Architecture**

- R1. Context file generation runs as a post-unpack step in both `flowline clone` (after PAC solution clone writes `Package/src/`) and `flowline sync` (after PAC solution sync writes `Package/src/`)
- R2. Generation is implemented in a dedicated `DataverseContextGenerator` class (or equivalent), not inline in `CloneCommand` or `SyncCommand` — one implementation, two callers, independently testable. Content sections (R12–R35) are designed to be added incrementally; each new component type needs only a new XML reader and markdown renderer, not changes to the core generation loop
- R3. `DataverseContextGenerator` receives: the `Package/src/` path, the solution name, and the repo root path (for AGENTS.md reference repair) — no live Dataverse connection required; no other command state required

**Output**

- R4. Output path: `solutions/<SolutionName>/DATAVERSE_CONTEXT.md` (relative to project root)
- R5. If `DATAVERSE_CONTEXT.md` has uncommitted changes when sync runs, emit a warning and overwrite — do not block sync (same pattern as `Plugins/Models/` dirty-tree handling)
- R6. Always generated on every successful sync — no opt-in config required for v1

**AGENTS.md integration**

- R7. The existing `ScaffoldAgentsFileAsync` template in `CloneCommand` is updated to append a "Dataverse schema context" section with a markdown link and an `@solutions/<SolutionName>/DATAVERSE_CONTEXT.md` import line at the end of the generated AGENTS.md — the `@` import is valid even before the context file exists
- R8. For multi-solution repos, one `@` import line and one markdown link per solution appear in AGENTS.md

**Self-healing reference check**

- R9. After writing DATAVERSE_CONTEXT.md, `DataverseContextGenerator` checks AGENTS.md for an `@solutions/<SolutionName>/DATAVERSE_CONTEXT.md` import line; if absent, it appends the line to the end of the file (if AGENTS.md does not exist, call `ScaffoldAgentsFileAsync` to create the full scaffold first, then append the import to the end)
- R10. After writing DATAVERSE_CONTEXT.md, `DataverseContextGenerator` checks AGENTS.md for a markdown link to the context file in a "Dataverse schema context" section; if absent, it appends the section and link
- R11. The self-healing check runs on every clone and sync — idempotent; a reference that already exists is never duplicated

**Content: Solution header**

- R12. First section: solution display name, unique name, version, publisher prefix — read from `Other/Solution.xml`

**Content: Entities**

- R13. One section per entity found in `Entities/*/Entity.xml`
- R14. Entity header: logical name, display name, EntitySetName (OData collection path), ownership type, IsActivity flag
- R15. Attributes table per entity: logical name, display name, type, required level (none/recommended/required), description (if present), custom flag (`IsCustomField`)
- R16. Inline option sets: for picklist and boolean attributes, include the value→label mapping in the attributes section (values are embedded in Entity.xml)
- R17. `ValidForCreate`/`ValidForRead`/`ValidForUpdate` flags noted for attributes where not all three are true (e.g., calculated/rollup fields)

**Content: Relationships**

- R18. Relationship metadata read from `Other/Relationships/{EntityName}.xml` (per-entity files produced by PAC unpack); if no relationship file exists for an entity, the relationships section is omitted for that entity
- R19. 1:N and N:1 relationships: schema name, related entity logical name, lookup field logical name
- R20. N:N relationships: schema name, related entity logical name, intersect entity name

**Content: Forms**

- R21. For each entity, list forms found in `FormXml/main/`, `FormXml/quick/`, and `FormXml/card/`
- R22. Form output is a condensed field list organized by tab → section → field names — not raw FormXML
- R23. Hidden controls (e.g., address GUIDs) are excluded from the form field list

**Content: Views**

- R24. For each entity, list saved queries found in `SavedQueries/*.xml`
- R25. View output: view name, column list, primary filter summary (condition attributes + operators), linked entity names
- R26. FetchXML is not reproduced verbatim — extract semantic content only

**Content: Workflows**

- R27. List Power Automate / workflow display names from the `Workflows/` folder
- R28. Include: display name, activation state (active/inactive), trigger entity if discernible from XML

**Content: Plugin steps**

- R29. List plugin step registrations from the `SdkMessageProcessingSteps/` folder
- R30. Include: message name, primary entity, execution stage (pre-validation/pre-operation/post-operation), mode (sync/async), plugin type class name (XML value is assembly-qualified — extract the class name before the first comma)

**Content: Connection references**

- R31. List connection references from `Other/Customizations.xml`
- R32. Include: display name, connector ID

**Content hygiene**

- R33. Sections with no content (e.g., no workflows in the solution) are omitted entirely — no empty section headers
- R34. Display names are read from `languagecode="1033"` entries; fall back to logical name if no English label is present
- R35. GUIDs never appear in the markdown output — all identifiers use logical names or display names

---

## Acceptance Examples

- AE1. **Covers R1, R4.** After `flowline clone` on solution `MyApp`, file `solutions/MyApp/DATAVERSE_CONTEXT.md` is created without requiring a subsequent `flowline sync`.

- AE1b. **Covers R1, R4.** After `flowline sync` on solution `MyApp`, `solutions/MyApp/DATAVERSE_CONTEXT.md` is regenerated to reflect the current state.

- AE2. **Covers R5.** `DATAVERSE_CONTEXT.md` has uncommitted changes before sync. Sync emits a warning and overwrites the file. Sync does not fail or block.

- AE3. **Covers R14, R15.** Contact entity has custom attribute `av_linkedin` (type: nvarchar, required: recommended, description: "LinkedIn Profile URL"). The attributes table includes this row with the custom flag set.

- AE4. **Covers R16.** Boolean attribute `donotbulkemail` has options `0=Allow`, `1=Do Not Allow`. These values appear inline in the attribute row or immediately beneath it.

- AE5. **Covers R33.** Solution has no workflows. The Workflows section is absent from the output file entirely.

- AE6. **Covers R35.** Form XML references `{6bf2efbd-4215-ed11-b83d-000d3aab2cf1}` — this GUID does not appear anywhere in the markdown output.

- AE7. **Covers R7, R8.** `flowline clone` on a repo with two solutions generates AGENTS.md with a "Dataverse schema context" section and `@` imports at the end:
  ```markdown
  # Flowline — Agent Instructions
  ...

  ## Dataverse schema context
  - [MySolution](solutions/MySolution/DATAVERSE_CONTEXT.md)
  - [AnotherSolution](solutions/AnotherSolution/DATAVERSE_CONTEXT.md)

  @solutions/MySolution/DATAVERSE_CONTEXT.md
  @solutions/AnotherSolution/DATAVERSE_CONTEXT.md
  ```

- AE8. **Covers R9, R10, R11.** An existing project was cloned before this feature — AGENTS.md exists but has no context file references. After `flowline sync` generates DATAVERSE_CONTEXT.md, the "Dataverse schema context" section and `@` import are appended to the end of AGENTS.md. Running sync again does not add duplicate lines.

- AE9. **Covers R9.** A project has no AGENTS.md at all. After sync generates DATAVERSE_CONTEXT.md, `DataverseContextGenerator` calls `ScaffoldAgentsFileAsync` to create the full AGENTS.md scaffold (daily dev loop, command reference, project structure, exit codes), then appends the `@` import line to the end.

- AE10. **Covers R2, R3.** `DataverseContextGenerator` can be instantiated and called in a unit test by providing a local `Package/src/` path and a solution name — no `IOrganizationServiceAsync2` mock, no `SyncCommand` or `CloneCommand` dependencies required.

- AE11. **Covers R8, R9, R10, R11.** A repo already has solution B's entries at the end of AGENTS.md (`@solutions/B/DATAVERSE_CONTEXT.md` import and a "Dataverse schema context" link). Running `flowline sync` for solution A: A's link is added to the existing "Dataverse schema context" section; A's `@` import is appended after B's import; B's entries are undisturbed. Running sync for A again does not duplicate A's entries.

---

## Success Criteria

- Claude Code loads `DATAVERSE_CONTEXT.md` automatically on every session — developers no longer need to paste entity structures into chat
- After `flowline sync`, the context file reflects the current solution schema from DEV
- `DataverseContextGenerator` has unit tests that run against fixture XML — no live Dataverse connection required
- Multi-solution repos generate one context file per solution, all referenced from AGENTS.md
- Existing projects self-heal on first sync after upgrading — no manual file editing required

---

## Scope Boundaries

- Security model (roles + privileges) — deferred post-v1; `Roles/` folder exists in PAC output but role content is complex and rarely needed for coding tasks
- `extraTables` entities (entities outside the solution scope) — deferred; would require extra API calls
- Standalone invocation (`flowline generate ai-context` or similar) — deferred; sync-only in v1
- Opt-out config flag (`aiContext: false`) — deferred; always generated in v1
- Canvas app source — excluded; YAML/binary format, not schema-relevant
- Ribbon customizations — excluded; too granular for AI context
- FetchXML verbatim in views — excluded; semantic summary only (R26)
- Non-English display names — v1 uses English (LCID 1033) as the priority display name; falls back to logical name if no English label exists (R34). Multi-language label support is deferred.
- Large solution output — DATAVERSE_CONTEXT.md size is proportional to solution complexity; no token cap is enforced in v1. For solutions with >25 entities the file may be large enough to warrant selective loading. Possible future direction: prioritize attributes that appear in forms or views over attributes not referenced in any form (using FormXml data already parsed in the same pass). Per-entity or per-section chunking is deferred post-v1.

---

## Key Decisions

- **XML-only, no API calls:** All content including relationship metadata comes from PAC-unpacked local files. Relationships are stored in `Other/Relationships/{EntityName}.xml` (separate per-entity files, confirmed against real PAC output — they are NOT inside `Entity.xml`). Generation is fully offline-safe and requires no live Dataverse connection.
- **Always generated, no opt-in:** Convention over configuration. Every successful `flowline sync` regenerates the file. Opt-out config can be added post-v1 if requested.
- **Two callers, one class:** `DataverseContextGenerator` is called from both `CloneCommand` (after PAC solution clone unpacks XML) and `SyncCommand` (after PAC solution sync). No live Dataverse connection required — all content comes from local XML. A shared class keeps the logic consistent and independently testable.
- **Warn on dirty, don't block:** The context file is generated output, not source. Blocking sync on an uncommitted context file is too strict — same treatment as `Plugins/Models/`.
- **AGENTS.md only — one file, all agents:** Claude Code, Codex, and GitHub Copilot all read AGENTS.md. No CLAUDE.md needed. The `@` import syntax at the top of AGENTS.md auto-loads the schema into Claude Code; other agents that don't support `@` see it as harmless text and still get the full workflow contract.
- **Self-healing over manual setup:** `DataverseContextGenerator` owns the reference chain. After writing the context file it checks and repairs AGENTS.md (both the `@` import and the markdown link). Existing projects heal on first sync — no migration script, no manual editing.
- **Condensed output over verbatim:** Forms and views are summarized (field list, filter summary) rather than reproduced as raw XML. Raw XML is available in `Package/src/` for tools that need it — the context file optimises for AI token efficiency.
- **Incremental component coverage:** The content section list (R12–R35) is expected to grow as more PAC XML schemas are verified against real unpack output. New component types (custom APIs, environment variables, app modules, etc.) are added by implementing a new XML reader and markdown renderer — no changes to the core generation loop. Only components with verified XML examples are included in a given version.

---

## Dependencies / Assumptions

- PAC CLI unpacks solution XML into `Entities/`, `Workflows/`, `SdkMessageProcessingSteps/`, `Other/` — generation assumes this folder structure (confirmed against real PAC output)
- Relationships are unpacked to `Other/Relationships/{EntityName}.xml` (one file per entity that has relationships, indexed by `Other/Relationships.xml`) — confirmed against real PAC output; `Entity.xml` does NOT contain relationship data
- `Other/Solution.xml` contains solution version and publisher info (standard PAC unpack output)
- `AGENTS.md` is already generated by `ScaffoldAgentsFileAsync` in `CloneCommand`; this feature updates the template (R7) and adds the `DataverseContextGenerator` call to clone (R1)
- For projects cloned before this feature, the self-healing check (R9, R10) adds the missing references on first sync
- Additional component types beyond R12–R35 (custom APIs, environment variables, app modules, etc.) are expected in future iterations; each requires a verified real PAC unpack XML example before being added to the spec
