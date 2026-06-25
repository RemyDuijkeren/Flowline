---
date: 2026-06-24
topic: sync-clean-standard-entity-metadata
status: rejected
rejected-reason: ISV-specific problem, not broadly needed. Only hits teams adding fields to standard entities. Most Flowline users never see it. The fix is ~28 lines of post-unpack script any team can own themselves. Flowline adds features broadly needed — not workarounds for a narrow audience.
origin: Spotler Connector ISV project — observed after each export/unpack cycle
---

# Sync — Strip Standard Entity Metadata After Unpack

## Decision

**Rejected.** See reasoning below.

---

## Target Group

This feature is only relevant to **ISVs** — teams building solutions intended for distribution to multiple customer environments. Specifically, ISVs who:

- Add custom fields to standard Dataverse entities (`Contact`, `Lead`, `Account`, etc.)
- Distribute their solution as unmanaged (or managed with an unmanaged development layer)
- Use Git as the source of truth and care about clean, reviewable diffs

This does **not** affect:

- **Product teams** building internal solutions — their solution only needs to work in their own environments. Metadata noise in `Entity.xml` is an annoyance but never causes a customer impact problem.
- **Consultants** building customer-specific customizations — their solution typically only contains custom entities (prefixed), so standard entity `Entity.xml` files rarely appear at all.
- **Teams building fully custom solutions** — if no fields are added to standard entities, the problem doesn't exist.

The ISV segment is real but narrow. Flowline's core audience is the broader Dataverse pro-developer community — and the majority of that community never encounters this problem.

---

## Why the Feature Was Considered

The problem is genuine. When an ISV adds a custom field to `Contact` and exports the solution, SolutionPackager writes a full `Contact/Entity.xml` containing:

- Every localized name and description from the developer's environment
- Every entity configuration flag (`IsConnectionsEnabled`, `IsMailMergeEnabled`, etc.) set in that environment
- Wizard strings

None of this was authored by the ISV. But it gets committed to Git after every `flowline sync`, producing a 50–70 line diff per standard entity that obscures the actual change (the new custom field). And when deployed to a customer environment, it silently overwrites whatever settings the customer had configured on `Contact` — which is the more serious problem.

The pain is proportional to how many standard entities the ISV extends. The Spotler Connector extends 6 (`Contact`, `Lead`, `Email`, `ActivityPointer`, `PhoneCall`, `List`) — enough that it motivated the request.

---

## Why It Was Rejected

**Not broadly needed.** Flowline's design principle is to add features that the majority of users benefit from. This problem is structurally limited to the ISV segment. A consultant or product-team developer using Flowline will never encounter it.

**The workaround is simple and self-contained.** The fix is approximately 28 lines of C# that runs post-unpack. Any team needing it can implement it in their own build script (Nuke, Cake, a PowerShell step in CI). It doesn't require Dataverse API access, special tooling, or Flowline internals. The cost of owning this outside Flowline is low.

**Flowline is not a build script replacement.** Teams with complex build requirements (ISV packaging, managed/unmanaged split, AppSource submission) will have build tooling regardless. Flowline handles the dev loop and deployment lifecycle; it doesn't need to absorb every post-processing step a specific project type requires.

**Maintenance cost vs. audience size.** Any feature added to Flowline must be maintained, documented, and tested. The entity metadata strip logic is simple today, but SolutionPackager's XML schema evolves. That maintenance cost is only justified when the audience is broad enough.

## Summary

After `flowline sync` unpacks a solution, `Entity.xml` files for **standard entities** (e.g. `Contact`, `Lead`, `PhoneCall`) contain full environment-specific metadata — localized names, configuration flags, wizard strings, scaffold elements. ISVs add *custom fields* to standard entities; they do not own or intend to ship entity-level configuration. This bloats git diffs and silently overwrites customer environment settings on import.

Add an opt-in post-unpack step that strips all metadata from standard entity `Entity.xml` files, keeping only the ISV's own custom attributes. Persisted as a one-time opt-in in `.flowline`. Also runnable standalone as `flowline clean-entities` for CI pipelines.

---

## Problem Frame

When a Dataverse solution is exported and unpacked (via SolutionPackager), `Entity.xml` files for standard entities include:

- Localized names and descriptions (`LocalizedNames`, `LocalizedCollectionNames`, `Descriptions`)
- Entity configuration flags (`IsConnectionsEnabled`, `IsDuplicateCheckSupported`, `IsMailMergeEnabled`, ...)

For ISVs this causes three concrete problems:

1. **Customer impact** — importing the solution overwrites standard entity settings in the customer's org with whatever was set in the developer's environment.
2. **Git noise** — every re-export produces a large diff of metadata unrelated to the solution's actual changes. In the Spotler Connector project, 6 standard entities (`Contact`, `Lead`, `Email`, `ActivityPointer`, `PhoneCall`, `List`) each gained 50–70 lines of environment-specific metadata per export cycle.
3. **Intent mismatch** — the solution carries fields the ISV added; it was never meant to carry entity-level configuration.

**What to keep:** For each standard entity `Entity.xml`, retain the full structure at root level (`<Name>`, `<EntityInfo>`, `<FormXml>`, `<SavedQueries>`, `<RibbonDiffXml>`) and the entire `<attributes>` element inside `EntityInfo/entity` with all child `<attribute>` elements unchanged. If an attribute appears in the exported XML, it was explicitly part of the solution. The problem is the entity-level metadata inside `EntityInfo/entity` (names, config flags), not the structure or the attributes themselves.

**What is a standard entity:** Any entity whose logical name does not start with the publisher prefix (e.g. `dh_`). Custom entities always carry the prefix; standard entities (Contact, Account, Lead, etc.) never do. The prefix is already known from `.flowline`.

---

## Command Shape

Flowline uses a flat command model — no sub-namespaces. Two surfaces:

**A — Opt-in flag on `sync` (primary)**

```shell
flowline sync --clean-standard-entities
```

On first use, saves `cleanStandardEntities: true` to `.flowline` so subsequent `flowline sync` runs apply it automatically without the flag. This mirrors how `flowline generate` persists its config.

**B — Standalone `flowline clean-entities` (composable)**

```shell
flowline clean-entities
flowline clean-entities --dry-run
```

Targets `Package/src/Entities/` relative to the `.flowline` project root. Can be piped into CI steps independently of sync. Uses the publisher prefix from `.flowline`. No arguments required for normal project use.

Both surfaces produce the same output and use the same logic. `flowline sync --clean-standard-entities` runs sync first, then calls the same cleaner as `flowline clean-entities`.

---

## Requirements

**Config**

- R1. The first time `flowline sync --clean-standard-entities` runs successfully, write `cleanStandardEntities: true` to `.flowline`. Subsequent `flowline sync` runs apply cleaning automatically without the flag.
- R2. If `.flowline` has `cleanStandardEntities: true`, `flowline sync` applies cleaning after every unpack — no flag needed.
- R3. `flowline clean-entities` standalone command does not require the flag or `.flowline` config entry — it always cleans when called directly.

**Detection — which entities are standard**

- R4. An entity is standard if its `<Name>` element value does not start with the publisher prefix read from `.flowline` (e.g. `dh_`).
- R5. Custom entities (name starts with publisher prefix) are not modified.
- R6. If the publisher prefix cannot be read from `.flowline`, fail with a clear error rather than guessing.

**What to strip**

- R7. For each standard entity `Entity.xml`, apply two targeted strip passes:
  - **Inside `EntityInfo/entity`:** remove everything except `<attributes>`. This strips localized names, entity config flags, wizard strings, and any other entity-level metadata. The `<attributes>` element and all its `<attribute>` children are kept exactly as-is.
  - **At root level:** remove anything that is not `<Name>`, `<EntityInfo>`, `<FormXml>`, `<SavedQueries>`, or `<RibbonDiffXml>`. `<FormXml>`, `<SavedQueries>`, and `<RibbonDiffXml>` are always kept — with or without content inside them.
- R8. `<EntityInfo>` is kept even if `<attributes>` is empty after stripping. The `Entity.xml` file is never deleted by the cleaner.
- R9. Custom entity `Entity.xml` files are untouched — full content preserved.

**Output**

- R10. Emit one log line per cleaned entity: entity name and count of attributes retained (e.g. `Contact — 3 attributes retained`).

**Dry-run**

- R12. `--dry-run` flag (on both `flowline sync --clean-standard-entities --dry-run` and `flowline clean-entities --dry-run`) prints what would be cleaned or removed per entity without writing any files.

**Idempotency**

- R13. Running the cleaner twice on the same `Package/src/` produces no diff. The cleaned XML is stable across runs.

**Packability**

- R14. The cleaned `Entity.xml` files must produce a valid solution zip when re-packed with `pac solution pack`. Minimal structure must satisfy SolutionPackager's schema validation.

---

## Acceptance Examples

- AE1. **Covers R4, R7, R10.** `Contact` entity has 3 `dh_` attributes in the exported XML. After cleaning: `Entity.xml` retains all 3 attributes; `<FormXml />`, `<SavedQueries />`, `<RibbonDiffXml />` at root are preserved; localized names, entity config flags, and wizard strings inside `EntityInfo/entity` are removed. Log emits: `Contact — 3 attributes retained`.

- AE1b. **Covers R7.** `List` entity has a `<FormXml>` element with actual form content inside (not empty). After cleaning: the `<FormXml>` element and its content are preserved unchanged.

- AE2. **Covers R8.** `PhoneCall` entity is in the solution because a plugin step fires on it but no `Entity.xml` exists for it — nothing for the cleaner to do. No log line emitted.

- AE2b. **Covers R8.** `Contact` entity has an `Entity.xml` whose `<attributes>` is empty after stripping. `Entity.xml` is kept with the empty `<attributes>` element. `<EntityInfo>` is not removed.

- AE3. **Covers R5, R9.** `dh_campaign` is a custom entity (starts with `dh_`). Its `Entity.xml` is untouched after cleaning.

- AE4. **Covers R12.** `flowline clean-entities --dry-run` prints the list of entities that would be cleaned and what would be removed, without writing any files.

- AE5. **Covers R13.** Running `flowline clean-entities` twice on a freshly synced solution produces no file changes on the second run (`git status` is clean after the second run).

- AE6. **Covers R14.** After cleaning, `pac solution pack --folder Package/src --zipfile /tmp/test.zip` completes without errors.

- AE7. **Covers R14.** The packed zip from AE6 imports into a Dataverse environment without errors. Custom attributes appear on the entity; standard entity settings in the target org are not overwritten.

- AE8. **Covers R1, R2.** First run: `flowline sync --clean-standard-entities` completes, writes `cleanStandardEntities: true` to `.flowline`. Second run: plain `flowline sync` (no flag) also applies cleaning.

- AE9. **Covers R6.** `.flowline` has no publisher prefix configured. `flowline clean-entities` fails with: `Publisher prefix not configured — cannot identify standard entities. Run 'flowline clone' to set up the project.`

---

## Scope Boundaries

- OOB attribute properties (required level, max length, etc.) that the ISV customized are retained — they appear in the `attributes` element and are intentional. The stripped content is entity-level metadata inside `EntityInfo/entity` only.
- `.flowline` opt-out flag (e.g. `cleanStandardEntities: false`) — deferred; use the absence of the config key as the opt-out.
- Per-entity exclusion list — deferred. If an ISV needs to preserve full metadata for a specific standard entity, they can exclude it post-v1.
- Running as part of `flowline clone` — not needed; clone is a one-time setup and the initial unpack is immediately followed by a sync anyway.

---

## Key Decisions

- **Two-pass strip, whitelist approach per level.** Inside `EntityInfo/entity`: keep only `<attributes>`, remove everything else. At root: keep `<Name>`, `<EntityInfo>`, `<FormXml>`, `<SavedQueries>`, `<RibbonDiffXml>`, remove anything else. Whitelist beats blacklist — new metadata elements added by future PAC versions are stripped automatically without touching the spec.
- **`<FormXml>`, `<SavedQueries>`, `<RibbonDiffXml>` always kept.** These are structural elements, not metadata. They may contain real content (forms, views, ribbon diffs) on some standard entities. Never removed, regardless of content.
- **All attributes kept, no IsCustomField filtering.** Every attribute in the exported XML was explicitly included in the solution — either a custom field or an OOB field the ISV intentionally customized. Filtering by `IsCustomField` would silently drop those customizations.
- **Entity.xml never deleted (R8).** Even if `<attributes>` is empty, keep the file with the empty element. Standard entity folders also appear without any `Entity.xml` (confirmed in the Spotler Connector solution) — the cleaner never touches the folder structure.
- **Opt-in via `.flowline`, auto-persist (R1, R2).** Not everyone needs this — ISV projects do, product-team unmanaged projects might not. Opt-in on first use, then automatic. Mirrors `flowline generate` config persistence.
- **Standalone command `flowline clean-entities` (R3).** CI pipelines sometimes need to run cleanup independently of a sync cycle (e.g. after a manual PAC unpack, or as a pre-commit hook). A standalone command covers this without forcing the caller to run a full sync.
- **Flat command name.** `flowline clean-entities` over `flowline solution clean-entity-metadata` — Flowline has no sub-namespaces. Short, unambiguous, consistent with existing verb-noun pattern.
- **Publisher prefix from `.flowline`, not a flag.** The prefix is already stored after `clone`. Requiring `--custom-prefix` on every invocation is unnecessary friction and error-prone. Fail fast if the prefix is missing (R6) rather than guessing.

---

## Dependencies / Assumptions

- Publisher prefix is stored in `.flowline` after `flowline clone` — confirmed by existing implementation.
- All `attribute` elements in the exported `Entity.xml` are treated as intentional — no filtering within the attributes element.
- The minimum `Entity.xml` structure accepted by `pac solution pack` (with only custom attributes present) needs a one-time integration test to confirm. If SolutionPackager requires additional elements even when empty, the strip step must emit them as empty stubs.
- Standard entity folders without `Entity.xml` (only `RibbonDiff.xml`, `SavedQueries/`, `FormXml/`) are the normal case — confirmed in the Spotler Connector solution. These folders are not touched by the cleaner.
