---
title: Secondary match predicate missing mode field causes sync/async step confusion
date: 2026-06-22
category: docs/solutions/logic-errors/
module: PluginPlanner
problem_type: logic_error
component: tooling
symptoms:
  - Plugin class with both PostOperation sync and PostOperation async [Handles] on same message+table silently matches wrong Dataverse step
  - Wrong step mode mutated in Dataverse (sync step claimed as async or vice versa)
  - No error or warning visible to user during step registration
  - Corrupted step mode persists in Dataverse after push
root_cause: logic_error
resolution_type: code_fix
severity: high
related_components:
  - PluginAssemblyReader
tags:
  - multi-handles
  - plugin-registration
  - dataverse-sync
  - silent-corruption
  - stage-sharing
---

# Secondary match predicate missing mode field causes sync/async step confusion

## Problem

When a plugin class uses stacked `[Handles]` with both PostOperation sync and PostOperation async registrations on the same message+table, the secondary match predicate in `PlanPluginSteps` omits `mode`. Since `ProcessingStage.PostOperation` (value 40) is shared by sync (mode=0) and async (mode=1) steps, `FirstOrDefault` returns whichever Dataverse row appears first — the wrong step gets its mode silently mutated in Dataverse.

## Symptoms

- Sync PostOp handler gets `mode` set to 1 (async) in Dataverse; async handler created fresh instead of matched.
- Opposite case: async PostOp handler gets `mode` set to 0 (sync).
- No warning or error — silent mode corruption invisible until the step fires at the wrong timing.
- Only triggered via the secondary match path (multi-`[Handles]` step renames); single-`[Handles]` is unaffected.

## What Didn't Work

- **Reviewing only the primary match**: The primary path was correct (includes mode). Secondary match looked "similar enough" and wasn't scrutinized for stage/mode collision.
- **Assuming stage value uniqueness**: Stage 40 documents PostOperation, but the schema allows both sync and async at stage 40. True step identity requires `(message, filter, stage, mode)`.
- **Missing parity check**: `ValidateSecondaryTable` was called in the single-`[Handles]` path (line 375) but not in `BuildMultiHandlesSteps`. Both paths accept `secondaryTable` and must enforce identical validation.

## Solution

### Primary fix — add `mode` to secondary match predicate

**File:** `src/Flowline.Core/Services/PluginPlanner.cs:224`

```csharp
// Before (buggy) — stage 40 is ambiguous: PostOp sync AND async
var secondaryMatch = dvSteps.Values.FirstOrDefault(s =>
    s.GetAttributeValue<EntityReference?>("sdkmessageid")?.Id == messageId &&
    s.GetAttributeValue<EntityReference?>("sdkmessagefilterid")?.Id == filterId &&
    s.GetAttributeValue<OptionSetValue>("stage")?.Value == asmStep.Stage &&
    !asmStepNames.Contains(s.GetAttributeValue<string>("name")));

// After (fixed) — mode makes the lookup fully qualified
var secondaryMatch = dvSteps.Values.FirstOrDefault(s =>
    s.GetAttributeValue<EntityReference?>("sdkmessageid")?.Id == messageId &&
    s.GetAttributeValue<EntityReference?>("sdkmessagefilterid")?.Id == filterId &&
    s.GetAttributeValue<OptionSetValue>("stage")?.Value == asmStep.Stage &&
    s.GetAttributeValue<OptionSetValue>("mode")?.Value == asmStep.Mode &&
    !asmStepNames.Contains(s.GetAttributeValue<string>("name")) &&
    !secondaryMatchedIds.Contains(s.Id));
```

The `!secondaryMatchedIds.Contains(s.Id)` guard also makes the exclusion explicit rather than relying on in-place name mutation to prevent re-matching the same Dataverse row.

### Secondary fix — enforce `ValidateSecondaryTable` parity

**File:** `src/Flowline.Core/Services/PluginAssemblyReader.cs` — `BuildMultiHandlesSteps`

The single-`[Handles]` path validated secondary table (line 375). Multi-`[Handles]` path skipped it. Fix: added `ValidateSecondaryTable(type.Name, msg, secondaryTable)` per-handle inside the `BuildMultiHandlesSteps` loop, called before `MapHandlesStage`.

```csharp
// Added per-handle inside BuildMultiHandlesSteps loop
var msg = ParseHandlesMessage(hAttr, type.Name);
ValidateSecondaryTable(type.Name, msg, secondaryTable);  // ← parity fix
var (s, m) = MapHandlesStage(...);
```

This ensures multi-`[Handles]` rejects invalid secondary tables (e.g., `SecondaryTable="account"` on Create/Update) the same way single-`[Handles]` does.

## Why This Works

`ProcessingStage.PostOperation = 40` appears in both sync and async Dataverse rows. Without `mode` in the predicate, snapshot iteration returns the first matching row — a coin flip between sync and async. The secondary match is only invoked during multi-`[Handles]` step renames and is the sole path that can encounter sync/async ambiguity on the same stage. Adding `mode` makes the predicate unambiguous.

The parity bug existed because the multi-`[Handles]` path was added later and did not mirror all validators from the single-`[Handles]` path.

## Prevention

**Test both modes at PostOperation on the same class**

```csharp
[Step("account")]
[Handles(Message.Create, Stage.PostOperation)]    // sync (mode=0)
[Handles(Message.Create, Stage.PostOperationAsync)] // async (mode=1)
internal class MockBothModesPlugin : IPlugin { ... }
```

Assert the planner produces one sync step and one async step, each with the correct `mode` value.

**Test invalid SecondaryTable in multi-`[Handles]`**

```csharp
[Step("contact", SecondaryTable = "account")]
[Handles(Message.Create, Stage.PostOperation)]
[Handles(Message.Update, Stage.PostOperation)]
internal class MockErrSecondaryTablePlugin : IPlugin { ... }
```

Assert `InvalidOperationException` is thrown — identical to what single-`[Handles]` would throw.

**Code review checklist for new code paths that parallel existing ones**

When adding `BuildMultiHandlesSteps` (or any alternative to `TryBuildSteps`), grep for every validator call in the original path and verify each appears in the new path too. Divergences in `Validate*` calls are the most common parity bug pattern in this codebase.

**Step identity predicate rule**

All step-matching predicates must include all five discriminating fields: `plugintypeid`, `messageId`, `filterId`, `stage`, **and `mode`**. Stage alone is insufficient because PostOperation allows both modes at value 40.

## Related Issues

- `docs/solutions/design-patterns/attribute-per-dataverse-registration-2026-05-29.md` — establishes `[Handles]` design rationale and why step identity is encoded in class attributes
- Multi-`[Handles]` requirements doc (`docs/brainstorms/2026-06-21-multi-handles-multi-step-requirements.md`) underspecified the predicate tuple — did not list `mode` in the fallback match fields
