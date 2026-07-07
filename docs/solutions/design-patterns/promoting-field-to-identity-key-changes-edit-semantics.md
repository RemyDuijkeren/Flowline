---
title: "Promoting a Field to an Identity Key Changes Its Edit Semantics Everywhere Downstream"
date: 2026-07-07
category: docs/solutions/design-patterns/
module: PluginPlanner
problem_type: design_pattern
component: tooling
severity: high
applies_when:
  - "Promoting a field into a matching/identity key used to look up an existing entity for update-vs-create decisions"
  - "Deciding whether a field that disambiguates multiple registrations on one class should also be freely, silently editable"
  - "Auditing warning or deletion-guard logic after an identity/match-key redesign — anything keyed off \"is this an update or a delete\" needs re-checking"
  - "Reviewing whether a step/record carrying an irrecoverable linked resource (e.g. SecureConfig) can end up duplicated when identity-key changes trigger delete+create instead of update"
symptoms:
  - "Test expecting an in-place update instead observes a delete+create once an identity-key field (e.g. stage) changes"
  - "Cross-solution/shared-resource warning only checks the update path, so a step deleted (not updated) due to an identity-key change is silently removed with no warning"
  - "A record protected from deletion by a guard (e.g. SecureConfig-linked) ends up duplicated: old protected row remains active alongside a freshly created replacement"
tags:
  - plugin-registration
  - identity-key
  - step-matching
  - dataverse
  - cross-solution-warnings
  - secure-config
  - upsert-semantics
  - design-decision
related_components:
  - PluginPlanner
  - AddCrossSolutionWarnings
  - SecureConfig deletion guard
---

# Promoting a Field to an Identity Key Changes Its Edit Semantics Everywhere Downstream

## Context

`flowline push` reconciles the plugin steps declared in code (via `[Handles]` attributes) against
the plugin steps that already exist in a Dataverse solution. `PluginPlanner.cs`
(`src/Flowline.Core/Services/PluginPlanner.cs`) is the component that decides, for each declared
step, whether it already exists (→ update in place) or needs to be created, and which existing rows
are now orphaned (→ delete). That decision is made by *matching* — looking up a declared step
against the existing Dataverse rows using some key.

Before this refactor, the match was two-phase: try the generated display `name` first, and only if
that missed, fall back to a content tuple `(sdkmessageid, sdkmessagefilterid, stage, mode)`. This was
fragile: Flowline's own naming convention had already changed once (multi-`[Handles]` stage
qualification), and every time it changes, existing steps stop matching by name, fall through to the
tuple, and — if the tuple doesn't line up either — get deleted and recreated instead of updated. A
plan (`docs/plans/2026-07-07-001-refactor-plugin-matching-identity-plan.md`) fixed this at the root:
make the tuple (extended with `plugintypeid`) the *sole* identity key, and make the display name
write-only — never read back for matching. No more naming-convention churn.

While implementing the new tuple-primary lookup —

```csharp
var dvStepsByKey = dvStepsForType.ToLookup(s => (
    s.GetAttributeValue<EntityReference?>("sdkmessageid")?.Id,
    s.GetAttributeValue<EntityReference?>("sdkmessagefilterid")?.Id,
    s.GetAttributeValue<OptionSetValue>("stage")?.Value,
    s.GetAttributeValue<OptionSetValue>("mode")?.Value));
```

— an existing test failed: a test that changes an existing step's `Stage` from `PreValidation` (10)
to `PostOperation` (20) and expects an in-place update. Under the old code this worked, because the
match was by *name*, and the name doesn't encode stage. Under the new code it broke: changing
`Stage` changes the lookup key itself, so the row that used to match no longer matches. The step is
now treated as brand-new (create), and the old row becomes "obsolete" (delete) — the exact
delete+recreate churn this refactor exists to eliminate, just triggered by a different cause
(editing a field that is now part of the identity key, instead of a naming-convention change).

Tracing this to its root: `stage`, `mode`, `sdkmessageid`, and `sdkmessagefilterid` are in the
identity key *specifically* so that two `[Handles]` registrations on the same plugin class (e.g. a
`PreValidation` step and a separate `PostOperation` step on the same class) can be told apart —
Flowline's build-time uniqueness validation for `[Handles]` depends on exactly this disambiguation.
But those same fields are also fields a developer might legitimately want to edit in place — move a
handler from one stage to another, change which table/message it filters on. **No subset of those
fields can simultaneously (a) be a unique disambiguator across a class's registrations and (b)
remain freely, silently editable — putting a field in the identity key necessarily redefines what
"editing that field" means, everywhere downstream that reads or reacts to the
create/update/delete classification.**

This landed squarely on top of the original ask that started the whole plan: the user had asked "I
think the filter will be updated a lot, which I think should not trigger a registration change, or
am I wrong?" — which read, on its face, as being in direct tension with "identity-key field edits
now recreate the step." Rather than guess, this required stopping mid-implementation and asking two
direct follow-up questions:

1. **Which field did the concern actually mean?** Two candidates looked similar but behaved
   completely differently: the table/message filter (`sdkmessagefilterid` — genuinely in the
   identity key, genuinely in tension) versus `filteringattributes` (the column list a step filters
   change-detection on — never part of the identity key, always a plain mutable field, no tension at
   all). The user meant the latter. No actual conflict existed for the case they were worried about.
2. **For the real tension that remained** (stage/mode/message-filter changes): recreate-on-change
   (matching the new tuple-primary design, accepting a narrow, well-defined behavior change) versus
   reintroducing a name-based fallback for this one case (restoring today's in-place-update
   behavior, at the cost of reopening the exact two-path/naming-fragility structure this plan exists
   to remove). The user chose recreate-on-change — accepted as deliberate, and arguably the more
   *correct* framing anyway: a step registered at a different pipeline stage genuinely runs at a
   different point in Dataverse's execution model, so recreating it isn't just a technical
   compromise, it matches reality.

## Guidance

**When you promote a field into a matching/identity/lookup key, you are not just changing how
lookups succeed or fail — you are silently redefining, for every other piece of code that consumes
the create/update/delete classification produced by that lookup, what "the user edited this field"
now means.** Anything downstream that assumed "editing field X only ever produces an update" now has
a case where editing field X produces a delete-and-create pair instead. If that downstream code
special-cases "update" and "delete" differently — which most reconciliation-adjacent code does,
because deletes are typically more dangerous and get extra guards or warnings — it will silently
stop covering the new case, because it was written and reviewed under the old assumption.

This is not hypothetical: it happened twice in this same change, in two unrelated pieces of code,
and both were only caught because independent reviewers (a full `ce-code-review` pass, and a
cross-model Codex adversarial pass) went looking for exactly this failure mode after the identity
key changed. Neither was anticipated during the original design discussion above — that discussion
resolved *whether* recreate-on-change was acceptable, not *what else in the codebase assumed it
wouldn't happen*.

### Consequence 1 — a warning system that filtered on "update" stopped covering a real case

`AddCrossSolutionWarnings` (same file) warns an operator when a step/image/customapi that also
belongs to *another* Dataverse solution is about to be modified by this push — cross-solution
sharing means an edit here can affect a solution the operator isn't even looking at. Before this
refactor, *every* field edit — including stage/mode — went through the update path
(`plan.Steps.Upserts`, filtered to updates), so checking only that list correctly covered every case
that mattered. The warning code itself was never wrong for what it checked.

After the refactor, a stage/mode/message-filter edit on a step shared with another solution now
takes the *delete* path (`plan.Steps.Deletes`) instead of the update path — a path this warning
check never looked at. Result: the step could be silently deleted out of the other solution with
zero warning. The bug is not "broken logic" — it's "correct logic whose input assumption (edits are
updates) was invalidated by a design decision made in a different part of the same change."

### Consequence 2 — a delete-guard and the new recreate-on-change design combined into a duplicate-active-registration risk

Separately, this same plan added a guard: an obsolete step carrying a linked Secure Configuration (a
config record that, once deleted, can *never* be recovered — Dataverse never returns or exports its
content) is left in place rather than deleted, with a warning instead of a silent delete. Reviewed
in isolation, this guard is correct: never destroy irrecoverable data without telling the operator.
Reviewed in isolation, "identity-key changes recreate the step" is also correct — it was the
explicit, deliberate resolution above.

Combine them: a step *with* a linked Secure Configuration whose stage/mode/filter changes gets a
**new** row created (the old tuple no longer matches, so it looks brand-new) while the **old** row
is protected from deletion (it carries a Secure Configuration). Both the old and the new
registration are now active in Dataverse at the same time — potentially double-executing the same
plugin logic on every triggering operation. Neither piece of code is wrong on its own; the
interaction between them, mediated by the identity-key redesign, is what creates the risk.

## Why This Matters

If this principle is ignored, the failure mode is not a compile error or an obvious test failure —
it's a *silent* correctness or data-loss-adjacent gap that only manifests for the specific
combination of conditions (cross-solution sharing; a Secure-Configuration-linked step whose stage
changes) that the original author didn't happen to test. That is exactly the kind of gap that
survives a first pass of "I wrote a test for the case I thought of" — the two follow-on consequences
here were each found by a *different, independent* reviewer than the one who resolved the original
design tension, specifically because they were looking for "what still assumes the old semantics"
rather than "does the new lookup work."

The practical cost if either had shipped unnoticed: (1) an operator's push silently deletes a step
out of a colleague's solution with no warning, discovered only when that solution's automation stops
firing; (2) a plugin's business logic runs twice per triggering event in production — a class of bug
that is expensive to diagnose because nothing in the push output flags it, and Dataverse itself does
not warn about duplicate active registrations.

## When to Apply

- Whenever a matching/identity/lookup key is designed, extended, or a field is moved into or out of
  it (a `ToLookup`/`Dictionary` key, a composite unique constraint, a reconciliation diff key,
  anything that classifies records into create/update/delete).
- Before shipping, grep for every other place in the codebase that branches on the *result* of that
  classification — warning systems, cascade-delete guards, cache invalidation, audit logging,
  anything keyed off "is this an update vs a delete vs a create" — and ask, for each one: does it
  still cover every case that used to route through "update" and now might route through
  "delete"/"create" instead?
- Treat this as a required review question, not an optional nice-to-have: "what previously assumed
  this field could only ever be updated in place, and is that still true now that it's part of the
  identity key?"
- Also applies to the terminology lesson embedded in the original design conversation: before
  resolving a scope question framed around a field name, confirm precisely which underlying field
  the requester means when more than one field could plausibly match that name (here, "filter" could
  have meant `sdkmessagefilterid` — genuinely in the identity key — or `filteringattributes` — never
  in tension at all). Guessing wrong here would have produced a design decision that solved the
  wrong problem.

## Examples

**Extending the cross-solution warning to cover deletes as well as updates**, via a small shared
helper so both paths use one implementation instead of duplicating the "is this in another solution"
check (`PluginPlanner.cs`):

```csharp
// Before: only Upserts (filtered to updates) were scanned for cross-solution membership.
var updates = plan.Steps.Upserts
    .Concat(plan.Images.Upserts)
    .Concat(plan.CustomApis.Upserts)
    .Concat(plan.RequestParams.Upserts)
    .Concat(plan.ResponseProps.Upserts)
    .Where(a => !a.IsCreate);

foreach (var action in updates)
{
    if (!snapshot.ComponentSolutionMembership.TryGetValue(action.Entity.Id, out var solutions))
        continue;
    // ...build the "other solutions" list, warn if non-empty...
}

// After: a shared helper is called from both the update loop and a new delete loop.
foreach (var action in updates)
    WarnIfInOtherSolutions(plan, snapshot, solutionName, "Updating", action.Entity.LogicalName, action.Name, action.Entity.Id);

var deletes = plan.Steps.Deletes
    .Concat(plan.Images.Deletes)
    .Concat(plan.CustomApis.Deletes)
    .Concat(plan.RequestParams.Deletes)
    .Concat(plan.ResponseProps.Deletes)
    .Concat(plan.PluginTypes.Deletes);

foreach (var action in deletes)
    WarnIfInOtherSolutions(plan, snapshot, solutionName, "Deleting", action.EntityLogicalName, action.Name, action.Id);
```

**Strengthening the Secure-Configuration guard message** to name the duplicate-active-registration
risk explicitly, rather than leaving the operator to infer it (`PluginPlanner.cs`):

```csharp
// Before: warns the obsolete step is being kept, without connecting it to a replacement
// that may have just been created for the same plugin type in this same push.
warnings.Add(
    $"Skipping deletion of step '{stepName}' — has a linked Secure Configuration; remove manually via the Plugin Registration Tool if intended.");

// After: explicitly flags when this same push also created a new step for the same plugin type.
var replacementCreated = stepPlan.Upserts.Any(u => u.IsCreate);
var suffix = replacementCreated
    ? " A new step was also created for this plugin type in this push, so both registrations are now active — verify this is intended."
    : "";
warnings.Add(
    $"Skipping deletion of step '{stepName}' — has a linked Secure Configuration; remove manually via the Plugin Registration Tool if intended.{suffix}");
```

## Residual Risk (accepted, not fixed here)

An independent adversarial review also found a related but out-of-scope race: two `flowline push`
runs against the same environment (two operators, or an overlapping CI retry) can both observe zero
matches for a brand-new step and both create a row with the identical Flowline-generated name —
Dataverse enforces no server-side uniqueness on the identity tuple. Every subsequent push then hits
the tuple-collision path with two identically-named rows, which the existing name-based tiebreak
cannot resolve (the name-match count is always 2, never 1), permanently hard-failing every future
push until an operator manually deletes one duplicate via the Plugin Registration Tool. This is a
concurrency/architecture gap — no push-time lock exists — explicitly accepted as a documented
limitation in the plan rather than fixed in this round. Operators should serialize `flowline push`
runs against the same environment.

## Related

- `docs/solutions/logic-errors/secondary-match-predicate-missing-mode.md` — the ancestor bug this
  refactor was designed to prevent recurring (a missing `mode` field in the old secondary-match
  predicate). Its described `secondaryMatch` code path no longer exists — superseded by the
  tuple-primary lookup this doc describes.
- `docs/solutions/tooling-decisions/plugintype-id-not-needed.md` — already refreshed to describe the
  tuple-primary identity model; no further action needed.
- `CONCEPTS.md` — "Step identity" entry documents the resolved five-field tuple and the Secure
  Configuration collision gate.
- `docs/plans/2026-07-07-001-refactor-plugin-matching-identity-plan.md` — the full decision record.
