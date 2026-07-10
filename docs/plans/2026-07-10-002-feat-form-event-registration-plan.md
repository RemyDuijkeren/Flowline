---
title: "feat: Automatic form event handler registration"
type: feat
date: 2026-07-10
artifact_contract: ce-unified-plan/v1
artifact_readiness: requirements-only
product_contract_source: ce-brainstorm
---

# feat: Automatic Form Event Handler Registration - Plan

## Goal Capsule

- **Objective:** `flowline push` automatically registers and keeps in sync form `onLoad`/`onSave` JavaScript event handlers declared via a source-local annotation, so developers stop wiring handlers by hand in the maker portal after every new or changed function.
- **Product authority:** Extends the `// flowline:depends` annotation mechanism (`docs/plans/2026-06-13-002-feat-webresource-dependency-registration-plan.md`) to a new kind of form metadata.
- **Open blockers:** none for planning to start; two empirical unknowns are called out under Risks below and should be spiked early in implementation, the way `dependencyxml`'s format was spiked in U1 of the dependency-registration plan.

---

## Problem Frame

Flowline syncs web resource *content* but has no way to wire a JS function to a form event. Every time a developer adds or renames a handler function, they must open the form in the maker portal, add the library, type the function name, and publish — a manual step that's easy to forget, isn't source-controlled, and leaves no trace of intent in the repository.

---

## Empirical Findings (verified live against a Dataverse dev environment)

These were confirmed by manually registering handlers via the maker portal and diffing `systemform.formxml` before/after — see conversation for full diffs.

- Registering a handler adds two things to `formxml`, both server-generated GUIDs: a `<Library name="{webresource-name}" libraryUniqueId="{guid}"/>` under `<formLibraries>`, and a `<Handler functionName="{...}" libraryName="{...}" handlerUniqueId="{guid}" enabled="true" parameters="" passExecutionContext="true"/>` under `<events><event name="onload"|"onsave"><Handlers>`.
- `<Handlers>` (designer/user-added) is a distinct list from `<InternalHandlers>` (system/ISV-added) under the same `<event>`. Only `<Handlers>` should ever be touched.
- `libraryName` must exactly match a `Library.name` in `formLibraries`; `functionName` is an arbitrary string, not validated by Dataverse against the file's actual contents.
- Re-registering/updating an existing handler (e.g. changing `parameters`) reuses the existing `handlerUniqueId` rather than minting a new one — required for idempotent re-deploys.
- `parameters` stores the comma-separated string verbatim, no transformation.
- A form with no prior `<events>`/`<formLibraries>` gets these elements created fresh. The newly-created `<event>` node's `application`/`active` attributes were observed as `true`/`true` on one form and `false`/`false` on another (see Risks) — cause not yet understood.
- **Publishing the form is required** for the change to take effect; a saved-but-unpublished change is invisible on re-query.
- Quick Create forms support `onLoad`/`onSave` the same way Main forms do, structurally identical `<events>`/`formLibraries` shape. Quick View forms do not support JS libraries at all (confirmed via maker portal — no library/event UI available for that form type).
- Field-level `onchange` events live in a different XML location entirely (nested inside `<control><events>`, not the top-level `<events>` block) — a materially different mechanism, out of scope here.

---

## Requirements

**Annotation syntax**

- R1. A JS file declares a form event binding via `// flowline:onload <entity> <form> [Function[(param1,param2,...)]]` and `// flowline:onsave` (same shape), mirroring the `// flowline:depends` annotation family.
- R1a. Recognized in all three comment forms `flowline:depends` already supports — `//`, `//!`, `/*! ... */` — and scanned across the whole built file, not just a leading block, so the annotation survives minification the same way: Terser/esbuild/SWC preserve `//!`/`/*! ... */` "legal comments" by default even when stripping regular comments, and a plain `//` comment ahead of code still works pre-minification. Reuses `WebResourceAnnotationParser`'s existing regex/scanning approach rather than a new mechanism.
- R2. `<entity>` is the table logical name.
- R3. `<form>` is the form's display name — written bare when it contains no whitespace, double-quoted when it does (Dataverse form names routinely contain spaces).
- R4. `Function` is optional. When omitted, it defaults to `onLoad` for `flowline:onload` and `onSave` for `flowline:onsave`.
- R5. An optional trailing `(param1,param2,...)` after the function name sets the Handler's comma-separated `parameters` string. Omitted entirely means empty parameters.
- R6. Function name matching (whether explicit or defaulted) is case-insensitive against the file's actual exported function names. The registered `functionName` uses the real casing found in the file, not the casing written in the annotation.

**Validation**

- R7. If the resolved function name does not exist in the file, `push` fails with a clear error naming the file and the missing function. No handler is registered.
- R8. If `<entity>` or `<form>` does not resolve to an existing table / form, `push` fails with a clear error. No handler is registered.

**Sync behavior**

- R9. Applies to Main and Quick Create forms. Quick View forms are excluded (no JS support). Field-level `onchange` is excluded (different XML location/mechanism).
- R10. After web resource content sync, compute the desired `(entity, form, event)` → handler set from all local annotations.
- R11. Read current `formxml` before writing (read-modify-write; never overwrite blind).
- R12. Add missing `Library`/`Handler` entries; when a `Handler` for a given `(functionName, libraryName)` already exists, reuse its `handlerUniqueId` and update only what changed (e.g. `parameters`).
- R13. `Enabled` and `Pass execution context as first parameter` are always set `true`. Neither is configurable via the annotation in this version.
- R14. Removing a `// flowline:onload`/`onsave` annotation removes the corresponding `Handler` on the next `push`, subject to the ownership rule (R15).
- R15. Flowline only ever adds, updates, or removes `Handler` entries whose `libraryName` points to a web resource tracked in this project's WebResources folder. Handlers referencing any other library (other solutions, ISVs, system) are never modified or removed — same boundary already used for dependency-annotation orphan exemption.
- R16. Any form whose `Handlers` or `formLibraries` changed is published after the update, in the same publish step web resource changes already go through — publishing is required for the change to take effect; a saved-but-unpublished form is unchanged when re-queried.

---

## Scope Boundaries

### Out of scope for this version

- Field-level `onchange` events (different XML location: `<control><events>`, not the top-level `<events>` block).
- Quick View forms (Dataverse does not support JS libraries on this form type).
- Per-annotation configuration of `Enabled` or `Pass execution context` — both fixed to `true`.
- Any event type beyond `onload`/`onsave` (the maker portal's own "Configure Event" dialog exposes only these two).

### Outside this product's identity

- General form layout/authoring (fields, tabs, sections) — Flowline manages event/library wiring only, not form design.

---

## Risks & Open Questions for Planning

- **`<event>` node defaults when created fresh:** observed `application`/`active` attribute values differed between two forms when Dataverse created the `<event>` element for the first time (`true`/`true` on one Main form, `false`/`false` on a Quick Create form). Needs a spike to determine the correct values to write when Flowline creates this element itself, the way `dependencyxml`'s format was spiked before implementation in the prior dependency-registration plan.
- **Function-existence check mechanics:** annotations are parsed from the *built* `dist/*.js` file (same file `flowline:depends` already scans), not the TypeScript source. Detecting "does function X exist" against compiled/bundled output (rather than raw TS) needs a concrete detection approach — likely a regex scan for function declarations/assignments, to be worked out in planning.
- **Case-insensitive resolution + real-casing lookup** both depend on the same existence-check mechanism above, so they share this risk.

---

## Documentation Notes

- `WebResources-Project.md` (wiki) — document the `// flowline:onload`/`// flowline:onsave` annotation syntax alongside the existing `// flowline:depends` documentation, once implemented.
