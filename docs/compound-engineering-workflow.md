# Compound Engineering Workflow

Skills for planning, implementing, reviewing, and documenting work. Explains when to use each, when to skip, and where output lands.

Skills provided by the [Compound Engineering Plugin](https://github.com/EveryInc/compound-engineering-plugin).

---

## The full pipeline

```
/ce-ideate → /ce-brainstorm → /ce-plan → /ce-work → /code-review-and-fix → /ce-compound → /commit-merge-fast
```

Rarely need every step. Driver is **certainty**, not size. See [decision table](#decision-table) below.

---

## Planning phase

### `/ce-ideate`
Use when you don't know **what** to build.

Option space is open. Multiple approaches could work. Output: ranked survivors with axes, confidence, complexity — a visual HTML artifact with diagrams and tradeoff cards. Not a working document; opened in a browser, never edited.

Output lands in: `docs/ideation/`

Skip when the approach is already obvious.

---

### `/ce-brainstorm`
Use when you know **what** but not the exact **shape**.

Despite the name, this is structured **requirements analysis**. Direction is chosen; now you need edge cases, acceptance criteria, key technical decisions, and open questions locked down. Output: a requirements markdown doc that `/ce-plan` consumes directly.

Output lands in: `docs/brainstorms/`

Skip when ideation already produced enough precision, or when the feature is small enough that the plan is the requirements.

---

### `/ce-plan`
Use when requirements are clear and you want **file-level implementation steps**.

Consumes a requirements doc. Output: units with file paths, dependencies, and verification steps. Skip for trivial changes — just implement.

Output lands in: `docs/plans/`

---

## Implementation phase

### `/ce-work`
Use to **execute a plan end-to-end**.

Pass it a plan file path or a concrete build request. Reads the plan, implements all units, verifies each step. Use `/ce-debug` instead when the problem is open-ended (no plan, unknown cause).

Output: code changes in the working tree.

---

## Review phase

### `/code-review-and-fix`
Use after implementation to **catch bugs and inefficiencies**.

Runs a full code review of the diff, then walks you through each finding interactively — you approve or skip each fix. Approved fixes applied in one batch. Covers correctness bugs, reuse opportunities, simplification, and efficiency.

Run before committing. Skip for trivial one-liner changes.

---

## Knowledge capture

### `/ce-compound`
Use **after solving a non-trivial problem** to document it for future reference — before closing the branch.

Auto-triggered by phrases like "that worked", "it's fixed", "working now". Run it while the branch is still open so the conversation context and code changes are available together. Can also be invoked manually after any non-obvious fix or design decision.

Runs parallel subagents to extract the problem, write the solution doc, and find overlapping existing docs to avoid duplication.

Two modes:
- **Full** (default) — parallel subagents, overlap detection, cross-references existing docs
- `mode:headless` — non-interactive, for automations

Side effects beyond the solution doc:
- Updates `CONCEPTS.md` with any new domain vocabulary introduced by the solution
- May add a brief pointer to `CLAUDE.md` if the knowledge store needs surfacing

Output lands in: `docs/solutions/[category]/` + may update `CONCEPTS.md`

---

## Wrap-up

### `/commit-merge-fast`
Use at the **very end** to close the branch.

Commits remaining changes, fast-forward merges the current branch into main, then deletes the branch. Run after `/ce-compound` — once the branch is deleted there is no going back.

---

## Decision table

| Scenario | Workflow |
|---|---|
| Vague problem, many options | ideate → brainstorm → plan → work → review → compound → commit |
| Know the approach, details unclear | brainstorm → plan → work → review → compound → commit |
| Clear feature, complex implementation | plan → work → review → commit |
| Small or obvious change | implement → commit |
| Non-obvious fix without a full feature branch | work → compound → commit |

---

## Key rule

**Size is a proxy for certainty, not the driver.**

A small feature with genuine alternatives still benefits from ideation. A large feature with an obvious approach can go straight to plan.

Ask: _do I know what I'm building?_ If yes, skip ideate. _Are the requirements precise enough to implement from?_ If yes, skip brainstorm.

---

## Docs folder structure

| Folder / File | Created by | Content |
|---|---|---|
| `docs/ideation/` | `/ce-ideate` | HTML visual artifacts — ranked ideas, tradeoff cards, SVG diagrams. Read in browser. Never edited. |
| `docs/brainstorms/` | `/ce-brainstorm` | Markdown requirements docs — acceptance criteria, key decisions, open questions. Referenced during planning and implementation. |
| `docs/plans/` | `/ce-plan` | Markdown implementation plans — units, file paths, dependencies, verification steps. Read during `/ce-work`. |
| `docs/solutions/` | `/ce-compound` | Markdown solution docs with YAML frontmatter — bugs fixed, patterns established, decisions made. Searchable by module, tag, problem type. |
| `CONCEPTS.md` | `/ce-compound` (side effect) | Shared domain vocabulary — entities, named processes, status concepts with project-specific meaning. Updated when new terms emerge. |

### Output format rationale

| Format | Used for | Reason |
|---|---|---|
| HTML | Ideation | Read-once visual artifact — rich cards, diagrams, status chips. Never edited. |
| Markdown | Everything else | Working references — diffable, grepped, linked, edited during implementation. |
