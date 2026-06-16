---
title: "feat: Add sub-change detail to SolutionChangeSummary"
date: 2026-06-16
origin: docs/brainstorms/2026-06-16-entity-change-detail-requirements.md
---

# feat: Add sub-change detail to SolutionChangeSummary

## Summary

`SolutionChangeSummary` currently shows *that* a component changed — not *what* changed inside it. This plan adds XML-diff-based sub-change detail for entities (attributes), views (grid columns, filter, sort), option sets (option labels), and forms (fields, sections, tabs). Sub-changes are shown in the terminal tree up to a hardcoded threshold, and always written in full to `CHANGES.md` in the solution folder.

---

## Problem Frame

After `flowline sync`, the change tree shows `~ entity metadata` or `~ My View Name (view)` with no sub-detail. Developers have to open the XML to understand what actually changed. The goal is to surface attribute-level, column-level, and option-level changes directly in the sync output — with enough detail to write a meaningful commit message without opening a file.

---

## Requirements

From `docs/brainstorms/2026-06-16-entity-change-detail-requirements.md`:

- Sub-changes for Entity.xml (attributes: added with type, removed, modified by name), SavedQueries (grid columns ±, filter flag, sort flag), FormXml (fields ±, sections/tabs ±), and OptionSets (option labels ±)
- New files: skip sub-detail; component-level icon is sufficient
- Deleted files: show all items as removed (read from git HEAD)
- Modified files: diff old (git HEAD) vs. new (working tree)
- Terminal threshold: hardcoded internal constant (start at 5); 0 = count-inline mode (`~ entity metadata (3 added, 1 removed)`); overflow = `...and N more (see CHANGES.md)`
- Sub-items ordered: added → removed → modified
- CHANGES.md: always written to `solutions/<SolutionName>/CHANGES.md`, overwrites, full detail

---

## Key Technical Decisions

**KTD1: `SubChange` attached to `ChangeItem` as optional property**
Add `IReadOnlyList<SubChange>? SubChanges = null` to the existing `ChangeItem` record. Additive and backwards-compatible — all existing code that constructs `ChangeItem` without sub-changes continues to work. The alternative (a new wrapper type) adds complexity with no gain at this scale.

**KTD2: CHANGES.md as a separate method, not inside `ComputeAsync`**
Add `WriteChangesFileAsync(string slnFolder, string? envDisplayName, CancellationToken ct)` to `SolutionChangeSummary`, called from `SyncCommand` after `WriteTree`. Keeps computation and I/O independently testable, consistent with the existing `WriteTree`/`WriteFlat` pattern. `slnFolder` is already available in `SyncCommand` (the caller derives `srcPath` from it). Solution name is derived from `Path.GetFileName(slnFolder)`.

**KTD3: Old XML via `git show HEAD:<path>` per component**
Reuse the existing `git show` pattern from `ResolveXmlNameAsync`. For modified files, fetch old XML from git HEAD and read new XML from disk. For deleted files, fetch old XML from git HEAD only. For new files, skip entirely. No batch reads — component-level git calls are fast and the pattern is already established.

**KTD4: Display threshold as hardcoded internal constant**
`private const int SubChangeDisplayThreshold = 5` in `SolutionChangeSummary`. Not configurable in `.flowline` (confirmed in brainstorm). Zero is a valid value (count-inline mode) and is enforced in the rendering path.

**KTD5: Sub-change dispatch keyed on `ParsedPath`**
In `ComputeAsync`, after name resolution, determine which diff to run based on `parsed.ComponentKey` suffix:
- ends with `/entity` → entity attribute diff
- contains `/view/` → view diff
- contains `/form/` → form diff
- `parsed.Group == "OptionSets"` → option set diff

This avoids adding new fields to `ParsedPath` and reuses what's already computed.

---

## Implementation Units

### U1. Add `SubChange` record and extend `ChangeItem`

**Goal:** Introduce the data model that carries sub-change detail through the pipeline.

**Requirements:** Enables all sub-change rendering in U6 and file output in U7.

**Dependencies:** None.

**Files:**
- `src/Flowline/Utils/SolutionChangeSummary.cs`

**Approach:**
- Add `public record SubChange(string Description, ChangeStatus Status)` alongside existing records at the top of `SolutionChangeSummary`
- Add `IReadOnlyList<SubChange>? SubChanges = null` as an optional positional or init-only property on `ChangeItem`
- Add `private const int SubChangeDisplayThreshold = 5` near the top of the class

**Patterns to follow:** `ChangeItem` record declaration in `SolutionChangeSummary.cs:13`.

**Test scenarios:** None — pure data model, no logic. Verified implicitly by U2–U7.

**Verification:** Code compiles with no errors. Existing `SolutionChangeSummaryTests` still pass.

---

### U2. Entity attribute diff (`Entity.xml`)

**Goal:** When `Entity.xml` is modified (not added or deleted), compute which `<attribute>` elements were added, removed, or changed by comparing old (git HEAD) vs. new (working tree) XML.

**Requirements:** Attribute added: `+ av_name (Type)`. Removed: `- av_name`. Modified (present in both, any property changed): `~ av_name`. Identity: `<LogicalName>` element value.

**Dependencies:** U1.

**Files:**
- `src/Flowline/Utils/SolutionChangeSummary.cs`
- `tests/Flowline.Tests/SolutionChangeSummaryTests.cs`

**Approach:**
- In `ComputeAsync`, after building the `components` dictionary and resolving names, call a new `ResolveSubChangesAsync` for each component whose `parsed.ComponentKey` ends with `/entity`
- `ResolveSubChangesAsync` reads old XML via `git show HEAD:<path>` (null if new file → skip), reads new XML from disk (null if deleted), then delegates to a synchronous `DiffEntityAttributes(oldXml, newXml)` helper
- `DiffEntityAttributes`: parse both XMLs, collect `<attribute>` elements keyed by inner `<LogicalName>` text, classify each as Added/Removed/Modified. For added: extract `<Type>` text to append in parens. Return `List<SubChange>`
- Skip sub-change computation entirely when aggregate status is `Added` (new file)
- For `Deleted` status: only old XML available; all attributes returned as Removed (no type shown, not available in the diff output)

**Technical design (directional):**
```
oldAttribs = dict<logicalName, XElement>  // from HEAD xml
newAttribs = dict<logicalName, XElement>  // from working tree xml

foreach name in newAttribs:
  if not in oldAttribs → Added (name + Type element value)
  else if element differs → Modified (name only)
foreach name in oldAttribs where not in newAttribs:
  → Removed (name only)
```

**Patterns to follow:** `ResolveXmlNameAsync` in `SolutionChangeSummary.cs:363` for the `git show` pattern. `GetLocalizedName` for XDocument parsing style.

**Test scenarios:**
- `Entity.xml` modified: attribute added → SubChanges contains `+ av_new (Text)` with Added status
- `Entity.xml` modified: attribute removed → SubChanges contains `- av_old` with Deleted status
- `Entity.xml` modified: attribute property changed (any element child differs) → SubChanges contains `~ av_changed` with Modified status
- `Entity.xml` modified: no attribute changes (other XML changed) → SubChanges is empty or null
- `Entity.xml` added (new file, git status `??`) → SubChanges is null/empty, no sub-detail
- `Entity.xml` deleted → SubChanges contains all attributes from HEAD as Removed
- Old XML malformed/unparseable → SubChanges is null, no crash, component still shown with name only
- Multiple entities modified in one sync → each gets its own independent SubChanges

**Verification:** `ComputeAsync` returns `ChangeItem` with correct `SubChanges` for a committed-then-modified `Entity.xml` in the integration test git repo.

---

### U3. View diff (`SavedQueries/<guid>.xml`)

**Goal:** When a SavedQuery file is modified, report which grid columns were added/removed and whether filter or sort changed.

**Requirements:** Grid columns from `<cell name="...">` in `<layoutxml>`. Filter flag: any diff in `<fetchxml>` excluding `<order>` and `<attribute>` elements that correspond to grid columns. Sort flag: any diff on `<order>` elements.

**Dependencies:** U1.

**Files:**
- `src/Flowline/Utils/SolutionChangeSummary.cs`
- `tests/Flowline.Tests/SolutionChangeSummaryTests.cs`

**Approach:**
- Trigger: `parsed.ComponentKey` contains `/view/` and aggregate status is `Modified`
- `DiffSavedQuery(oldXml, newXml)`:
  - Collect old and new `<cell name>` sets from `<layoutxml><grid><row>` → diff for Added/Removed columns
  - Compare canonicalized `<fetchxml>` (excluding `<order>` elements) for filter change
  - Compare `<order>` elements for sort change
  - Return combined `List<SubChange>` ordered: columns first (Added then Removed), then filter flag, then sort flag
- Skip when status is Added (new view, no diff)

**Patterns to follow:** XDocument parsing style from `ResolveXmlName` in `SolutionChangeSummary.cs:400`.

**Test scenarios:**
- Column added to layoutxml → SubChanges contains `+ columnname` Added
- Column removed from layoutxml → SubChanges contains `- columnname` Deleted
- `<fetchxml>` filter condition changed → SubChanges contains `~ filter changed` Modified
- `<order>` element changed → SubChanges contains `~ sort changed` Modified
- Only column order in layoutxml changed (no add/remove) → no column sub-changes; filter and sort flags unchanged
- New view (Added status) → no sub-changes
- Both filter and sort changed → both flags present
- Malformed XML → no crash, null sub-changes

**Verification:** Integration test: commit SavedQuery XML, modify it (add a column, change fetchxml filter), run `ComputeAsync`, assert SubChanges contains the expected entries.

---

### U4. OptionSet diff (`OptionSets/<name>.xml`)

**Goal:** When an OptionSet file is modified, report which option labels were added or removed.

**Requirements:** Option identity: `Value` attribute on `<Option>`. Label from `<label description="..." languagecode="1033">`. Format: `+ Label (value)` / `- Label (value)`.

**Dependencies:** U1.

**Files:**
- `src/Flowline/Utils/SolutionChangeSummary.cs`
- `tests/Flowline.Tests/SolutionChangeSummaryTests.cs`

**Approach:**
- Trigger: `parsed.Group == "OptionSets"` and aggregate status is `Modified`
- `DiffOptionSet(oldXml, newXml)`:
  - Collect `<Option>` elements keyed by `Value` attribute
  - Diff: options only in new → Added; only in old → Removed; present in both with different label text → Modified (use new label)
  - Label extracted from `<label description="..." languagecode="1033">` on the option's `<labels>` child, falling back to first label if no 1033 match
- Skip when Added

**Patterns to follow:** `GetLocalizedName` in `SolutionChangeSummary.cs:417` for label extraction pattern.

**Test scenarios:**
- Option added → SubChanges contains `+ New Label (100000003)` Added
- Option removed → SubChanges contains `- Old Label (100000001)` Deleted
- Option label text changed (same value, different label) → SubChanges contains `~ New Label (value)` Modified
- No option changes (other XML diff) → SubChanges empty
- New OptionSet file (Added) → no sub-changes
- Label has no 1033 entry → falls back to first available label
- Malformed XML → no crash

**Verification:** Integration test: commit OptionSet XML with two options, add a third option, run `ComputeAsync`, assert SubChanges contains `+ NewOption (value)`.

---

### U5. Form diff (`FormXml/<type>/<guid>.xml`)

**Goal:** When a form file is modified, report fields added/removed and section/tab structural changes.

**Requirements:** Fields from `datafieldname` attribute on `<cell>` elements. Sections from `name` attribute on `<section>`, label from `<label description="..." languagecode="1033">`. Tabs from `name` attribute on `<tab>`, same label resolution.

**Dependencies:** U1.

**Files:**
- `src/Flowline/Utils/SolutionChangeSummary.cs`
- `tests/Flowline.Tests/SolutionChangeSummaryTests.cs`

**Approach:**
- Trigger: `parsed.ComponentKey` contains `/form/` and aggregate status is `Modified`
- `DiffFormXml(oldXml, newXml)`:
  - Collect all `<cell>` elements with a non-empty `datafieldname` attribute, keyed by that value. Diff → Added/Removed fields
  - Collect `<tab>` elements keyed by `name` attribute. Diff → Added/Removed tabs (label resolved from child `<labels>`)
  - Collect `<section>` elements keyed by `name` attribute. Diff → Added/Removed sections (label resolved from child `<labels>`)
  - Return: fields first (Added then Removed), then tabs, then sections; all ordered within category Added → Removed
- Skip when Added

**Patterns to follow:** Existing XDocument traversal in `SolutionChangeSummary.cs:400-415`.

**Test scenarios:**
- Field added to form → SubChanges contains `+ av_newfield` Added
- Field removed from form → SubChanges contains `- av_oldfield` Deleted
- Section added → SubChanges contains `+ section: My Section` Added
- Section removed → SubChanges contains `- section: Old Section` Deleted
- Tab added → SubChanges contains `+ tab: Summary` Added
- Tab removed → SubChanges contains `- tab: Details` Deleted
- Only layout/position changed (same fields, sections, tabs) → SubChanges empty
- New form file (Added) → no sub-changes
- Malformed XML → no crash

**Verification:** Integration test: commit form XML, add a field and remove a section, run `ComputeAsync`, assert SubChanges contains both changes.

---

### U6. Terminal tree: threshold-based sub-change rendering

**Goal:** Render `SubChanges` in the Spectre tree, capped at `SubChangeDisplayThreshold`. When threshold is 0, show counts inline. When items exceed threshold, show "...and N more (see CHANGES.md)".

**Requirements:** Sub-items ordered: Added → Removed → Modified. Status icons: `+` / `-` / `~` matching existing `StatusIcon`. Count-inline format: `~ entity metadata (3 added, 1 removed)`.

**Dependencies:** U1, U2, U3, U4, U5 (for data to render).

**Files:**
- `src/Flowline/Utils/SolutionChangeSummary.cs`
- `tests/Flowline.Tests/SolutionChangeSummaryTests.cs`

**Approach:**
- Update `AddGroupItems` (and the entity-group path in `WriteTree`) to check `item.SubChanges`
- If `SubChanges` is null or empty, render as before (no change)
- If `SubChangeDisplayThreshold == 0`: append count summary to the component name node inline — `~ entity metadata (2 added, 1 removed)`. Do not add child nodes
- If `SubChangeDisplayThreshold > 0`: add child nodes for up to `SubChangeDisplayThreshold` sub-changes (in Added→Removed→Modified order), then if more remain add a dim `...and N more (see CHANGES.md)` child node

**Patterns to follow:** `AddGroupItems` in `SolutionChangeSummary.cs:281`. `StatusIcon` in `SolutionChangeSummary.cs:297`. Dim text: `[dim]...[/]` as used in verbose paths.

**Test scenarios:**
- SubChanges has 3 items, threshold 5 → all 3 shown as child nodes, no truncation message
- SubChanges has 8 items, threshold 5 → 5 shown, `...and 3 more (see CHANGES.md)` dim node
- SubChanges has 5 items, threshold 5 → all 5 shown, no truncation message (boundary: ≤ threshold shows all)
- Threshold is 0, SubChanges has 3 added 1 removed → component node label ends with `(3 added, 1 removed)`, no child nodes
- Threshold is 0, SubChanges has 1 added 0 removed 2 modified → `(1 added, 2 modified)` (omit zero counts)
- SubChanges is null → renders exactly as before (no child nodes, no count)
- Added status on component + no SubChanges → still shows `+` icon, no sub-nodes
- Sub-items ordered: Added first, then Removed, then Modified in output

**Verification:** `WriteTree` unit tests using `TestConsole` confirm sub-nodes appear under the correct component, truncation message appears when expected, count-inline renders correctly.

---

### U7. CHANGES.md writer + SyncCommand integration

**Goal:** After every sync, write a full-detail markdown file to `solutions/<SolutionName>/CHANGES.md`, overwriting any previous content.

**Requirements:** Markdown structure from requirements doc. Sections appear only when changes exist. Components with no sub-detail listed at section level only (no sub-block). Solution name from `Path.GetFileName(slnFolder)`. Date is today (`DateOnly.FromDateTime(DateTime.Now)`).

**Dependencies:** U1, U2, U3, U4, U5, U6.

**Files:**
- `src/Flowline/Utils/SolutionChangeSummary.cs`
- `src/Flowline/Commands/SyncCommand.cs`
- `tests/Flowline.Tests/SolutionChangeSummaryTests.cs`

**Approach:**
- Add `public async Task WriteChangesFileAsync(string slnFolder, string? envDisplayName, CancellationToken ct = default)` to `SolutionChangeSummary`
- Derive solution name: `Path.GetFileName(slnFolder.TrimEnd(Path.DirectorySeparatorChar, ...))`
- Write header: `# Changes — {solutionName} ({date:yyyy-MM-dd})\n\nSynced from: {envDisplayName ?? "DEV"}`
- Group writes:
  - `## Entities` section: for each entity group, write `### {entityName}`. For each item (entity metadata, views, forms): write bold subheading with all sub-changes as bullet list. If item has no SubChanges, write item name as a plain list item only
  - `## OptionSets` section: for each OptionSet item with SubChanges, write `### {name}` + bullet list
  - Other groups are not included in CHANGES.md (Workflows, Plugin Steps, etc. — out of scope per requirements)
- In `SyncCommand`, after `summary.WriteTree(...)`, call `await summary.WriteChangesFileAsync(slnFolder, devEnv.DisplayName, cancellationToken)`
- If `summary.TotalFiles == 0`, skip writing the file (no changes to record)

**Patterns to follow:** `WriteFlat` and `WriteTree` for the iteration over `Groups` and `Items`. Markdown bullet format: `- + av_name (Text)`, `- - av_name`.

**Test scenarios:**
- Summary with entity attributes added/removed/modified → file contains `## Entities`, `### Account`, `**Attributes**`, correct bullet items
- Summary with view change → file contains view name as bold subheading under entity, column bullets
- Summary with OptionSet change → file contains `## OptionSets`, `### av_name`, option bullets
- Entity with no sub-changes (e.g. ribbon changed) → entity listed under Entities without a sub-block
- Form changes → form name as bold subheading, field/section bullets
- `TotalFiles == 0` → no file written (or file not created)
- `envDisplayName` null → "Synced from: DEV" in header
- File already exists from previous sync → overwritten with new content
- No entity changes (only Workflows changed) → `## Entities` section absent from file
- No OptionSet changes → `## OptionSets` section absent from file

**Verification:** Write `CHANGES.md` to a temp dir, read back and assert expected markdown structure. Verify `SyncCommand` calls `WriteChangesFileAsync` by inspecting the written file after a mock sync scenario.

---

## Scope Boundaries

### In scope
- Entity attributes, views (grid columns + filter/sort flags), option sets, forms (fields + sections/tabs)
- Terminal threshold rendering (hardcoded const 5, 0-mode, overflow truncation)
- CHANGES.md written to solution folder after every sync

### Deferred to Follow-Up Work
- Configurable threshold in `.flowline` (decided against in brainstorm)

### Out of scope (confirmed)
- Modified attribute property detail (required level, display name, etc.) — name only
- Full FetchXML diff — changed/unchanged flag only
- Workflow semantic diff
- Plugin Step sub-detail (step name already encodes context)
- Roles, Dashboards, AppModules, Environment Variables sub-detail

---

## Open Questions

None — all product decisions resolved in brainstorm.
