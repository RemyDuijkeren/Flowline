---
title: "feat: flowline:onchange annotation"
type: feat
date: 2026-07-14
artifact_contract: ce-unified-plan/v1
artifact_readiness: implementation-ready
execution: code
product_contract_source: ce-brainstorm
---

> **Product Contract preservation:** unchanged — all R-IDs carried forward verbatim. One planning-time assumption added: default name derivation for all-lowercase attribute names produces `onCreditlimitChange`, not `onCreditLimitChange`; developers should name explicitly for standard Dataverse attributes.

# feat: flowline:onchange Annotation - Plan

## Goal Capsule

- **Objective:** Close the one remaining form-event gap in Flowline by adding a `// flowline:onchange` annotation that wires attribute-level onChange handlers, so `push` discovers, registers, and keeps them in sync — just like it already does for `onload`/`onsave`.
- **Product authority:** Extends `docs/plans/2026-07-10-002-feat-form-event-registration-plan.md` to the attribute-level event path, using the same annotation model, discovery mechanism, and push contract.
- **Open blockers:** None. Both empirical questions from the requirements phase are resolved — see Empirical Findings.

---

## Problem Frame

`flowline push` registers and syncs `onload`/`onsave` handlers via annotation. Field-level `onchange` is the one remaining form-event type with no equivalent: developers still wire it by hand in the Maker Portal after every deploy.

---

## Syntax

```
// flowline:onchange <entity> <form> <attribute> [Function[(param1,param2,...)]]
```

Positional, extending the existing onload/onsave shape with one additional token.

**Examples:**

```js
// flowline:onchange account "Account Main" creditlimit
export function onCreditLimitChange(executionContext) { ... }  // matched case-insensitively; real casing registered

// flowline:onchange account "Account Main" new_credit_limit onCreditLimitChange

// flowline:onchange account "Account Main" creditlimit onCreditLimitChange(ctx)

// flowline:onchange account "Account Main" creditlimit MyNs.onCreditLimitChange
```

---

## Requirements

**Annotation syntax**

- R1. A JS file declares an onChange binding via `// flowline:onchange <entity> <form> <attribute> [Function[(params)]]`, mirroring the onload/onsave annotation family.
- R1a. Recognized in all three comment forms: `//`, `//!`, `/*! ... */`. Scanned across the whole built file, not just a leading block. Reuses `WebResourceAnnotationParser`'s existing approach.
- R2. `<entity>` is the table logical name.
- R3. `<form>` is the form's display name — bare when it contains no whitespace, single- or double-quoted when it does. Same quoting rules as onload/onsave (R3 in the parent plan).
- R4. `<attribute>` is the attribute's logical name (e.g., `creditlimit`, `new_creditlimit`). Always a bare token — attribute logical names never contain spaces.
- R5. `Function` is optional. When omitted, the default is derived from the attribute logical name using the convention `on<PascalCasedAttribute>Change`: strip the publisher prefix (everything up to and including the first `_`), convert each remaining underscore-separated segment to PascalCase using the existing `ToPascalCase` helper, join them, and prepend `on` and append `Change`. Examples: `creditlimit` → `onCreditlimitChange`; `new_creditlimit` → `onCreditlimitChange`; `new_credit_limit` → `onCreditLimitChange`; `cr507_risk_rating` → `onRiskRatingChange`. The derived name is a case-insensitive guess — matching (R6) is case-insensitive, so a developer who exports `onCreditLimitChange` from their file will have that exact casing registered in Dataverse even when the default guess is `onCreditlimitChange`. The default's casing only matters if no function in the file matches at all (hard fail per R12). This is the one supported convention — no dual-matching.
- R6. Function name matching (explicit or defaulted) is case-insensitive against the file's actual exported function names, in both namespaced and bare global forms. Registered `functionName` uses real casing from the file. Reuses `FormEventFunctionResolver` and R6/R6a/R7/R7a from the parent plan unchanged.

**Behavior**

- R7. Registers at the attribute level, not the control-instance level. The `<event name="onchange" attribute="fieldname">` element lives in the form-level `<events>` block (same block as onload/onsave), so a single entry covers all controls bound to that attribute regardless of how many times the field appears in the layout. Confirmed empirically: adding an onchange handler on the `name` attribute (which appears as two controls on the form) produces one `<event>` entry in the XML, not two.
- R8. Applies to Main (type=2) and Quick Create (type=7) forms only. Quick View forms are excluded (no JS library support).
- R9. `passExecutionContext="true"` and `enabled="true"` are always set. Neither is configurable via annotation in this version.
- R10. `formLibraries` registration follows the same rules as onload/onsave — the library must exist in Dataverse before the control-level registration runs (same R10a ordering constraint from the parent plan).

**Validation**

- R11. If `<entity>` or `<form>` does not resolve to an existing table/form that is a component of this project's solution, `push` fails with a clear error naming the declaration — same R8/R8a behavior as onload/onsave.
- R12. Function name resolution follows R7/R7a from the parent plan: hard fail when defaulted name not found; three-outcome resolution (found / confirmed absent / inconclusive) when explicit.

**Sync behavior**

- R13. Handler detection and orphan cleanup scan every solution-scoped form (not just annotation-targeted ones), same as R14/R15 in the parent plan. A handler orphaned by removing the last annotation for an attribute is found and removed on the next `push`. Orphan detection for onchange enumerates every `<event name="onchange" attribute="...">` element present in each form's `formxml`, not only the attributes currently referenced by annotations.
- R14. Deterministic ID derivation includes the attribute dimension: `entity | form | "onchange" | attribute | functionName | libraryName` (length-prefixed, same KTD1 approach from the parent plan). This key space is distinct from onload/onsave.
- R15. Ownership boundary: Flowline only manages `<Handler>` entries inside `<event name="onchange">` elements whose `libraryName` points to a tracked library. Handlers referencing other libraries are never touched.
- R16. Unrecognized handlers (non-matching deterministic ID on a tracked library) surface through the existing R18/R18a–R18c confirmation gate. The proposed annotation uses the `flowline:onchange` keyword with the resolved attribute name.
- R17. Publish follows the same entity-level `PublishXml` block as onload/onsave. A form with changed onchange handler entries is published in the same step.

**Rename resilience**

- R18. The form-name token in `flowline:onchange` benefits from the existing rename resilience infrastructure (`FormEventRenameAdvisor`, `FormEventIdentityCache`) via the same `(entity, formName)` cache key. No new cache dimension for attribute names — attribute logical names are immutable in Dataverse.
- R18a. The self-tag check in `FormEventRenameAdvisor` must be extended to scan onchange event elements (by passing `FormEventType.OnChange` and the annotation's attribute to `FormXmlEventSerializer.GetHandlers`) in addition to the current onload/onsave scan.

---

## Scope Boundaries

### Out of scope

- Section/tab targeting — all control instances for the attribute are always registered.
- Rename resilience for attribute logical names — they are immutable.
- Per-annotation configuration of `passExecutionContext` or `enabled`.
- Any event type beyond `onload`, `onsave`, and `onchange`.
- Tab/section/row-level events (no Maker Portal equivalent exists).

### Outside this product's identity

- General form layout or control placement.
- Changing which attributes a form exposes — Flowline manages event wiring only.

---

## Empirical Findings

Verified live against the AutomateValue Dev environment by registering onchange handlers via the Maker Portal on the "AutomateValue" Account Main form, then diffing `systemform.formxml` before and after.

- **XML location:** Onchange events are a direct child of `<form><events>` — the same top-level block as `onload`/`onsave`, **not** inside `<control><events>`. The parent plan's out-of-scope note ("nested inside `<control><events>`") was incorrect; the distinction is only the `attribute` attribute on the `<event>` element itself.
- **Event element shape:** `<event name="onchange" application="false" active="false" attribute="creditlimit">` — the field is specified via `attribute="<logicalname>"` on the `<event>` element.
- **Defaults:** `application="false"` and `active="false"` for onchange (contrast with `application="true" active="true"` for onload/onsave).
- **Handler shape:** Identical to onload/onsave: `functionName`, `libraryName`, `handlerUniqueId`, `enabled="true"`, `parameters`, `passExecutionContext="true"`. No differences.
- **Multi-control confirmed at XML level:** The `name` attribute appears as two separate `<control>` elements in the form layout, but registering an onchange handler produces exactly one `<event name="onchange" attribute="name">` in the top-level `<events>` block. Dataverse handles attribute-level scope, not control-instance scope.
- **`formLibraries`:** The library entry appears at the form level in `<formLibraries>`, same as for onload/onsave. No separate control-level library reference exists.
- **Verified example output:**

```xml
<event name="onchange" application="false" active="false" attribute="creditlimit">
  <Handlers>
    <Handler functionName="Example1.onCreditLimitChange"
             libraryName="av_Cr07982/example1.js"
             handlerUniqueId="{96efc294-0531-454d-8a33-46edabf3cf52}"
             enabled="true"
             parameters=""
             passExecutionContext="true" />
  </Handlers>
</event>
```

---

## Planning Contract

### Key Technical Decisions

**KTD1 — `FormEventAnnotation` gets a nullable `string? Attribute` field, not a separate subtype.**
`FormEventAnnotation` (`FormEventModels.cs:12`) is a positional record with all nullable optionals. Adding `string? Attribute` (null for `OnLoad`/`OnSave`, required for `OnChange`) follows the same pattern as `FunctionName` and `Parameters`. A separate `OnChangeFormEventAnnotation` subtype would fork every downstream pipeline (reader, planner, executor, advisor) without adding safety — `OnChange` is always accompanied by a non-null attribute, enforced by the parser, and downstream code checks `Event == OnChange` before reading `Attribute`.

**KTD2 — `FormEventType.OnChange` causes `EventName(evt)` to produce `"onchange"`, which is the correct XML `name` attribute.** The existing helper `EventName(FormEventType evt) => evt.ToString().ToLowerInvariant()` (`FormXmlEventSerializer.cs:130`) requires no change — `OnChange.ToString()` = `"OnChange"`, lowercased = `"onchange"`.

**KTD3 — `FormXmlEventSerializer.GetHandlers` and `SetHandlers` gain an optional `string? attribute` parameter.** For `OnLoad`/`OnSave`, `attribute` is null and the existing `FindEvent` logic is unchanged (find `<event name="onload|onsave">`). For `OnChange`, `FindEvent` must additionally match the `attribute` XML attribute: `<event name="onchange" attribute="creditlimit">`. When creating a new onchange `<event>` node, use `application="false" active="false"` (empirically verified) and set `attribute="..."`. A new `GetOnChangeAttributes(XDocument form)` method enumerates the set of attribute logical names that currently have `<event name="onchange">` elements — needed by the planner for orphan detection.

**KTD4 — Orphan detection for onchange enumerates the XML, not only the annotation set.** For `OnLoad`/`OnSave`, the planner's orphan loop iterates `[OnLoad, OnSave]` — a fixed two-element set. For `OnChange`, the "current" set is the union of (a) all `<event name="onchange" attribute="...">` elements found in each form's `formxml` (via `GetOnChangeAttributes`) and (b) all attributes referenced by current annotations. The planner must iterate this union per form, not just the enum values. This matches R13's "scan every solution-scoped form" requirement.

**KTD5 — `FormEventDeterministicId.ForHandler` gains an optional `string? attribute` parameter.** When non-null (only for `OnChange`), it is injected into the key between the event name and `functionName`: `entity | form | "onchange" | attribute | functionName | libraryName`. Onload/onsave callers pass `null` and the key is unchanged. The length-prefix encoding (KTD1 in the parent plan) still applies to each non-null part.

**KTD6 — Default function name derivation reuses `ToPascalCase` and a new `StripPublisherPrefix` helper.** `StripPublisherPrefix(string name)` returns the substring after the first `_` if one exists; otherwise returns the whole name unchanged. `ToPascalCase` is already `internal static` in `FormEventPlanner` and reused by `FormEventRenameAdvisor`. For `OnChange`, `DeriveHandlerResolutionInputs` adds a third branch: when `FunctionName` is null and `Event == OnChange`, derive as `"on" + ToPascalCase(StripPublisherPrefix(attribute!)) + "Change"`. `DeriveHandlerResolutionInputs` receives the `Attribute` value from `ResolvedFormEventAnnotation`.

**KTD7 — Two separate regex patterns in `FormEventAnnotationParser` (not one unified pattern).** The onchange annotation has four mandatory tokens (entity, form, attribute) plus one optional (function), while onload/onsave have three mandatory plus one optional. A unified regex with conditional attribute capture would be hard to read and maintain. Two compile-time regexes — `OnLoadSaveAnnotationRegex` (existing, renamed) and `OnChangeAnnotationRegex` (new) — are cleaner. The intent regex (malformed-detection) is updated to also match `onchange`.

---

## Assumptions

- Default function name for all-lowercase attribute names (e.g., `creditlimit`) produces `onCreditlimitChange` as the intermediate guess, but this is a non-issue: matching is case-insensitive (R6), so a file exporting `onCreditLimitChange` matches and that real casing is what gets registered in Dataverse. The guess's own casing only matters if no function in the file matches at all.
- `FormEventReader` requires no changes: it already loads both local annotations and solution-scoped form formxml; the new `OnChange` enum value propagates through the existing snapshot without structural changes to the reader.
- The three-phase push sequencing (KTD12 in the parent plan) applies unchanged: onchange handlers participate in Phase 1 (cleanup) and Phase 3 (registration) alongside onload/onsave handlers with no separate sequencing.

---

## Implementation Units

### U1. Add `OnChange` to `FormEventType`, `Attribute` to `FormEventAnnotation`, and attribute dimension to `FormEventDeterministicId`

**Goal:** Extend the model layer so `OnChange` is a first-class event type and the deterministic ID key includes the attribute dimension.

**Requirements:** R14 (key includes attribute), R1 (foundation for parser).

**Dependencies:** None.

**Files:**
- `src/Flowline.Core/Models/FormEventModels.cs`
- `tests/Flowline.Core.Tests/FormEventDeterministicIdTests.cs` (extend existing)

**Approach:**
- Add `OnChange` to `FormEventType` after `OnSave`.
- Add `string? Attribute` to `FormEventAnnotation`: `(string Entity, string Form, FormEventType Event, string? FunctionName, string? Parameters, string? Attribute)`. Position after `Parameters` — adds at the end, preserving existing deconstruction patterns at call sites that name the positional fields.
- In `FormEventDeterministicId.ForHandler`, accept `string? attribute = null`. When non-null, inject it between the `evt.ToString()` part and `functionName` in the length-prefixed key: `$"{evtPart.Length}:{evtPart}{attrPart}{fnPart}{libPart}"` where `attrPart` is empty string when `attribute` is null and `$"{attribute.Length}:{attribute.ToLowerInvariant()}"` when non-null.
- `ForLibrary` is unchanged.

**Patterns to follow:** Existing `FormEventDeterministicId.ForHandler` length-prefix pattern (`FormEventModels.cs:57–61`); existing nullable optionals in `FormEventAnnotation`.

**Test scenarios:**
- `OnChange` enum value round-trips through `EventName(evt)` → `"onchange"` (via `FormXmlEventSerializer`'s helper, which is implicitly tested in U3 — mention it here as a model-level smoke check).
- `ForHandler` with `OnChange` + non-null attribute produces a different GUID than the same inputs with `OnLoad` (key-space isolation).
- `ForHandler` with `OnChange` + `attribute="creditlimit"` vs `attribute="revenue"` (all else equal) → different GUIDs.
- `ForHandler` with `OnChange` + `attribute="Creditlimit"` (upper-cased) = `ForHandler` with `OnChange` + `attribute="creditlimit"` (case-insensitive key).
- `ForHandler` with `OnLoad` + `attribute=null` matches the pre-change output — backward compatibility.

**Verification:** Model file compiles; deterministic ID tests pass; no existing call site requires `attribute` since it defaults to `null`.

---

### U2. Extend `FormEventAnnotationParser` to parse `flowline:onchange`

**Goal:** Recognise `// flowline:onchange <entity> <form> <attribute> [Function[(params)]]` in all three comment forms and populate `FormEventAnnotation.Attribute`.

**Requirements:** R1, R1a, R2, R3, R4, R5 (parsing portion), R6.

**Dependencies:** U1.

**Files:**
- `src/Flowline.Core/Services/FormEventAnnotationParser.cs`
- `tests/Flowline.Core.Tests/FormEventAnnotationParserTests.cs`

**Approach:**
- Rename the existing `AnnotationRegex` to `OnLoadSaveAnnotationRegex` (no logic change, just renaming for clarity).
- Add `OnChangeAnnotationRegex` (new `Regex.Compiled`):
  ```
  ^(?://!?|/\*!)\s*flowline:onchange\s+(?<entity>\S+)\s+(?<form>"[^"]+"|'[^']+'|\S+)\s+(?<attribute>\S+)(?:\s+(?<function>[A-Za-z_][\w.]*)(?:\((?<params>[^)]*)\))?)?\s*(?:\*/)?$
  ```
  Note: `<attribute>` is mandatory (bare `\S+`); `<function>` and `<params>` remain optional.
- Update `AnnotationIntentRegex` to match `on(?:load|save|change)\b`.
- In `ParseAnnotations`, try `OnLoadSaveAnnotationRegex` first (existing path); if it matches, parse `event` as `"load"` or `"save"` → `FormEventType.OnLoad`/`OnSave`, `Attribute = null`. Then try `OnChangeAnnotationRegex`; if it matches, `Event = FormEventType.OnChange`, populate `Attribute` from the `<attribute>` capture group (no quote stripping — attribute names are never quoted), `FunctionName` and `Parameters` as before.
- Malformed-line detection: a line matching `AnnotationIntentRegex` but neither `OnLoadSaveAnnotationRegex` nor `OnChangeAnnotationRegex` is flagged in `MalformedLines` (same as existing behaviour).

**Patterns to follow:** Existing `OnLoadSaveAnnotationRegex` structure (`FormEventAnnotationParser.cs:18–20`); quote-stripping for form name (`FormEventAnnotationParser.cs:53`).

**Test scenarios:**
- `// flowline:onchange account "Account Main" creditlimit` → `Entity="account"`, `Form="Account Main"`, `Attribute="creditlimit"`, `Event=OnChange`, `FunctionName=null`, `Parameters=null`.
- `// flowline:onchange account 'Account Main' creditlimit` → same via single quotes.
- `// flowline:onchange account AccountMain creditlimit` → bare form name (no spaces) parsed correctly.
- `// flowline:onchange account "Account Main" new_credit_limit onCreditLimitChange` → `Attribute="new_credit_limit"`, `FunctionName="onCreditLimitChange"`.
- `// flowline:onchange account "Account Main" creditlimit onCreditlimitChange(ctx)` → `Parameters="ctx"`.
- `//! flowline:onchange account "Account Main" creditlimit` → recognized identically to `//`.
- `/*! flowline:onchange account "Account Main" creditlimit */` → recognized.
- Annotation on line 50 of a file with a banner comment on lines 1–4 — still parsed (whole-file scan).
- Two onchange annotations in the same file targeting different attributes — both returned, no interference.
- Existing `// flowline:onload account "Account Main"` in the same file — still parsed as `OnLoad` with `Attribute=null`.
- `// flowline:onchange account "Account Main"` (missing attribute token) → not matched by `OnChangeAnnotationRegex`; matched by `AnnotationIntentRegex` → flagged as malformed.
- Malformed `// flowline:onchange account` (only two tokens) → flagged as malformed.

**Verification:** All new and existing parser tests pass; `FormEventAnnotationParserTests.cs` extended with the scenarios above.

---

### U3. Extend `FormXmlEventSerializer` for onchange event elements

**Goal:** Enable reading and writing of `<event name="onchange" attribute="...">` elements, with correct `application="false" active="false"` defaults and attribute-keyed `FindEvent`.

**Requirements:** R7, R9, R13 (orphan enumeration), empirically verified XML shape.

**Dependencies:** U1.

**Files:**
- `src/Flowline.Core/Services/FormXmlEventSerializer.cs`
- `tests/Flowline.Core.Tests/FormXmlEventSerializerTests.cs`

**Approach:**
- Add `string? attribute = null` parameter to `GetHandlers(XDocument form, FormEventType evt, string? attribute = null)` and `SetHandlers(XDocument form, FormEventType evt, IReadOnlySet<FormEventHandler> desired, string? attribute = null)`.
- Update `FindEvent` (the private helper that locates or creates an `<event>` element) to match by both `name` and `attribute` when `attribute` is non-null: find `<event>` where `name == eventName` AND `attribute == attribute` (case-insensitive). When `attribute` is null (onload/onsave), the existing name-only match is unchanged.
- When `FindEvent` must create a new `<event>` node for onchange: set `application="false" active="false" attribute="<attrname>"` (empirically verified defaults — opposite of the `true/true` defaults for onload/onsave).
- Add `GetOnChangeAttributes(XDocument form) → IReadOnlySet<string>`: returns the set of `attribute` values from all `<event name="onchange">` elements in the top-level `<events>` block. Returns empty set if none exist. Used by the planner for orphan enumeration (KTD4).

**Patterns to follow:** Existing `FindEvent`, `GetOrAdd`, and `EventName` helpers (`FormXmlEventSerializer.cs:110–131`); existing attribute-access patterns on `XElement`.

**Test scenarios:**
- `GetHandlers(form, OnChange, "creditlimit")` on a form with no `<events>` → empty set, no exception.
- `GetHandlers(form, OnChange, "creditlimit")` on a form with an `<event name="onchange" attribute="creditlimit">` → returns the handler set correctly.
- `GetHandlers(form, OnChange, "creditlimit")` does not return handlers from `<event name="onchange" attribute="revenue">` (attribute isolation).
- `GetHandlers(form, OnLoad)` still works unchanged when `attribute` is omitted (backward compat).
- `SetHandlers(form, OnChange, desired, "creditlimit")` on a form with no `<events>` → creates `<events><event name="onchange" application="false" active="false" attribute="creditlimit"><Handlers>…` with correct attribute order.
- `SetHandlers(form, OnChange, desired, "creditlimit")` on a form that already has `<event name="onchange" attribute="revenue">` → only the `creditlimit` event element is modified; `revenue` event element is untouched.
- `SetHandlers(form, OnLoad, desired)` is unaffected by the new parameter — onload element attribute defaults remain `application="true" active="true"`.
- `GetOnChangeAttributes` on a form with two onchange event elements → returns both attribute names.
- `GetOnChangeAttributes` on a form with no onchange events → returns empty set.
- Round-trip: `SetHandlers(OnChange, desired, "creditlimit")` followed by `GetHandlers(OnChange, "creditlimit")` returns the original desired set (matches the existing round-trip test pattern in `FormXmlEventSerializerTests.cs:242–278`).
- Handler XML attribute order for onchange matches the empirically verified shape: `functionName`, `libraryName`, `handlerUniqueId`, `enabled`, `parameters`, `passExecutionContext`.

**Verification:** Existing serializer tests pass unchanged; new onchange-specific tests pass.

---

### U4. Extend `FormEventPlanner` for onchange: default name derivation, attribute-aware planning, and orphan detection

**Goal:** Make the planner produce correct plans for onchange annotations — including the attribute-derived default function name — and detect orphaned onchange handlers across solution-scoped forms.

**Requirements:** R5 (default name), R12 (function resolution), R13 (orphan detection), R14 (attribute in deterministic ID), R15 (ownership boundary), R16 (unrecognized handler confirmation), R18a (attribute in self-tag).

**Dependencies:** U1, U2, U3.

**Files:**
- `src/Flowline.Core/Services/FormEventPlanner.cs`
- `tests/Flowline.Core.Tests/FormEventPlannerTests.cs`

**Approach:**
- Add `internal static string StripPublisherPrefix(string name)`: if `name` contains `_`, return the substring after the first `_`; otherwise return `name` unchanged.
- Update `DeriveHandlerResolutionInputs` to handle `FormEventType.OnChange`. The current third branch: when `FunctionName` is null and `Event == OnChange`, derive `requestedFunctionName = "on" + ToPascalCase(StripPublisherPrefix(resolved.Annotation.Attribute!)) + "Change"`. Pass `resolved.Annotation.Attribute` through to callers that need it (planner loop, deterministic ID, serializer).
- For the orphan-detection loop: the existing `foreach (var evt in Enum.GetValues<FormEventType>())` naturally includes `OnChange`. For `OnChange`, the "current attributes" on a form is the union of (a) `FormXmlEventSerializer.GetOnChangeAttributes(formXml)` and (b) the set of attributes from current annotations targeting this form. Iterate this union per form instead of just the fixed onload/onsave pair, and call `GetHandlers(formXml, OnChange, attribute)` for each attribute in the union.
- Pass `resolved.Annotation.Attribute` to `FormEventDeterministicId.ForHandler` for `OnChange` events.
- Pass `attribute` to `FormXmlEventSerializer.GetHandlers` / `SetHandlers` for `OnChange` events.
- The library set (for `<formLibraries>`) is computed across all handler types — the existing union over all event types already includes `OnChange` once the enum value is added.
- Proposed-annotation text for unrecognized handlers (R16): `$"// flowline:onchange {entity} \"{form}\" {attribute} {handler.FunctionName}"`.

**Patterns to follow:** Existing `DeriveHandlerResolutionInputs` and `ToPascalCase` helpers (`FormEventPlanner.cs:243–271`); existing orphan detection loop structure.

**Test scenarios:**
- Annotation `// flowline:onchange account "A" creditlimit` (no function) → default `requestedFunctionName = "onCreditlimitChange"` (single all-lowercase segment).
- Annotation `// flowline:onchange account "A" new_credit_limit` → default = `"onCreditLimitChange"` (prefix stripped, two segments each PascalCased).
- Annotation `// flowline:onchange account "A" cr507_risk_rating` → default = `"onRiskRatingChange"`.
- Annotation with explicit `onCreditLimitChange` → passed through unchanged, `isExplicit = true`.
- `ForHandler` called with attribute dimension produces a different GUID than the same annotation without attribute (key isolation from onload/onsave).
- Form with an existing `<event name="onchange" attribute="creditlimit">` handler whose ID does not match Flowline's derivation → surfaces in unrecognized set with correct proposed annotation text.
- Form with an existing `<event name="onchange" attribute="revenue">` handler on a tracked library, with no current annotation targeting `revenue` → detected as stale, scheduled for removal (orphan detection via `GetOnChangeAttributes`).
- `GetOnChangeAttributes` union covers both annotation-referenced attributes and XML-found attributes — neither alone produces false negatives.
- A handler on a non-tracked library in an onchange event element → ignored entirely (R15 boundary).
- `StripPublisherPrefix("creditlimit")` → `"creditlimit"` (no underscore, no change).
- `StripPublisherPrefix("new_creditlimit")` → `"creditlimit"`.
- `StripPublisherPrefix("cr507_risk_rating")` → `"risk_rating"` (only first prefix stripped; remaining underscores are ToPascalCase input).

**Verification:** New planner tests pass; existing planner tests pass unchanged (the `Enum.GetValues` loop now includes `OnChange`, which tests for other event types must not inadvertently affect — verify by checking that no existing test uses real formxml containing onchange elements).

---

### U5. Extend `FormEventRenameAdvisor` for onchange self-tag check

**Goal:** When a form is renamed and an onchange annotation fails resolution, the self-tag signal correctly scans onchange event elements (with the right attribute dimension in the deterministic ID) for evidence of prior authorship.

**Requirements:** R18a.

**Dependencies:** U1, U3, U4.

**Files:**
- `src/Flowline.Core/Services/FormEventRenameAdvisor.cs`
- `tests/Flowline.Core.Tests/FormEventRenameAdvisorTests.cs`

**Approach:**
- In `FindSelfTagMatch`, the existing loop iterates `sharingAnnotations` and computes a deterministic handler ID for each. For `OnChange` annotations, pass `resolved.Annotation.Attribute` to `FormEventDeterministicId.ForHandler` (the `attribute` parameter from U1).
- When calling `FormXmlEventSerializer.GetHandlers(xdoc, evt)` for `OnChange` annotations, also pass `resolved.Annotation.Attribute` so the correct `<event name="onchange" attribute="...">` element is searched (rather than scanning all onchange handlers regardless of attribute).
- No changes to the cache lookup or sole-survivor signals — those operate on `(entity, formName)` and are attribute-agnostic.

**Patterns to follow:** `FindSelfTagMatch` in `FormEventRenameAdvisor.cs:52–88`; `DeriveHandlerResolutionInputs` call pattern already used there.

**Test scenarios:**
- Self-tag happy path: a renamed form still carries a deterministic handler ID for an onchange handler on `creditlimit`; the advisor finds it by scanning `GetHandlers(xdoc, OnChange, "creditlimit")` → returns the renamed form's name.
- Self-tag isolation: an onchange handler on `revenue` with a matching ID does not satisfy a self-tag check for `creditlimit` (attribute-keyed scan prevents false positives).
- Self-tag for `OnLoad`/`OnSave` annotations is unaffected — no attribute argument changes the existing codepath.
- No self-tag candidate exists for an onchange annotation → advisor falls through to cache and sole-survivor signals as before.
- Regression (R18): even when self-tag finds a candidate, the push still fails — push never succeeds because a suggestion exists.

**Verification:** New advisor tests pass; existing advisor tests pass unchanged; the `FormEventAnnotationParserTests` remain green (no parser changes in this unit).

---

## Verification Contract

| Command | Applies to | Proves |
|---|---|---|
| `dotnet build Flowline.slnx` | U1–U5 | No compile regressions from the new enum value, nullable field, and optional serializer parameters |
| `dotnet test tests/Flowline.Core.Tests/Flowline.Core.Tests.csproj` | U1–U5 | All new and updated test scenarios above pass |
| `dotnet test tests/Flowline.Tests/Flowline.Tests.csproj` | Cross-cut | No regressions in `FlowlineStoragePathsTests`, push-command integration tests |
| Manual push with `// flowline:onchange account "AutomateValue" creditlimit` annotation in a tracked JS file | End-to-end | Handler appears in the Account form's formxml after push; re-push is idempotent; removing the annotation removes the handler on the next push |

---

## Definition of Done

- U1–U5 implemented; all listed test scenarios pass.
- `dotnet build Flowline.slnx` clean — no warnings introduced.
- Existing `FormEventAnnotationParserTests`, `FormXmlEventSerializerTests`, `FormEventPlannerTests`, and `FormEventRenameAdvisorTests` pass unchanged.
- A push with a `flowline:onchange` annotation registers the correct handler; a subsequent push without the annotation removes it — verified against the AutomateValue Dev environment.
- R18a's note about "control-level" scanning is superseded by the empirical finding: onchange events are in the top-level `<events>` block with an `attribute` attribute. No control-level scanning is needed.
- Any exploratory code from approaches that didn't pan out is removed, not left in the diff.
