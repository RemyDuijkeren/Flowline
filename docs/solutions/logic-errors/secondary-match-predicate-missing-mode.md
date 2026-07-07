---
title: Step-matching predicates must include mode, not just stage — sync/async PostOperation confusion
date: 2026-06-22
last_updated: 2026-07-07
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
  - identity-key
---

# Step-matching predicates must include mode, not just stage — sync/async PostOperation confusion

## Problem

When a plugin class uses stacked `[Handles]` with both PostOperation sync and PostOperation async
registrations on the same message+table, a step-matching predicate that omits `mode` will confuse
them. Since `ProcessingStage.PostOperation` (value 40) is shared by sync (mode=0) and async (mode=1)
steps, a lookup missing `mode` can silently match the wrong Dataverse row and mutate its mode.

**Historical note:** this bug was originally found in a *secondary-match fallback* predicate — see
"Original context" below. `PluginPlanner`'s step matching has since been redesigned so there is no
longer a secondary-match fallback at all; the identity key (which always included `mode`) is now the
*sole* lookup path. The underlying rule this doc documents — mode must be part of any predicate that
disambiguates steps sharing a stage — is unchanged and is now enforced by construction rather than by
a predicate an author could omit it from. See "Current state" below for what the code looks like today.

## Symptoms

- Sync PostOp handler gets `mode` set to 1 (async) in Dataverse; async handler created fresh instead of matched.
- Opposite case: async PostOp handler gets `mode` set to 0 (sync).
- No warning or error — silent mode corruption invisible until the step fires at the wrong timing.

## What Didn't Work

- **Reviewing only the primary match**: the primary path was correct (included mode); a fallback path added later looked "similar enough" and wasn't scrutinized for the same stage/mode collision.
- **Assuming stage value uniqueness**: stage 40 documents PostOperation, but the schema allows both sync and async at stage 40. True step identity requires `(plugintypeid, message, filter, stage, mode)`.
- **Missing parity check**: `ValidateSecondaryTable` was enforced in the single-`[Handles]` path but was missing from the multi-`[Handles]` path (`BuildMultiHandlesSteps`) when it was first added. Both paths accept `secondaryTable` and must enforce identical validation — see "Secondary fix" below, still current.

## Current state (as of the tuple-primary identity refactor)

`PluginPlanner.cs`'s `PlanPluginSteps` no longer has a name-primary/tuple-secondary two-path
structure at all. The identity key — `(plugintypeid, sdkmessageid, sdkmessagefilterid, stage,
mode)` — is the sole lookup, built via a single `ToLookup` grouping (`dvStepsByKey`) rather than a
name-keyed dictionary with a name-miss fallback. `mode` is one of the five fields in that key by
construction, so the specific "predicate omits mode" mistake this doc documents can no longer be
made by accident in a fallback path — there is no fallback path to omit it from. The general
principle survives in a stronger form: see `CONCEPTS.md`'s **Step identity** entry, and
`docs/solutions/design-patterns/promoting-field-to-identity-key-changes-edit-semantics.md` for the
broader lesson this redesign surfaced (promoting a field into an identity key changes what "editing
that field" means everywhere downstream, not just at the lookup site).

Regression coverage for the sync/async disambiguation this doc originally reported now lives at two
layers: `Analyze_MultiHandles_PostOperationAsyncSuffix_DistinguishesFromSync` in
`tests/Flowline.Core.Tests/PluginAssemblyReaderTests.cs` (display-name qualification) and
`Plan_TwoHandlesSameStageDifferentMode_MatchIndependently` in
`tests/Flowline.Core.Tests/PluginPlannerTests.cs` (the actual `PlanPluginSteps` matching layer —
added specifically to close a gap where the sync/async case was untested at the layer that matters).

## Original context (historical — the code below no longer exists)

The bug was originally found in this shape, in a secondary-match fallback predicate that has since
been removed entirely:

```csharp
// Before (buggy) — stage 40 is ambiguous: PostOp sync AND async
var secondaryMatch = dvSteps.Values.FirstOrDefault(s =>
    s.GetAttributeValue<EntityReference?>("sdkmessageid")?.Id == messageId &&
    s.GetAttributeValue<EntityReference?>("sdkmessagefilterid")?.Id == filterId &&
    s.GetAttributeValue<OptionSetValue>("stage")?.Value == asmStep.Stage &&
    !asmStepNames.Contains(s.GetAttributeValue<string>("name")));

// After (fixed at the time) — mode makes the lookup fully qualified
var secondaryMatch = dvSteps.Values.FirstOrDefault(s =>
    s.GetAttributeValue<EntityReference?>("sdkmessageid")?.Id == messageId &&
    s.GetAttributeValue<EntityReference?>("sdkmessagefilterid")?.Id == filterId &&
    s.GetAttributeValue<OptionSetValue>("stage")?.Value == asmStep.Stage &&
    s.GetAttributeValue<OptionSetValue>("mode")?.Value == asmStep.Mode &&
    !asmStepNames.Contains(s.GetAttributeValue<string>("name")) &&
    !secondaryMatchedIds.Contains(s.Id));
```

This fix was itself later superseded by removing the two-path structure altogether (see "Current
state" above) rather than by further patching this predicate.

### Secondary fix — `ValidateSecondaryTable` parity (still current, unrelated to the identity refactor)

**File:** `src/Flowline.Core/Services/PluginAssemblyReader.cs` — `BuildMultiHandlesSteps`

The single-`[Handles]` path validated secondary table. The multi-`[Handles]` path (`BuildMultiHandlesSteps`)
initially skipped it. Fix: call `ValidateSecondaryTable(type.Name, msg, secondaryTable)` per-handle
inside the `BuildMultiHandlesSteps` loop, before `MapHandlesStage`. This call is still present in the
current codebase and is unaffected by the step-matching identity refactor — it validates assembly
metadata at build time, not Dataverse matching at push time.

## Why This Works

`ProcessingStage.PostOperation = 40` appears in both sync and async Dataverse rows. Any predicate
that disambiguates steps sharing a stage must include `mode`, or an ambiguous match becomes possible.
Today this is enforced by construction (mode is one of the five identity-key fields, not an optional
predicate clause an author writes and could omit), which is a stronger guarantee than "remember to
add mode to every new predicate."

## Prevention

**Test both modes at PostOperation on the same class** — covered today by
`Analyze_MultiHandles_PostOperationAsyncSuffix_DistinguishesFromSync`
(`PluginAssemblyReaderTests.cs`) and `Plan_TwoHandlesSameStageDifferentMode_MatchIndependently`
(`PluginPlannerTests.cs`).

**Test invalid SecondaryTable in multi-`[Handles]`** — assert `InvalidOperationException` is thrown
for an invalid `SecondaryTable` on a multi-`[Handles]` class, identical to what single-`[Handles]`
throws.

**Code review checklist for new code paths that parallel existing ones**

When adding an alternative code path to an existing one (e.g. a new build/match strategy), grep for
every validator or key-field call in the original path and verify each appears in the new path too.
Divergences in validation or key-construction calls are a recurring parity-bug pattern in this
codebase.

**Step identity rule (see `CONCEPTS.md` for the current, authoritative statement)**

All step-matching logic must key on all five discriminating fields: `plugintypeid`, `sdkmessageid`,
`sdkmessagefilterid`, `stage`, and `mode`. Stage alone is insufficient because `PostOperation` allows
both modes at value 40. This is no longer a predicate an author writes per code path — it is the
sole identity key's field set.

## Related

- `CONCEPTS.md` — **Step identity** entry: the current, authoritative statement of the five-field
  identity key.
- `docs/solutions/design-patterns/promoting-field-to-identity-key-changes-edit-semantics.md` — the
  broader design lesson the tuple-primary redesign surfaced.
- `docs/plans/2026-07-07-001-refactor-plugin-matching-identity-plan.md` — the plan that removed the
  two-path (name-primary/tuple-secondary) structure this doc originally described.
- `docs/solutions/design-patterns/attribute-per-dataverse-registration-2026-05-29.md` — `[Handles]`
  design rationale and why step identity is encoded in class attributes.
