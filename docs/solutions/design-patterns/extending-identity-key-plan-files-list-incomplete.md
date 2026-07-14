---
title: "A Plan's Per-Unit Files List Can Miss a Consumer When Extending an Identity Key"
date: 2026-07-14
category: docs/solutions/design-patterns/
module: form-event-registration
problem_type: design_pattern
component: tooling
severity: high
applies_when:
  - "Extending an existing identity/lookup key with a new dimension (e.g. entity+form+event -> entity+form+event+attribute)"
  - "Adding a new discriminator parameter to a deterministic-ID or matching function (e.g. ForHandler, FindEvent, GetHandlers/SetHandlers)"
  - "A plan's Implementation Units declare per-unit Files: lists used to scope what that unit touches"
  - "Confirming a unit's file list is complete before implementing it, not just before shipping it"
tags:
  - form-event
  - identity-key
  - onchange
  - deterministic-id
  - form-event-registration
  - plan-files-list
  - grep-callers
  - regression-test
related_components:
  - FormEventPlanner
  - FormEventExecutor
  - FormEventRenameAdvisor
  - FormXmlEventSerializer
  - FormEventDeterministicId
---

# A Plan's Per-Unit Files List Can Miss a Consumer When Extending an Identity Key

## Context

The onchange-annotation plan (`docs/plans/2026-07-14-001-feat-form-event-onchange-annotation-plan.md`) extended the existing form-event identity key with a new dimension: `FormXmlEventSerializer.GetHandlers`/`SetHandlers`/`FindEvent` and `FormEventDeterministicId.ForHandler` were keyed by `(entity, form, event)` only. The plan's design added a new `OnChange` member to `FormEventType` (`src/Flowline.Core/Models/FormEventModels.cs:8-10`, alongside the existing `OnLoad`/`OnSave`) and extended that key to `(entity, form, event, attribute)`, because unlike onLoad/onSave — one `<event>` element per form — onchange handlers are scoped per-attribute, and a form can have several.

The plan's Implementation Units each declared an explicit `Files:` list. Before implementing Unit 4 (FormEventPlanner), an `advisor()` consultation over the plan surfaced a question the plan's own Files list didn't raise: `FormEventExecutor.BuildFormXml` also calls the same serializer methods a second time, independently of the planner, to actually mutate and write back the XML (`GetHandlers(xdoc, formPlan.Event, formPlan.Attribute)` at `src/Flowline.Core/Services/FormEventExecutor.cs:387` and `SetHandlers(xdoc, formPlan.Event, desiredSet, formPlan.Attribute)` at `:409` — both now correctly threading the attribute through, per the fix below). The plan's Files lists for U1–U5 did not name `FormEventExecutor.cs`. Had the unit shipped exactly as scoped, the executor would have kept calling the old two-argument overload — for an `OnChange` event it would always resolve the *first* `<event name="onchange">` element on the form, regardless of which attribute a given plan entry actually targets. Two different onchange attributes on the same form (e.g. `creditlimit` and `revenue`) would read and write the *same* XML element, each push silently clobbering the other's handlers.

A grep sweep for every caller of these methods across `src/` confirmed the gap: exactly three files call them — `FormEventPlanner.cs`, `FormEventExecutor.cs`, and `FormEventRenameAdvisor.cs`. The plan's Files lists across all its units named only the first and third.

## Guidance

When an implementation unit extends an identity key, a deterministic-ID function, or a matching/lookup signature with a new dimension — adding a parameter, widening a tuple, adding an enum case that changes what "the same thing" means — do not trust the plan's stated `Files:` list as a complete enumeration of what needs to change. Treat it as a starting hint.

Before implementing that unit, grep the whole source tree for every caller of the affected method(s)/type(s) and cross-check the result against the plan's Files list. The concrete pattern used here:

```
grep -rn "FormXmlEventSerializer\.\(GetHandlers\|SetHandlers\)\|FormEventDeterministicId\.ForHandler" src/
```

This turns up every call site regardless of which unit's Files list mentions it. Any hit in a file the plan didn't scope for this unit is a signal to stop and confirm — either the unit's scope needs to grow, or there's a documented reason that caller doesn't need the new dimension (e.g. it deliberately still operates on the pre-extension key because the new case can't reach it). Do this grep sweep specifically at the point a plan is about to touch a shared key/signature, not just once at the start of the whole feature — a plan can be internally consistent about the units it does enumerate and still miss a caller because no unit's author was looking at that particular file when scoping.

## Why This Matters

A missed caller of an extended identity key is not a compile error. The unmodified caller still type-checks and still runs — it just keeps using the narrower, pre-extension key, silently. Here that would have meant `FormEventExecutor` locating handlers by `(entity, form, event)` while `FormEventPlanner` computed its diff by `(entity, form, event, attribute)`: the two would agree on single-attribute forms (only one onchange element exists, so "first match" happens to be the right one) and disagree exactly when a form has more than one onchange attribute registered — silently overwriting one attribute's handlers with another's on every push. A test suite that only ever exercises one onchange attribute per form — which is the natural first case anyone writes — would pass cleanly against this bug. It would only surface once a real customer form actually used two onchange attributes, at which point it's a live data-integrity incident against a production Dataverse org, not a caught regression.

## When to Apply

- Any time a plan or implementation unit extends an existing identity key, deterministic-ID function, or matching/lookup signature by adding a new parameter, widening a tuple, or introducing a case that changes what counts as "the same entity" for lookup/diff/mutation purposes.
- Not specific to form events, and not specific to this codebase — it applies wherever multiple independent code paths (a planner computing a diff, an executor applying it, an advisor cross-checking it) each call the same keyed lookup separately rather than sharing one computed result. The more call sites a key has, the more places a promotion can silently go stale in.

## Examples

Before: `FormXmlEventSerializer.GetHandlers(XDocument form, FormEventType evt)` and `SetHandlers(XDocument form, FormEventType evt, IReadOnlySet<FormEventHandler> desired)`, keyed by `(entity, form, event)` only — sufficient while `FormEventType` was just `OnLoad`/`OnSave`, one `<event>` element per form per type.

After (landed): both gained an optional `string? attribute = null` parameter, as did `FindEvent` and `FormEventDeterministicId.ForHandler`. `FormEventFormPlan` (`FormEventModels.cs:79-87`) gained a matching nullable `Attribute` field — null for OnLoad/OnSave, non-null for OnChange, following this codebase's existing "nullable field, not a subtype" convention for the same reason. The fix for the executor gap this doc describes was threading `formPlan.Attribute` into both call sites inside `FormEventExecutor.BuildFormXml` (`FormEventExecutor.cs:387,409`) — the exact caller the advisor consultation flagged as missing from the plan's Files list.

The regression test written to prove this — `ExecuteAsync_FormWithTwoOnChangeAttributePlans_MergedIntoSingleUpdateNeitherAttributeClobbered` (`tests/Flowline.Core.Tests/FormEventExecutorTests.cs:106`) — constructs two onchange `FormEventFormPlan` entries for different attributes ("creditlimit" and "revenue") sharing one `FormId`, pushes them through a real `ExecuteAsync`, and checks the captured formxml via `GetHandlers(xdoc, FormEventType.OnChange, "creditlimit")` and `..., "revenue")` both returning the correct, independent handler. The feature was also verified end-to-end against a live Dataverse environment (registering, idempotent re-push, and clean removal on annotation removal), confirming the fix holds outside the unit-test harness too.

## Related

- `docs/solutions/design-patterns/promoting-field-to-identity-key-changes-edit-semantics.md` — the same failure class, documented from an earlier, unrelated identity-key extension (`PluginPlanner`'s step-matching tuple) that bit a prior refactor via two independently-caught bugs. That doc's own review checklist named this exact area's signatures (`FindEvent`/`ForHandler`/`GetHandlers`/`SetHandlers`) as ones a future reviewer should grep for. This doc records that predicted recurrence, on the same key, extended a second time — the difference being it was caught here by a proactive advisor consultation plus a caller grep sweep before implementation, rather than by an independent reviewer after the fact.
- `docs/solutions/architecture-patterns/form-event-registration-rename-resilience.md` — same subsystem and same `FormEventDeterministicId.ForHandler`/`FormXmlEventSerializer.GetHandlers` mechanism, covering a different problem (rename-detection advisory UX). Its quoted `ForHandler` call site now needs the new `attribute` argument this doc introduces — flagged as a refresh candidate.
- `docs/solutions/logic-errors/secondary-match-predicate-missing-mode.md` — a near-identical generalizable discipline ("grep for every validator or key-field call in the original path and verify each appears in the new path too"), from an unrelated mechanism (`PluginPlanner` step-matching parity).
