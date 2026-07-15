---
title: Form Event Tab/IFRAME Events & Pipeline Rules - Plan
type: feat
date: 2026-07-15
topic: form-event-tab-iframe-pipeline
artifact_contract: ce-unified-plan/v1
artifact_readiness: requirements-only
product_contract_source: ce-brainstorm
execution: code
---

# Form Event Tab/IFRAME Events & Pipeline Rules - Plan

## Goal Capsule

- **Objective:** Close the FormXml-settable event gap (Tab `TabStateChange`, IFRAME `OnReadyStateComplete`), add bulk-edit-form opt-in for `onload`, and enforce the Form Event Pipeline's ordering and 50-handler-per-event rules.
- **Product authority:** This document.
- **Open blockers:** none — remaining unknowns are research tasks for planning, not product decisions (see Outstanding Questions).

---

## Product Contract

### Summary

Extend Flowline's form-event annotation system to cover the two remaining FormXml-settable events — Tab `TabStateChange` and IFRAME `OnReadyStateComplete` — scoped by control name the same way `onchange` is scoped by attribute. Add a `[bulkEdit]` annotation modifier so an `onload` handler can opt into running during Dataverse's bulk-edit form. Enforce the Form Event Pipeline's ordering and 50-handler-per-event rules that today go unchecked.

| Element | Event | Scope token | Status |
|---|---|---|---|
| Form | OnLoad | none | Existing |
| Form | OnSave | none | Existing |
| Column | OnChange | attribute | Existing |
| Tab | TabStateChange | tab name | New (this plan) |
| IFRAME | OnReadyStateComplete | control name | New (this plan) |

### Problem Frame

FormXml can represent five events through the Maker Portal's Event Handlers UI; Flowline's annotation system currently covers three (`OnLoad`, `OnSave`, `OnChange`). Anyone wiring a Tab or IFRAME event handler today has to hand-edit FormXml or use the Maker Portal directly, outside Flowline's tracked-annotation model.

Separately, two rules from Microsoft's Form Event Pipeline docs go unenforced today: handler registration order is undefined (`FormXmlEventSerializer.SetHandlers` writes from a `HashSet`, not the order annotations were read), and Dataverse's 50-handler-per-event cap is never checked before a push. These gaps exist for the three events Flowline already supports, not just the two being added.

Order only matters when one event/scope carries more than one Flowline-managed handler — e.g. two `flowline:onload` annotations for the same form (whether split across two lines in one file or one line each in two different files), or two `flowline:onchange` annotations on the same attribute. Dataverse invokes handlers sequentially in the FormXml list order; a handler that depends on state an earlier one set — via `executionContext.getSharedVariable`/`setSharedVariable`, or a side effect like a default value the next handler reads — needs to run after it. Today that order is accidental (`HashSet` enumeration), not something a developer can rely on or control by how they arrange their annotations. A form with a single handler per event, the common case, has nothing to order.

### Key Decisions

- **Directive names mirror the event name, lowercased** (`tabstatechange`, `onreadystatecomplete`) — matches the existing `FormEventType.ToString().ToLowerInvariant()` derivation used by `onload`/`onsave`/`onchange`; no aliasing.
- **Tab and IFRAME events are scoped by a control-name token**, positioned the same place `onchange`'s `<attribute>` token sits — one `<event>` element per control, mirroring the per-attribute `onchange` model.
- **`[bulkEdit]` is `onload`-only.** Dataverse only honors `BehaviorInBulkEditForm` on the form's `onload` event; the annotation parser rejects the modifier anywhere else.
- **Conflicting `[bulkEdit]` declarations resolve by OR.** If any `onload` annotation for a form specifies `[bulkEdit]`, the form's onload event is bulk-edit-enabled — consistent with how Flowline already unions handlers and libraries across annotation sources.
- **Handler order defaults to annotation-encounter order**, replacing today's arbitrary `HashSet`-derived order — sufficient on its own within a single file.
- **Cross-file ordering is explicit, not positional.** An optional `[order:N]` modifier lets a developer sequence handlers that share an event/scope regardless of which file declares them, mirroring the Maker Portal's own reorderable Event Handlers list rather than relying on file-system enumeration. Handlers without the modifier keep default encounter order and sort after every explicitly-ordered one.
- **Exceeding 50 handlers on one event hard-fails the push** via `FlowlineException`, before any Dataverse write — same validation-failure pattern as the existing function-not-found check.

### Requirements

**Tab & IFRAME events**

- R1. Flowline recognizes `flowline:tabstatechange` and `flowline:onreadystatecomplete` annotations, supporting the same `//`, `//!`, and `/*! */` comment forms as the existing directives.
- R2. Each directive requires a control-name token (tab name for `tabstatechange`, IFRAME control name for `onreadystatecomplete`) in the same position `onchange`'s attribute token occupies.
- R3. The default function name follows the existing `on<X>...`-style convention (e.g. `on<TabName>StateChange`, `on<IframeName>ReadyStateComplete`); unlike `onchange`, no publisher-prefix stripping applies, since tab and IFRAME control names are maker-assigned form-design names, not Dataverse schema attribute names.
- R4. Orphan cleanup, unrecognized-handler detection, and foreign-handler pass-through apply to Tab and IFRAME events the same way they already apply to `onload`/`onsave`/`onchange`.
- R5. Multiple tabs or IFRAME controls on one form register independently, one `<event>` element per control.

**Bulk edit support**

Bulk edit form, for reference: a Dataverse feature where a user selects multiple rows in a grid/view and clicks **Edit** on the command bar, opening a scaled-down "Edit (N) records" form (timeline wall, quick view forms, and reference panels stripped out). A saved change applies to every selected record in one action; the feature requires the `prvBulkEdit` privilege. Event handlers — onload, onsave, onchange, business rules — don't fire by default in this mode, since a script written for one record's context could misbehave when applied simultaneously to N different underlying records. `BehaviorInBulkEditForm="Enabled"` is the per-event opt-in for a handler that's safe to run there anyway (e.g. one that only sets field visibility or defaults from values that don't vary by record). Source: [Edit multiple rows (Bulk edit)](https://learn.microsoft.com/power-apps/user/edit-rows).

`BehaviorInBulkEditForm` is scoped to the `<event name="onload">` element, not to an individual `<Handler>`. A form has exactly one such element regardless of how many `onload` handlers register against it (unlike `onchange`, which gets one `<event>` per attribute) — every handler from every file lands as a `<Handler>` child inside the same shared `<Handlers>` collection:

```xml
<event name="onload" application="true" active="true" BehaviorInBulkEditForm="Enabled">
  <Handlers>
    <Handler functionName="InitDefaults" .../>   <!-- from library A -->
    <Handler functionName="ValidateOnLoad" .../> <!-- from library B -->
  </Handlers>
</event>
```

One flag, whole event — which is exactly why two annotations can disagree: library A's `onload` handler may not need bulk-edit while library B's does, and there's only one `BehaviorInBulkEditForm` slot to write for the form. Confirmed against `src/Flowline.Core/Services/FormXmlEventSerializer.cs:151-159` (`FindEvent` matches `onload` by `name="onload"` alone, no per-handler split) and `FormXmlEventSerializer.cs:74-111` (`SetHandlers` writes every handler for an event into that one shared `<Handlers>` collection).

- R6. An `onload` annotation may carry an optional `[bulkEdit]` modifier that sets `BehaviorInBulkEditForm="Enabled"` on the form's `<event name="onload">` element.
- R7. A push fails with a clear error if `[bulkEdit]` appears on any directive other than `flowline:onload`.
- R8. When a form's `onload` annotations disagree on `[bulkEdit]` (some set it, some don't), the form's onload event is marked bulk-edit-enabled if at least one does; if none do (including after a previously-set one is removed), the attribute is cleared.

**Pipeline rules**

- R9. Handlers for the same (entity, form, event, scope) register in the order their annotations were encountered by default (file line order; current enumeration order across files) — the only behavior needed when a single handler targets that event/scope.
- R10. Any annotation may carry an optional `[order:N]` modifier. When 2+ annotations share an event/scope and at least one specifies `[order:N]`, explicitly-ordered handlers sequence by ascending `N` first; unordered handlers keep their default encounter order and are appended after.
- R11. Two annotations sharing the same event/scope with the same `[order:N]` value fail the push with a clear error naming the conflicting annotations.
- R12. A push fails with a `FlowlineException`, before touching Dataverse, if any single event's final handler count — the full set that will be written, including foreign and unrecognized handlers passed through untouched — would exceed 50, not just the count of handlers Flowline itself is adding.

### Acceptance Examples

- AE1. Given an `onsave` annotation carrying `[bulkEdit]`, when pushed, then the push fails naming the offending annotation. **Covers R7.**
- AE2. Given two `onload` annotations for the same form in different files, one with `[bulkEdit]` and one without, when pushed, then the form's onload event has `BehaviorInBulkEditForm="Enabled"`. **Covers R8.**
- AE3. Given three `onload` annotations for the same form with no `[order:N]` modifiers, when pushed, then the resulting `<Handlers>` element lists them in the order their annotations were encountered. **Covers R9.**
- AE4. Given two `onload` annotations for the same form in different files, tagged `[order:2]` and `[order:1]` respectively, when pushed, then the `[order:1]` handler is listed first regardless of file. **Covers R10.**
- AE5. Given two `onload` annotations for the same form both tagged `[order:1]`, when pushed, then the push fails naming both conflicting annotations. **Covers R11.**
- AE6. Given a form's `onchange` event for one attribute already has 49 Flowline-managed handlers and a new annotation would add a 50th, when pushed, then it succeeds; a 51st fails before any Dataverse write. **Covers R12.**

### Scope Boundaries

- Grid/subgrid events, lookup/kbsearch events, process events, control `OnOutputChange`, form `Loaded`, and form-data `OnLoad` stay out of scope — none are FormXml-settable; supporting them needs JS-side `add*` wiring, a different mechanism than annotation-driven FormXml writes.
- `BehaviorInBulkEditForm` support is limited to `OnLoad` — Dataverse doesn't honor it on any other event today.
- Rename resilience for tabs and IFRAME controls (mirroring the form-level self-tag/rename-cache/sole-survivor advisor in `FormEventRenameAdvisor`) is deferred — a renamed tab or IFRAME control behaves like today's plain not-found case, no advisory suggestion. That advisor's logic is built around whole-form identity, not sub-elements within a form's FormXml, so extending it is a separate, larger piece of work to scope later if it proves necessary.

### Outstanding Questions

**Deferred to Planning**

- The exact live FormXml attribute Dataverse uses to scope Tab and IFRAME `<event>` elements is unverified — assumed to mirror `onchange`'s `attribute="logicalname"` shape, but needs a live Dataverse check (Maker Portal event configuration + formxml diff), the same method used to confirm `onchange`'s shape.

### Sources / Research

- Microsoft Learn — [Events in forms and grids in model-driven apps](https://learn.microsoft.com/power-apps/developer/model-driven-apps/clientapi/events-forms-grids): bulk-edit-form behavior, Form Event Pipeline (order, 50-handler cap), code-only vs. FormXml-settable event table.
- Microsoft Learn — [Configure model-driven app form event handlers](https://learn.microsoft.com/power-apps/maker/model-driven-apps/configure-event-handlers-legacy): the definitive FormXml/UI-settable event list (Form OnLoad/OnSave, Tab TabStateChange, Column OnChange, IFRAME OnReadyStateComplete).
- Microsoft Learn — [Edit multiple rows (Bulk edit)](https://learn.microsoft.com/power-apps/user/edit-rows): how bulk edit forms work end-to-end (grid selection, the scaled-down record dialog, the `prvBulkEdit` privilege).
- `src/Flowline.Core/Models/FormEventModels.cs:6-11` — current `FormEventType` enum (`OnLoad`, `OnSave`, `OnChange` only).
- `src/Flowline.Core/Services/FormXmlEventSerializer.cs:74-111` — `SetHandlers` writes from an `IReadOnlySet<FormEventHandler>` with no ordering guarantee; the `onchange` attribute-scoping precedent this plan extends to Tab/IFRAME.
- `src/Flowline.Core/Services/FormXmlEventSerializer.cs:151-159` — `FindEvent` matches `onload` by `name="onload"` alone (no per-handler split), confirming `BehaviorInBulkEditForm` is shared across every `onload` handler on a form.
- `src/Flowline.Core/Services/FormEventPlanner.cs` — no existing 50-handler validation; the per-event `planningKeys` loop is the extension point for Tab/IFRAME scope enumeration.
- `src/Flowline.Core/Services/FormEventAnnotationParser.cs:18-31` — existing annotation grammar (`OnLoadSaveAnnotationRegex`, `OnChangeAnnotationRegex`) this plan's new directives extend.
- `docs/plans/2026-07-14-001-feat-form-event-onchange-annotation-plan.md` — prior plan that added `onchange`'s attribute scoping and established the live-verification precedent this plan's Outstanding Questions follow.
