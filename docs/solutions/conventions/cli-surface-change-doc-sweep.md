---
title: "A CLI Surface Change Must Sweep Every Doc Surface, Including the Shipped Agent Skills"
date: 2026-10-31
category: docs/solutions/conventions/
module: flowline-cli
problem_type: convention
component: documentation
severity: medium
applies_when:
  - "Renaming, removing or replacing a command, flag, positional argument or placeholder"
  - "Changing the wording a help example or error message tells the user to type"
  - "A plan lists the docs to update for a breaking CLI change"
  - "A reviewer reports that no stale spelling remains"
tags:
  - cli-surface
  - docs-drift
  - plugin-skills
  - wiki
  - help-examples
  - breaking-change
---

# A CLI Surface Change Must Sweep Every Doc Surface, Including the Shipped Agent Skills

## Context

The solution name moved from a bare positional on `push` and `generate` to `-n|--solution-name`, with no alias by design: the old shape fails to parse. The plan's docs list named README, CHANGELOG, `docs/` and the wiki, which matches the Definition of Done in `AGENTS.md:152` ("README, wiki, and CHANGELOG are updated when their documented behavior or public contracts change"). Every item on that list was updated and a regex sweep came back clean.

Three misses still got past that point and were caught only in code review:

- Four shipped agent skills under `plugin/skills/` (`flowline`, `flowline-plugins`, `flowline-webresources`, `flowline-migration`) still showed `flowline push <solution> ...`. Nothing on the list named them. The first code review caught it, and only because the reviewer searched the whole repo instead of the listed files.
- An independent reviewer reported "no stale text remains in the skills" without having searched them. Its claim was wrong.
- A wiki example (`04-Command-Reference.md`, the WebResources setup snippet) survived the regex sweep. A second review, told to run and quote its searches, found it.

A planning session for the `settings push`/`pull` rename hit the same pattern (session history): a sweep keyed on the command string missed user-facing messages that described the concept in prose ("the pull placeholder"), and plugin skills appeared as a named sweep target there for the first time.

## Guidance

For any change to what a user types, the doc surfaces are:

| Surface | Path |
|---|---|
| Help examples | `src/Flowline/Program.cs` (`.WithExample(...)`) |
| Error and hint messages | `src/Flowline/Commands/`, `src/Flowline.Core/` |
| Scaffolded templates | `src/Flowline/Templates/` |
| Shipped agent skills | `plugin/skills/*/SKILL.md` |
| README | `README.md` (examples and the command table) |
| Wiki | sibling repo `../Flowline.wiki` (its own commit) |
| CHANGELOG | `CHANGELOG.md` (Unreleased; Breaking when the old form stops working) |
| Strategy and concepts | `STRATEGY.md`, `CONCEPTS.md`, `docs/folder-structure.md` when they name the command |

Leave dated records alone (`docs/reviews/`, `docs/solutions/`, `docs/ideation/`, `docs/plans/`, older CHANGELOG entries).

Close the change with a literal search over every surface at once, and keep its output. Run it from the main checkout, where the wiki is the sibling folder (from a worktree, give the wiki's absolute path):

```bash
rg -n "flowline (push|generate) [A-Z<\[]|init <name>|clone <solution>|\[solution\]" \
  README.md plugin/ src/ ../Flowline.wiki
```

Read every hit, do not count them: `[solution]` also matches a C# collection expression in `src/`, which is not a command example.

Then run a second search for the concept in prose, not the command string: the words a message uses to describe the old shape ("first argument", "as an argument", "the pull placeholder").

When delegating the sweep or a review, require the agent to quote the searches it ran. A "nothing stale" claim without them is not evidence.

## Why This Matters

Agents copy examples verbatim, and most Flowline runs are unattended. A stale example in a shipped skill becomes a failing command in someone else's pipeline. The no-alias rule makes that failure loud, which helps, but it lands on the user, not on the change that caused it.

The list in `AGENTS.md` is a floor, not the inventory. A sweep scoped to the list inherits its gaps, and a reviewer handed the same list repeats them.

## When to Apply

- Any rename or removal of a command, flag, positional or placeholder, breaking or not.
- Rewording a message that tells the user what to type.
- Before marking a plan's docs unit done, and before accepting a reviewer's clean report on docs.

## Examples

Missed by the listed sweep, found by a whole-repo search:

```text
plugin/skills/flowline/SKILL.md:23
  `flowline push <SolutionName> --plugin-file <dll|nupkg> --env <url>`
```

Fixed:

```text
  `flowline push --solution-name <SolutionName> --plugin-file <dll|nupkg> --env <url>`
```

Related: `docs/solutions/design-patterns/extending-identity-key-plan-files-list-incomplete.md` has the same failure shape (a plan's declared file list missed a consumer) and the same fix (search the tree literally instead of trusting the list).
