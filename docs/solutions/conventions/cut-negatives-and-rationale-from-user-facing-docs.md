---
title: Cut Negatives and Rationale from User-Facing Docs
date: 2026-08-07
category: docs/solutions/conventions/
module: documentation
problem_type: convention
component: documentation
severity: medium
applies_when:
  - Writing or reviewing a wiki command-reference section
  - A section runs longer than comparable sibling sections on the same page
  - Trimming a page a user has called too detailed
tags:
  - documentation-structure
  - wiki
  - writing-style
  - developer-experience
---

# Cut Negatives and Rationale from User-Facing Docs

## Context

The wiki's Command Reference had drifted into over-detail — but not the kind
[internal-vs-public-documentation-split](internal-vs-public-documentation-split.md) covers. No
internal paths had leaked. The bloat was three other species, all of it legitimately user-facing
material written at the wrong altitude:

1. **Defensive negation** — prose explaining what a command *doesn't* do, or pre-empting a
   misreading nobody had: *"clone never creates a solution"*, *"it's an allow-list, not a
   Production ban"*, *"exit code `0`, since nothing failed"*.
2. **Design rationale** — why the implementation is shaped the way it is: an *"Order of checks"*
   paragraph explaining that cheap refusals run before expensive round-trips, and a paragraph
   justifying the `prod → uat → test → dev` fallthrough as "the promotion ladder in reverse".
3. **Mechanism trivia** — steps a user cannot act on: that the plugins project is created by
   running `pac plugin init` in a directory named after the solution and then renaming only that
   directory; cache TTLs of 7 days / 12 hours / 4 hours.

## Guidance

Write what the reader can act on. Cut the rest.

- **A negative is only worth a sentence when the reader would otherwise lose data or waste a run.**
  "Clone never creates a solution" is a fact about the implementation; "to create one, use `init`"
  is the same fact as an action. Prefer the positive form, and when there is no action behind the
  negative, delete it.
- **Rationale belongs in `docs/`, not the wiki.** Why the fallthrough order is what it is serves a
  contributor. That it *is* `prod → uat → test → dev` serves a user.
- **Calibrate density against siblings, not against how much you know.** On a reference page,
  comparably complex commands should occupy comparable space. When one section is twice the length
  of its neighbours, the excess is usually rationale, not substance.
- **Keep every data-loss warning and every input constraint.** These are the actionable
  negatives — the ones the reader needs before they run the command, not after.

**Detection heuristic:** grep the region for `never|doesn't|does not|isn't|not a |except|refuse`.
High negation density is the defect's fingerprint. Most hits convert to a positive statement or
to nothing.

**The one guardrail when trimming:** delete and compress; never paraphrase a factual claim into a
simpler one. That is where errors enter on a trim pass — the sentence gets shorter and stops being
true. If a claim cannot be shortened without restating it, leave it alone.

## Why This Matters

Every defensive sentence costs the reader attention and buys nothing: they were not going to
assume the wrong thing until the doc raised the possibility. Rationale ages worse than behavior —
it describes a decision, and decisions get revisited while the observable behavior stays put, so
the rationale is the first thing to go stale and the last thing anyone re-reads. And density is
read as a signal: a section three times the length of its neighbours implies the command is three
times as complicated, which discourages people from using it.

## When to Apply

- Writing a new wiki section — draft it, then re-read for negatives and rationale before publishing.
- Reviewing a wiki edit — run the negation grep over the diff.
- A user says a page is too detailed — the fix is almost never a new page. Splitting relocates the
  noise and gives it a permanent home; deleting removes it.

## Examples

**Global flag — before:**

> `-f`, `--force <specifier>` — Approve a specific hazard by name; repeatable (`--force x --force y`).
> Each command accepts only the values it actually gates — passing one that doesn't apply fails with
> an error naming the values that do; `status` gates nothing, so passing `--force` there is always an
> error. `all` approves everything a command gates. `config` approves a `.flowline` config overwrite
> and is accepted by every command except `status` and `deploy` (`deploy` never writes `.flowline` —
> see its own section for its `first-import` hazard instead). See each command's own section for its
> command-specific hazards.

**After:**

> `-f`, `--force <specifier>` — Approve a specific hazard by name; repeatable (`--force x --force y`).
> `all` approves every hazard a command gates. `config` approves a `.flowline` config overwrite.
> Each command's section lists the hazards it gates.

The exceptions were not deleted as facts — each command's own section already names the hazards it
gates, so the global row does not need to enumerate the negative space.

---

**Rationale — deleted outright:**

> This mirrors Flowline's promotion ladder in reverse: PROD is the canonical baseline, so its
> solution state is preferred; UAT is checked next as the highest-fidelity environment below it,
> ahead of Test/Dev, which are short-lived and more likely to have diverged.

The sentence that survived states the order (`prod → uat → test → dev`) and what happens when
nothing matches. That is the whole actionable content.

---

**Negative promoted to a warning, because it can lose work:**

> **Safe to re-run:** … except when you've switched to `--managed` and the local source doesn't have
> the managed layer yet, in which case it re-syncs `Solution/src/` from Dataverse … Re-syncing
> overwrites `Solution/src/`, so commit any local edits there first.

became

> **Safe to re-run:** anything already present is left alone — remove the part you want to recreate
> and re-run clone.
>
> **Warning:** Switching an already-cloned project to `--managed` re-syncs `Solution/src/` from
> Dataverse, overwriting it. Commit local edits there first.

Same fact, lifted out of a clause and into the callout a reader will actually see.

## Related

- [internal-vs-public-documentation-split](internal-vs-public-documentation-split.md) — the adjacent
  rule: *what* content belongs in the wiki versus `docs/`. This doc covers *how* to write the
  content that legitimately belongs there. A page can satisfy that rule and still bloat with
  negatives and rationale.
- `docs/tone-of-voice.md` — the same economy applied to CLI runtime output. Scoped to messages, not
  to reference prose, but the "economical" and "honest" pillars are the same instinct.
