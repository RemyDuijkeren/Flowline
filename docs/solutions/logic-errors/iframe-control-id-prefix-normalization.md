---
title: Normalize IFRAME control id across every lookup, hash, and union site — not just the primary match
date: 2026-07-15
category: docs/solutions/logic-errors/
module: form-event-registration
problem_type: logic_error
component: tooling
symptoms:
  - "IFRAME OnReadyStateComplete annotations written with the bare control id (matching what Maker Portal's UI actually shows and lets a maker type, e.g. myFrame) failed to resolve, because the FormXml control's id attribute carries a system-generated IFRAME_ prefix Flowline required the annotation to repeat verbatim"
  - "The planner's union of live-FormXml-scan control ids and annotation-supplied control ids treated the prefixed and bare spelling of the same control as two distinct scopes, computing and writing that control's events element twice, the second write silently clobbering the first"
  - "Rename-detection self-tag matching computed a different deterministic handler hash depending on whether an annotation used the prefixed or bare spelling of the same control, breaking self-tag matches for prefixed-form annotations"
  - "Default function-name derivation produced a different auto-generated name for the same real control depending on which spelling the annotation happened to use"
root_cause: logic_error
resolution_type: code_fix
severity: medium
related_components:
  - FormXmlEventSerializer
  - FormEventPlanner
  - FormEventRenameAdvisor
  - FormEventDeterministicId
  - FormEventAnnotationParser
tags:
  - iframe
  - dataverse
  - form-events
  - normalization
  - maker-portal
  - identity-key
  - onreadystatecomplete
---

# Normalize IFRAME control id across every lookup, hash, and union site — not just the primary match

## Problem

Flowline's IFRAME `OnReadyStateComplete` form-event annotation carries a `<controlname>` token matched against the FormXml `<control id="...">` attribute (`src/Flowline.Core/Services/FormEventAnnotationParser.cs:50-53`). The original match required that token to equal the FormXml `id` byte-for-byte — including Dataverse Maker Portal's system-assigned `IFRAME_` prefix, so an annotation had to spell out `IFRAME_myFrame` rather than the `myFrame` a maker actually types. A Maker Portal screenshot of the IFRAME control's Name field showed why that contract was wrong: Maker Portal renders `IFRAME_` as a fixed, greyed-out, non-editable prefix segment in the control's Name field — the maker only ever types the suffix. Requiring the annotation to repeat a prefix the user never typed, and can't see as separate from the control's name, is friction that doesn't match the product surface the annotation is describing.

Left unfixed, a user writing the annotation the way Maker Portal actually presents the name (bare suffix) would get a plan-time "no IFRAME control found" error even though the control plainly exists on the form.

## Symptoms

- An `onreadystatecomplete` annotation using the bare control id (as shown in Maker Portal) fails plan-time validation with "no IFRAME control '\<id\>' found in form XML," even though the control exists.
- The same physical IFRAME control's `<events>` element gets computed and written twice in one push when the live FormXml scan and an annotation disagree on prefixed-vs-bare spelling — the second write silently clobbers the first.
- Rename-detection self-tag matching silently fails to recognize a handler it wrote itself, for any annotation using the prefixed spelling.
- The same control's default (unnamed) function-name derivation changes depending on which spelling — prefixed or bare — the annotation happens to use.

## What Didn't Work

The obvious fix is "strip the prefix at the one place the mismatch shows up" — `FindIframeCell`, the helper in `FormXmlEventSerializer.cs` that resolves a control id to its containing `<cell>` (used by both `GetOrAddEventsContainer` for writes and `FindEvent` for reads; `FormXmlEventSerializer.cs:255-263` and its callers at `:213-214` and `:288`). That fix alone is incomplete, because the same raw control-id string is independently consumed at four sites that all have to agree on one canonical spelling, or the same physical IFRAME control silently becomes two different identities:

1. **Live-FormXml orphan scan** — `GetIframeControlIdsWithReadyStateHandlers` (`FormXmlEventSerializer.cs:90-100`) enumerates every IFRAME control currently carrying a ready-state handler directly off the FormXml `id` attribute. `FormEventPlanner.cs:91-92` unions this scan's result with annotation-supplied control ids into one `HashSet<string>` of scopes to plan for (`readyStateScopes`, built at `FormEventPlanner.cs:91-104`). If the scan returns the prefixed spelling while an annotation uses the bare spelling (or vice versa), the union treats them as two separate scopes for the same real control — its `<events>` element gets computed and written twice, one write clobbering the other.
2. **Default function-name derivation** — `FormEventPlanner.DeriveDefaultFunctionName`'s `OnReadyStateComplete` branch (`FormEventPlanner.cs:450-460`) builds `"on" + PascalCase(controlId) + "ReadyStateComplete"` when an annotation omits an explicit function name. Without normalizing the token first, the same control produces a different default function name depending on which spelling the annotation happened to use — `onMyFrameReadyStateComplete` (bare) vs `onIFRAMEMyFrameReadyStateComplete` (prefixed — `ToPascalCase` splits on the underscore and capitalizes only each word's first character, so `IFRAME_myFrame` becomes `IFRAMEMyFrame`, not a cleanly cased `IframeMyFrame`).
3. **Deterministic handler-id hashing** — `FormEventDeterministicId.ForHandler` (`FormEventModels.cs:61-64`) folds the `attribute` token (the control id, for this event type) into the hash Flowline uses to detect "is this handler already Flowline's, and unchanged?" Two spellings of the same control hash to two different GUIDs, so Flowline concludes a handler vanished and a brand-new one appeared, even though nothing changed on the form.
4. **Rename-advisor self-tag matching** — `FormEventRenameAdvisor.FindSelfTagMatch` recomputes that same deterministic hash (`FormEventRenameAdvisor.cs:74-77`) to recognize "this form was renamed, but I wrote this exact handler." It must normalize the annotation's control-id token identically to how the planner normalized it when the handler was first written, or self-tag matching silently fails for any annotation using the prefixed spelling.

Patching only #1 would have left #2-4 as latent inconsistency bugs, each surfacing later as a separate, confusing symptom ("handler suddenly looks new," "rename detection didn't fire") with no obvious link back to a control-id spelling difference.

## Solution

One canonical normalization helper, applied at every site that consumes the raw control-id token — not just the lookup:

```csharp
// src/Flowline.Core/Services/FormXmlEventSerializer.cs:23-28
const string IframeControlIdPrefix = "IFRAME_";

public static string NormalizeIframeControlId(string controlId) =>
    controlId.StartsWith(IframeControlIdPrefix, StringComparison.OrdinalIgnoreCase)
        ? controlId[IframeControlIdPrefix.Length..]
        : controlId;
```

Applied at:
- `FindIframeCell` (`FormXmlEventSerializer.cs:255-263`) — normalizes both the queried id and each candidate `<control id="...">` before comparing, so `myFrame`, `IFRAME_myFrame`, and `iframe_myFrame` all resolve to the same control.
- `GetIframeControlIdsWithReadyStateHandlers` (`FormXmlEventSerializer.cs:96-100`) — `.Select(NormalizeIframeControlId)` before building the result `HashSet`, so the live-scan side of the union always returns the bare form.
- `FormEventPlanner`'s `readyStateScopes` union (`FormEventPlanner.cs:101`) and its per-scope annotation filter (`FormEventPlanner.cs:120-121`) — normalizes the annotation's control id before adding to the union and before comparing against a scope key, so an annotation written either way collapses onto the scan's scope.
- `DeriveDefaultFunctionName`'s `OnReadyStateComplete` branch (`FormEventPlanner.cs:458`) — normalizes before deriving the function name, so the default name is stable regardless of which spelling was written.
- `FormEventRenameAdvisor.FindSelfTagMatch` (`FormEventRenameAdvisor.cs:74-76`) — normalizes the annotation's control id the same way before recomputing the expected deterministic hash, matching the convention the planner used when it first wrote the handler.

**Regression surfaced mid-fix, and its fix:** `FormEventPlannerTests.BuildSnapshot` (`tests/Flowline.Core.Tests/FormEventPlannerTests.cs:32-34`) sweeps `FormXmlEventSerializer.GetHandlers(xdoc, evt)` across every `FormEventType` (including `OnReadyStateComplete`) with the default `attribute: null`, purely to infer which web-resource libraries are already referenced on a form — no real IFRAME scope is in play in that sweep. The old code path was implicitly null-tolerant: every comparison went through `string.Equals(controlId, ...)`, and `string.Equals` with a null argument just returns `false`, never throws. `NormalizeIframeControlId`'s `controlId.StartsWith(...)` call is not null-tolerant, so it threw `NullReferenceException` the moment `controlId` was null — breaking the library-inference sweep and any other caller that passes `attribute: null` through the `OnReadyStateComplete`-aware code path. Fixed with an explicit guard ahead of the normalization call:

```csharp
// FormXmlEventSerializer.cs:255-258
static XElement? FindIframeCell(XElement root, string? controlId)
{
    if (controlId is null) return null;
    var normalized = NormalizeIframeControlId(controlId);
    // ...
}
```

The guard restores, explicitly, the null-tolerance the old `string.Equals`-based code had implicitly.

Verification: full suite green — 805 (`Flowline.Core.Tests`) + 698 (`Flowline.Tests`) tests passing, 0 failures, 3 pre-existing unrelated skips. Tests added/updated cover prefix-insensitive lookup (`myFrame` / `IFRAME_myFrame` / `iframe_myFrame` all resolve to the same control), the live-scan-returns-bare-form contract, a planner-level regression proving a prefixed-form annotation collapses onto the same scope as the live FormXml scan rather than producing two scopes, and the rename-advisor test's hand-computed hash updated to the new canonical (bare) convention.

## Why This Works

A canonical-normalization change is not "done" when the primary lookup works — it's done when every hash, compare, union, and name-derivation site that consumes the identifier has been updated to agree on the same canonical form. An identifier that flows into a `HashSet` union, a derived display/function name, and a deterministic content hash creates three more places for two spellings of the same real-world entity to silently diverge into two different identities — each divergence manifesting as an unrelated-looking symptom (duplicate writes, unstable naming, spurious "new handler" detections) rather than an obvious "the ids don't match" error. The fix is only complete when normalization sits at the boundary where the raw external token first enters the system, or is applied identically at every downstream consumption site — and null-tolerance conventions the old code had implicitly (comparisons that degrade gracefully on null) must be re-established explicitly once the new normalization function is not itself null-tolerant.

## Prevention

- Before declaring a normalization "done," grep for every consumption site of the raw token — lookups, set/union membership, derived-name generation, content/deterministic hashing, and any "same entity, different code path" detection (rename/self-tag matching). Each is a place two spellings can diverge into different identities.
- Add a round-trip test with both spellings (prefixed and bare) at the level that matters most — ideally an end-to-end planner test proving both spellings collapse onto the same scope/plan entry, not just a unit test on the normalization helper in isolation.
- If the normalization helper is not null-tolerant but an existing call path passes `null` for unrelated reasons (e.g., a generic sweep across event types), add an explicit guard rather than assuming the old implicit tolerance still holds.

## Related Issues

- `docs/solutions/design-patterns/extending-identity-key-plan-files-list-incomplete.md` — same module and overlapping file set (`FormEventPlanner`, `FormEventRenameAdvisor`, `FormXmlEventSerializer`), and the same underlying lesson shape: a shared keyed lookup/identity mechanism with multiple independent call sites, one of which was originally missed or left inconsistent.
- `docs/solutions/architecture-patterns/form-event-registration-rename-resilience.md` — documents `FormEventRenameAdvisor.FindSelfTagMatch`, the function this fix also modifies (to normalize the control-id token before hash recomputation). Its quoted `FindSelfTagMatch` code block is now stale against current source (function signature and line numbers have shifted); flagged for `ce-compound-refresh`.
- `docs/solutions/logic-errors/secondary-match-predicate-missing-mode.md` — same abstract lesson shape (a match predicate/identity key missing a discriminating dimension causes silent misidentification) in a different subsystem (plugin step matching, not form events).
