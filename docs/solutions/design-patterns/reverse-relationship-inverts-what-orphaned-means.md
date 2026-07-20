---
title: "A Reverse Relationship Inverts What \"Orphaned\" Means — Custom APIs Are Not Owned by Their Plugin Type"
date: 2026-07-20
category: docs/solutions/design-patterns/
module: PluginPlanner
problem_type: design_pattern
component: tooling
severity: high
applies_when:
  - "Writing or reviewing an orphan sweep — any code that deletes a Dataverse record because it has \"no local source\""
  - "Extending a reconciliation path from one entity type to another that looks similar but points the other way"
  - "Deciding the scope of a query that feeds a deletion decision, especially when no assembly-shaped handle exists"
  - "Reviewing a fix that widens a \"known ids\" set rather than changing what the code does with an unknown"
symptoms:
  - "Pushing one plugin project deletes another project's live Custom APIs, with no --force flag involved"
  - "The same class of bug is fixed twice in different code paths, both times by adding more ids to a known-set"
  - "A deletion query is scoped by publisher prefix while every neighbouring sweep is scoped by assembly"
  - "A variable named `unlinkedApi` or similar decides a delete, where \"unlinked\" actually means \"we could not attribute it\""
tags:
  - plugin-registration
  - custom-api
  - orphan-cleanup
  - dataverse
  - relationship-direction
  - deletion-safety
  - api-surface
  - design-decision
related_components:
  - PluginPlanner
  - PluginReader.GetRegisteredCustomApisAsync
  - PluginService
---

# A Reverse Relationship Inverts What "Orphaned" Means

## Context

`flowline push` reconciles what a plugin assembly declares in code against what the target Dataverse
solution already holds, and removes registrations that no longer have local source behind them. Three
sweeps do this: orphan assemblies, orphan steps, and unlinked Custom APIs.

The first two are assembly-scoped and gated behind `--force delete-orphans`. The third was neither.
It built `DeleteAction`s straight into the normal plan, and its rule was:

```csharp
var typeId = a.GetAttributeValue<EntityReference>("plugintypeid")?.Id;
return typeId == null || typeId == Guid.Empty || !knownPluginTypeIds.Contains(typeId.Value);
```

Three conditions, one action. When multi-plugin-project support landed, two plugin projects under one
publisher deleted each other's Custom APIs on every ordinary push.

That bug was found and fixed **twice** — once for the classic `.dll` path, once for the NuGet package
path — and both fixes did the same thing: widen `knownPluginTypeIds` with the sibling projects' plugin
type ids so fewer APIs fall into the unknown branch. Both were reviewed and both passed. The second
one was only caught because a reviewer asked why the first fix had been applied to one branch of a
two-branch method.

## The root cause is the direction of the relationship

Steps and Custom APIs look like the same shape and are not:

| | Foreign key | Reading | Consequence |
|---|---|---|---|
| Step | `sdkmessageprocessingstep.plugintypeid` -> `plugintype` | the step is **owned by** the type | delete the type and the step cascades; "ours" is well-defined per assembly |
| Custom API | `customapi.plugintypeid` -> `plugintype` | the API **references** the type as its implementation | the API is the parent; it can exist with no `plugintypeid` at all |

The arrow points the same way in the schema and means the opposite thing. For a step, the plugin type
is the parent. For a Custom API, the plugin type is a detail hanging off the API.

So the sweep's premise — *its plugin type is gone, therefore the API is orphaned* — reasons up a
relationship that points down. **Losing an implementation does not orphan a contract.**

Two things compound it:

1. **The query has no assembly-shaped handle.** `PluginReader.GetRegisteredCustomApisAsync` filters
   `uniquename BeginsWith "<prefix>_"` — publisher-wide, because a Custom API's `plugintypeid` does not
   narrow to one physical DLL the way a step's does. Every neighbouring sweep is assembly-scoped; this
   one structurally cannot be. Its input therefore includes other projects' and other repos' APIs.
2. **A Custom API is a public API surface.** External callers depend on it. The bar for deleting one is
   higher than for a step, not lower.

## Guidance

**Positive attribution, not exclusion by unknown.** Delete only what you can affirmatively show you own.
The corrected rule:

| Case | Action |
|---|---|
| `plugintypeid` is one of **ours**, and the API is no longer declared in source | delete on a normal push |
| `plugintypeid` is set, but not one we own | **never delete** — not ours; report under `--verbose` |
| `plugintypeid` is null or `Guid.Empty` | delete **only** under `--force delete-orphans` |

The third row is the one that looks like a regression and is not. An API with no implementation may be a
contract awaiting one, or one whose handler was deliberately detached. It goes behind the same gate as
every other unknown.

**Widening a known-set is a symptom fix.** If the answer to "we deleted something we shouldn't have" is
"add more ids to the known-set", check whether the real problem is that *unknown maps to delete*. With a
publisher-wide query the unknown bucket can be shrunk but never emptied — so the bucket, not its size, is
the defect. Positive attribution removes the bucket: a sibling's API is not protected by enumeration, it
is simply never considered.

**Name the test, not the null check.** The original loop variable was `unlinkedApi`. In this codebase
"unlinked" reads as "orphaned", which is exactly the wrong model and quietly argued for deletion every
time someone read the loop. Name such things after the ownership question being asked.

## Why This Matters

The failure mode is silent, irreversible, and lands on a public contract. Deleting a step breaks an
integration you own; deleting a Custom API breaks whatever a third party wired into it, and the push
that did it reported success.

It is also a bug that survives review. Both patches were locally correct — they genuinely fixed the case
in front of them — which is what let the same defect ship twice. Reviewing "does this widen the set
correctly?" cannot catch "should an unknown be deleted at all?".

## When to Apply

Reach for this whenever a reconciliation path is extended to a new entity type. Before reusing an orphan
rule, ask three questions:

1. **Which end owns which?** Read the foreign key, then say out loud which record is the parent. A shared
   FK direction does not imply shared ownership semantics.
2. **What is the query's scope, and does it match the sweep's premise?** A sweep that reasons about "what
   we own" needs a query scoped to what we own. If the entity has no handle that narrows that far, the
   sweep cannot use exclusion — it must use attribution.
3. **Is this a contract or an implementation detail?** Anything an external caller can bind to earns a
   `--force` gate at minimum.

## Examples

**The mistake.** Two plugin projects, `Sales` and `Support`, under publisher prefix `contoso`. Pushing
`Sales` queries every `contoso_*` Custom API, finds `Support`'s API pointing at a plugin type absent from
`Sales`'s snapshot, classifies it unowned, and deletes it. `Support`'s next push recreates it and deletes
`Sales`'s. No `--force` at any point.

**The symptom fix that did not hold.** Supply `Support`'s plugin type ids so `Sales` recognises them.
Correct as far as it goes — and silent about a third project, another repo under the same publisher, or
the branch of the method nobody updated.

**The root fix.** `Sales` considers only Custom APIs whose `plugintypeid` is in `Sales`'s own plugin
types. `Support`'s API is not protected; it is never examined.

## Residual Risk (accepted, not fixed here)

A Custom API that Flowline genuinely orphaned — created by a project since deleted — now survives until
someone runs `--force delete-orphans` or removes it in the maker portal. That is the deliberate trade:
under-deletion is recoverable and visible, over-deletion of a public API surface is neither.

## Related

- `docs/solutions/design-patterns/extending-identity-key-plan-files-list-incomplete.md` — the same family:
  a change to what identifies a record, with consumers left behind. That one is about enumerating
  incompletely; this one is about reasoning in the wrong direction, which enumeration cannot fix.
- `docs/solutions/design-patterns/promoting-field-to-identity-key-changes-edit-semantics.md` — changing an
  identity key changes what "edit" means downstream.
- `docs/plans/2026-07-15-001-feat-multi-plugin-project-support-plan.md` — the work that made the latent
  defect reachable.
