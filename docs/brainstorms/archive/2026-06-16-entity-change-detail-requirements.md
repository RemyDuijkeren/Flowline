# Enhanced Component Change Detail in SolutionChangeSummary

**Date:** 2026-06-16
**Status:** Ready for planning

## Problem

`SolutionChangeSummary` currently shows *that* an entity, view, or option set changed — not *what* changed inside it. A developer seeing `~ entity metadata` after sync has no idea whether a column was added, renamed, or just had its required level tweaked without opening the XML.

## Goals

- Show sub-level changes for Entities (attributes), Views (SavedQueries), Forms (FormXml), and OptionSets in both the terminal tree and a written change file.
- Keep the terminal compact by capping named sub-items at a hardcoded threshold.
- Always write `CHANGES.md` to the solution folder so it can be committed alongside the sync checkpoint and reused for commit messages or PR descriptions.

## Behavior

### Source of truth: XML diff

For each changed component file, compare old content (`git show HEAD:<path>`) against new content (working tree). For new files (no HEAD version), skip sub-detail — the component-level added/deleted icon is sufficient. For deleted files, show all items as removed.

---

### Entities — `Entities/<Name>/Entity.xml`

Show attribute-level changes when `Entity.xml` is modified (not added/deleted).

| Change | Terminal format |
|---|---|
| Attribute added | `+ av_fieldname (Type)` |
| Attribute removed | `- av_fieldname` |
| Attribute modified | `~ av_fieldname` |

`Type` is the XML `<Type>` value (e.g. `Text`, `Lookup`, `bit`).

Attribute identity: `<LogicalName>` element.

---

### Views — `Entities/<Name>/SavedQueries/<guid>.xml`

Show three signal types when a view file is modified:

| Change | Terminal format |
|---|---|
| Grid column added | `+ columnname` (from `<cell name="...">` in `<layoutxml>`) |
| Grid column removed | `- columnname` |
| Filter logic changed | `~ filter changed` (any diff inside `<fetchxml>`, excluding column list) |
| Sort changed | `~ sort changed` (any diff on `<order>` elements in `<fetchxml>`) |

---

### Forms — `Entities/<Name>/FormXml/<type>/<guid>.xml`

Show when a form file is modified (lowest implementation priority).

| Change | Terminal format |
|---|---|
| Field added to form | `+ fieldname` (from `datafieldname` attribute on `<cell>` elements) |
| Field removed from form | `- fieldname` |
| Section added | `+ section: SectionName` |
| Section removed | `- section: SectionName` |
| Tab added | `+ tab: TabName` |
| Tab removed | `- tab: TabName` |

Section/tab identity: `name` attribute. Label resolved from `<label description="..." languagecode="1033">`.

---

### OptionSets — `OptionSets/<name>.xml`

Show option-level changes when an OptionSet file is modified (not added/deleted).

| Change | Terminal format |
|---|---|
| Option added | `+ Label (value)` |
| Option removed | `- Label (value)` |
| Option label changed | `~ Label (value)` |

Option identity: `Value` attribute. Label from `<label description="..." languagecode="1033">`. For modified options, use the new label.

---

### Terminal tree display

Sub-items appear as children of the component node in the Spectre tree.

**Threshold:** hardcoded internal constant (start at 5). Controls how many named sub-items are shown per component in the terminal before truncating.

| Threshold value | Behavior |
|---|---|
| `0` | No sub-items shown; count appended inline: `~ entity metadata (3 added, 1 removed)` |
| `N > 0` | Up to N sub-items shown; if more: `...and M more (see CHANGES.md)` |

Sub-items are ordered: added first, then removed, then modified.

---

### CHANGES.md

Written to `solutions/<SolutionName>/CHANGES.md` after every sync. Overwrites previous content.

**Structure:**

```markdown
# Changes — <SolutionName> (<date>)

Synced from: <env display name>

## Entities

### <EntityName>
**Attributes**
- + av_newfield (Text)
- - av_oldfield
- ~ av_status

**My View Name (view)**
- + av_lastactivityon
- - emailaddress1
- ~ filter changed

**My Form Name (main form)**
- + av_newfield
- - section: Old Section

## OptionSets

### av_statuscode
- + Pending Review (100000003)
- - Archived (100000001)
```

Sections only appear when there are changes in that category. Components with no sub-detail (entirely new or deleted) are listed at the section level only, without a sub-block.

## Out of scope

- Configurable threshold in `.flowline` (internal constant only, tunable by developer)
- Modified attribute property detail (e.g. "required level changed") — name only in both tree and file
- Full FetchXML diff — only the changed/unchanged flag
- Workflow semantic diff — flow JSON is too noisy
- Plugin Step detail — step name already encodes context
- Roles, Dashboards, AppModules, Environment Variables
