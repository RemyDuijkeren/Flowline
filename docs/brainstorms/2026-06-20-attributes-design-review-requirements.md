# Flowline.Attributes Design Review — Requirements

**Date:** 2026-06-20
**Scope:** Review and improve `Flowline.Attributes` — naming, params, convention-before-configuration, KISS, and missing coverage. Based on a full audit of `src/Flowline.Attributes/` against `src/Flowline.Core/Services/PluginAssemblyReader.cs` and the original design brainstorm at `docs/ideation/BRAINSTORM_ATTRIBUTES.md`.

---

## Outcome

One concrete change: add five missing Dataverse SDK messages to the `Message` enum.

Everything else in the current design was reviewed and confirmed. The attribute surface is sound.

---

## What was reviewed and confirmed

| Area | Decision |
|---|---|
| `[Step]` naming | Correct. Names the registration artifact ("plugin step"), aligns with Plugin Registration Tool vocabulary, and pairs cleanly with `[CustomApi]`. |
| `[Handles]` naming | Correct. Reads naturally ("this class handles Update at PreOperation") and is clearly an escape hatch. |
| `[Filter]` naming | Correct. Matches the Dataverse term "filtering attributes." |
| `[PreImage]` / `[PostImage]` split | Correct. Separate attributes over a single generic `[Image(name, alias, ImageType, …)]` eliminates ImageType mistakes and gives sensible default aliases. |
| `Name => Alias` on images | Keep collapsed. Alias drives both the code key and the display name. Simpler, devs only care about the key. |
| `"none"` sentinel on `[Step]` | Correct. "none" is the actual value Dataverse Plugin Registration Tool stores for Primary Entity on global steps — not backwards semantics. |
| Warning when `[Step]` has no table | Keep. A global Update step firing on every entity change in the environment is a costly mistake. The warning is a useful guardrail. |
| `Validation` as the only stage keyword | Keep. One canonical form over `Validate`/`Validation` aliases. `Validation` reads better in the class name pattern (`AccountValidationUpdatePlugin` = "at the Validation stage for Update"). |
| `FieldType.Picklist` | Keep. Target audience writes C# code against the Dataverse SDK where the type is `OptionSetValue`. `Picklist` bridges that mental model better than `Choice`. |
| `SecondaryTable` on `[Step]` | Keep. It is step identity (defines *which* step is registered), not a behavioral add-on like `[Filter]` or `[PreImage]`. Extracting it to `[Secondary("contact")]` was considered and rejected. |
| No `[Ignore]` attribute | Confirmed YAGNI. The scanner already filters `IsAbstract: false` and silently skips any concrete `IPlugin` without `[Step]` or `[CustomApi]`. |

---

## Required change: add missing messages to `Message` enum

**File:** `src/Flowline.Attributes/Message.cs`

Five Dataverse SDK messages are missing from the enum. Plugin developers writing bulk-optimized plugins today must fall back to `[Handles("CreateMultiple", Stage.PostOperation)]` instead of using the typed enum.

| Message | Available for | Notes |
|---|---|---|
| `Upsert` | Standard + elastic tables | Single-record create-or-update. Available since SDK 9.x. |
| `CreateMultiple` | Standard + elastic tables | Bulk create. Performance-optimized; `Targets` is `EntityCollection`. |
| `UpdateMultiple` | Standard + elastic tables | Bulk update. Supports `[Filter]`; see downstream impacts below. |
| `UpsertMultiple` | Tables supporting both `CreateMultiple` and `UpdateMultiple` | Bulk upsert. |
| `DeleteMultiple` | Elastic tables only | Bulk delete. Using on a standard table returns an error at runtime. |

Add all five in alphabetical sort order with XML doc comments. Mark `DeleteMultiple` with a note that it is elastic-table-only.

### Downstream impacts in `PluginAssemblyReader`

Adding these enum values exposes two places in `PluginAssemblyReader.cs` that currently hard-code `"Update"` and need to be extended:

**1. `ValidateFilter` — allow `[Filter]` on `UpdateMultiple`**

```csharp
// Current
if (filteringColumns == null || message == "Update") return;

// After
if (filteringColumns == null || message is "Update" or "UpdateMultiple") return;
```

The filter warning ("Update step has no `[Filter]`") should also fire for `UpdateMultiple`:

```csharp
// Current
if (message == "Update" && filteringColumns == null)

// After
if (message is "Update" or "UpdateMultiple" && filteringColumns == null)
```

**2. `ImageSupportedMessages` — add `CreateMultiple` and `UpdateMultiple`**

```csharp
private static readonly HashSet<string> ImageSupportedMessages =
[
    "Assign", "Create", "CreateMultiple", "Delete", "DeliverIncoming", "DeliverPromote",
    "Merge", "Route", "Send", "SetState", "Update", "UpdateMultiple"
];
```

`UpsertMultiple` and `DeleteMultiple` image support is not documented and should not be assumed — leave them out until confirmed.

---

## Scope boundaries

**Not in scope:**
- `[Secondary("contact")]` as a separate attribute — rejected; `SecondaryTable` stays on `[Step]`
- `[Ignore]` attribute — YAGNI
- Renaming `FieldType.Picklist` to `Choice`
- Accepting `Validate` as a stage keyword alias — keep `Validation` only
- Splitting `Name` and `Alias` on `[PreImage]`/`[PostImage]`

**Outstanding assumptions:**
- `UpsertMultiple` and `DeleteMultiple` image support is unverified; treat as unsupported until tested against a real environment.
- `DeleteMultiple` on standard tables returns a runtime error ("DeleteMultiple has not yet been implemented") — this cannot be caught at registration time. Flowline emits a **warning** (not an error) in `BuildStepWarnings` when a step is registered on `DeleteMultiple`, advising the developer to verify their table is elastic. Throwing is wrong because Flowline has no Dataverse connection during assembly analysis and cannot distinguish elastic from standard tables.
