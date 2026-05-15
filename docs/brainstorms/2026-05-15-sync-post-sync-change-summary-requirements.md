---
date: 2026-05-15
topic: sync-post-sync-change-summary
---

# SyncCommand: Post-Sync Change Summary

## Summary

After `pac solution sync` completes, print a change summary showing how many files changed and which solution components were affected, with human-readable names resolved from file paths and XML content. On `--verbose`, expand each group to list individual file paths. When nothing changed, print a distinct no-op message instead.

---

## Problem Frame

After sync completes, the developer has no signal about what actually changed. A sync that pulled 400 modified XML files looks identical to one that changed nothing — both print the same "Synced! Run 'git commit'" finish line. The developer must leave the terminal and run `git diff` manually to understand scope.

This matters for two reasons:
1. **No-op ambiguity** — if the solution was already up to date, the commit nudge is misleading.
2. **Scope blindness** — a sync that touches 12 Account forms and 3 workflows carries very different review weight than one that touched a single field label. Developers can't triage without running a separate git command.

---

## Behaviour

Both default and verbose output use Spectre.Console's `Tree` control (`AnsiConsole.Write(tree)`). Follow the pattern in `src/Flowline.Core/Services/PluginService.cs` → `WriteSnapshotVerbose` (line 292).

### Default output (changes detected)

After `pac solution sync` returns, compute the change summary and render a two-level tree:

```
Solution synced from Dataverse in 4.2s
Changes (9 files, +47 -23)
├── Account: Information (main), Active Accounts view
├── Contact: entity metadata
├── dh_SpotlerAutomation: Information (main), Active Spotler Automations view, All Spotler Automations view
└── Workflows: AccountWF01-CreateSpotlerJob, ContactBR01-LockSpotlerInfo

  Synced! Run 'git commit' to save a checkpoint.
```

- Tree root: `Changes (N files, +A -B)` — meaningful file count (excluding managed/sidecar files, see File Exclusions)
- Top-level nodes: entity name or component group
- Component names listed inline (comma-separated) on each node
- Commit nudge always shown (outside the tree, after it)

### Verbose output (`--verbose`, changes detected)

Three-level tree — group → component name → file path(s):

```
Solution synced from Dataverse in 4.2s
Changes (9 files, +47 -23)
├── Account
│   ├── Information (main)
│   │   └── Entities/Account/FormXml/main/{1fed44d1-ae68-4a41-bd2b-f13acac4acfa}.xml
│   └── Active Accounts view
│       └── Entities/Account/SavedQueries/{58fb20ff-d5be-406f-908e-c777e9dedf5f}.xml
├── dh_SpotlerAutomation
│   ├── Information (main)
│   │   └── Entities/dh_SpotlerAutomation/FormXml/main/{07f3f88a-febc-4ad7-8bf9-72e82e458c25}.xml
│   └── Active Spotler Automations view
│       └── Entities/dh_SpotlerAutomation/SavedQueries/{1a6caa22-08dc-4d15-aa0e-f49f98deab3e}.xml
└── Workflows
    └── AccountWF01-CreateSpotlerJob
        └── Workflows/AccountWF01-CreateSpotlerJob-45534473-EA8B-4AD0-A20F-67C7F430C5FA.xaml

  Synced! Run 'git commit' to save a checkpoint.
```

### No-op output (nothing changed)

When the diff is empty:

```
Solution synced from Dataverse in 3.1s
No changes pulled from DEV.

  Synced! Run 'git commit' to save a checkpoint.
```

- No headline, no breakdown
- Commit nudge still shown — developer may have Plugin/WebResource changes to commit

---

## Component Name Resolution

The change summary uses two name sources: **file path** (no I/O) and **XML read** (open current file on disk for added/modified; for deleted files, read from the previous git commit via `git show HEAD:{path}`).

### Entity components

Files under `Entities/{EntityName}/` are grouped by entity name. Within each entity group:

| Sub-path | Display name | Source |
|---|---|---|
| `FormXml/{type}/{Guid}.xml` | `{title} ({type})` | XML: `/forms/systemform/LocalizedNames/LocalizedName[@languagecode="1033"]/@description` |
| `SavedQueries/{Guid}.xml` | view title | XML: `/savedquery/LocalizedNames/LocalizedName[@languagecode="1033"]/@description` |
| `Entity.xml` | `entity metadata` | static |
| `RibbonDiff.xml` | `ribbon` | static |
| `Formulas/{name}.xaml` | `formula: {name}` | filename (strip `.xaml`) |

Form type values from folder name: `main`, `quick`, `card`.

Language fallback for XML reads: prefer `languagecode="1033"` (English); if absent, use the first `<LocalizedName>` found.

### Non-entity components

Files NOT under `Entities/` are grouped by their top-level folder:

| Path pattern | Group label | Item display name | Source |
|---|---|---|---|
| `Workflows/{Name}-{Guid}.xaml` | `Workflows` | strip GUID suffix from filename | filename |
| `OptionSets/{Name}.xml` | `OptionSets` | schema name | filename (strip `.xml`) |
| `Roles/{Name}.xml` | `Roles` | role name | filename (strip `.xml`) |
| `environmentvariabledefinitions/{Name}/` | `Environment Variables` | variable schema name | folder name |
| `SdkMessageProcessingSteps/{Guid}.xml` | `Plugin Steps` | step name | XML: root element `Name` attribute |
| `Dashboards/{Guid}.xml` | `Dashboards` | dashboard name | XML: `/Dashboard/LocalizedNames/LocalizedName[@languagecode="1033"]/@description` |
| `AppModules/{Name}/` | `App Modules` | app name | folder name |
| `WebResources/{folder}/{file}.{ext}.data.xml` | `Web Resources` | `{folder}/{file}.{ext}` | filename (strip `.data.xml`) |
| `Other/` | skip | — | solution metadata, not shown |

### File exclusions

These files are excluded from the count and the breakdown entirely:

- `*_managed.xml` — always paired with the unmanaged version; same logical change shown once
- `*.xaml.data.xml` — metadata sidecar for workflow files; the `.xaml` file represents the change
- `*.dll.data.xml` — plugin assembly binary metadata; changes captured by the assembly folder name

The headline file count is computed from the filtered list, not from raw `git diff --stat`, so it matches what the breakdown shows.

---

## Scope Boundaries

**In scope:**
- Headline (meaningful file count, +lines, −lines)
- Component breakdown with human-readable names, grouped by entity or component type
- `--verbose` per-file path expansion under each component item
- No-op detection with distinct message
- Deleted file name resolution via `git show HEAD:{path}`

**Out of scope:**
- Per-group or per-component line counts (file counts per group only)
- PAC omission warnings (Plugin Packages, canvas apps) — covered by idea #2
- Sorting or ordering within groups (implementation choice)
- Warnings about unknown folder structures or unrecognised component types

---

## Success Criteria

- Sync with real changes prints headline + grouped component breakdown before the finish line
- Sync with no changes prints `No changes pulled from DEV.` with no headline or breakdown
- Component names are human-readable (form title + type, view name, workflow name stripped of GUID)
- `--verbose` shows file path(s) under each named component
- Deleted components show a resolved name, not a raw GUID
- Managed/sidecar files excluded from count and breakdown
- All output consistent with tone-of-voice guide (`docs/tone-of-voice.md`)
- Existing `SyncCommand` tests still pass