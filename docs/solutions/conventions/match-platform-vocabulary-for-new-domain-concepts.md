---
title: Match Platform Vocabulary for New Domain Concepts
date: 2026-07-12
category: docs/solutions/conventions/
module: form-event-registration
problem_type: convention
component: tooling
severity: low
applies_when:
  - "Naming a new code-level concept that mirrors or wraps an entity an external platform (SDK, UI, schema) already names"
  - "A generic language/framework-convention objection is raised against a platform-matching name"
tags:
  - naming
  - dataverse
  - conventions
  - form-events
  - code-review
  - verification
---

# Match Platform Vocabulary for New Domain Concepts

## Context

Flowline's form event registration feature let developers declare a Dataverse form's `onLoad`/`onSave` handler via source comment annotations (`// flowline:onload`, `// flowline:onsave`) instead of wiring it manually in the Maker Portal's Configure Event dialog. Building it required two new domain concepts:

- **Concept A**: a form-level reference to a JS web resource — first coded as `FormLibraryEntry`
- **Concept B**: a function bound to a form event — first coded as `FormHandler`

Both names were invented in isolation, before checking what Dataverse itself calls these things. Since this feature exists specifically to keep source code and the Maker Portal in sync, naming the underlying concepts arbitrarily risked creating a permanent translation gap between the code, the docs, and the UI a developer would still occasionally open by hand.

## Guidance

Two-part rule when a new code-level concept mirrors something an external platform already names:

**1. Match the platform's own vocabulary, don't invent your own.** Research the platform's UI and schema before naming. For Flowline's form event feature:

- Maker Portal UI: "Form libraries" panel, "Library" dropdown, "Handlers" list, "+ Event Handler" button, and prose stating "This function is called an event handler."
- Form XML schema: `<Library name=... libraryUniqueId=.../>` inside `<formLibraries>`; `<Handler functionName=... libraryName=... handlerUniqueId=.../>` inside `<events><event><Handlers>`.

These converge on **Library** and **(Event) Handler** as the platform's real vocabulary — not `FormLibraryEntry` or bare `FormHandler`.

**2. Before letting a generic convention-collision argument block a platform-matching name, verify the convention is actually used in *this* codebase.** A plausible-sounding objection was raised: keep `FormHandler` instead of `FormEventHandler` because the longer name could be confused with .NET's own `EventHandler` delegate convention (`public event EventHandler<T> SomethingChanged`). That's a real concern in the abstract — but "in the abstract" isn't good enough. Check with a targeted search:

```
Grep -E 'EventHandler|event\s+\w+\s*\(|public\s+event\s' src/
```

Zero matches. Flowline's own codebase has no `EventHandler`-delegate usage anywhere, so there was nothing for `FormEventHandler` to actually collide with. The objection didn't apply here; it only sounded like it should.

## Why This Matters

Matching platform vocabulary keeps code, docs, and Maker Portal UI speaking the same language — a developer who knows the Maker Portal's "Library" and "Event Handler" concepts can map them onto Flowline's types without a translation step. Skipping the empirical check on the convention-collision objection would have produced a permanently weaker, less-discoverable name (`FormHandler`) traded away against a risk that didn't exist in this codebase. Naming decisions are expensive to reverse once shipped in public APIs, docs, and a wiki — verify objections before they cost you the better name.

## When to Apply

- Naming is contested or under active debate, or
- Someone raises a generic language/framework convention-collision argument against the platform-matching name.

In that second case, don't resolve the debate by intuition — grep (or equivalent) the actual codebase for the competing convention before accepting or rejecting the platform-matching name.

## Examples

**Renames applied**, driven by Maker Portal and Form XML schema terms:

- `FormLibraryEntry` -> `FormLibrary` (matches Maker Portal "Library" dropdown / "Form libraries" panel, and Form XML `<Library name=... libraryUniqueId=.../>`)
- `FormHandler` -> `FormEventHandler` (matches Maker Portal "Handlers" list / "+ Event Handler" button, and Form XML `<Handler functionName=... libraryName=... handlerUniqueId=.../>`)

**Verifying the convention-collision objection before accepting it:**

```
Grep -E 'EventHandler|event\s+\w+\s*\(|public\s+event\s' src/
# 0 results — no .NET delegate EventHandler pattern anywhere in Flowline's own code
```

Result: the objection to `FormEventHandler` (confusion with .NET's `EventHandler<T>` delegate convention) had no actual collision to point to, so it didn't block the platform-matching rename.

**Secondary example — adjusting when the platform's own term is context-dependent:**

The prose glossary term went through one more refinement after the initial rename. `CONCEPTS.md` first documented Concept B as "Event Handler" (matching the Maker Portal button label verbatim), then was renamed again to "Form Event Handler": "Event Handler" alone doesn't convey that it's a form-scoped concept outside the context of the Maker Portal's own Configure Event dialog, where the scoping is implicit. This is the same match-the-platform principle applied one level deeper — match the platform's vocabulary, but when the platform's own term relies on surrounding UI context to be unambiguous, restore that context explicitly in the prose term. `CONCEPTS.md` documents and quotes this divergence from the Maker Portal's literal "+ Event Handler" label.

**Third example — the same disambiguation recurred independently (session history):** In a separate session naming a `--force <specifier>` token for `push`'s "remove unrecognized form event handler(s)" hazard, the user raised the identical concern on their own: "event or event-handlers or handlers only make sense within the context of forms... need to add 'form' to it for context" — since `push` also deals with plugin/assembly step "handlers," a different Dataverse concept entirely. The specifier initially landed on `unrecognized-form-handlers` (chosen over the longer `unrecognized-form-event-handlers`) — a deliberately shorter surface-specific compromise, distinct from the fuller `FormEventHandler` type name and "Form Event Handler" prose term used in code and docs. The same "form" disambiguator was independently required on two different surfaces (a C#/prose domain term, and a CLI flag token) within days of each other, reinforcing that "form" is the load-bearing word whenever "handler" alone would be ambiguous against Flowline's other event-handling concepts (plugin steps, SDK message handlers). It was later shortened again to `delete-form-handlers`, trading the `unrecognized` qualifier for a verb-first shape matching `delete-orphans`/`recreate-assembly` — the "form" disambiguator survived both revisions.

## Related

- `CONCEPTS.md` — "Form Library" and "Form Event Handler" glossary entries (`## Web Resources` section), including the note on diverging from the Maker Portal's literal "+ Event Handler" label
- `docs/plans/2026-07-10-002-feat-form-event-registration-plan.md` — plan doc for the feature that introduced these concepts
- `docs/plans/2026-07-11-001-refactor-force-specifier-plan.md` — companion `--force` specifier plan, where the same form-context disambiguation was independently applied to a CLI flag token
- [internal-vs-public-documentation-split](internal-vs-public-documentation-split.md) — related convention doc: same `convention` category and general theme of choosing the right vocabulary for an audience, but a different mechanism (docs-layering vs. code/type naming against a platform's UI)
