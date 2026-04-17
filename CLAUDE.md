# Flowline — Claude Code Instructions

## Karpathy Skills — Coding Behaviour

Behavioural guidelines to reduce common LLM coding mistakes.

**Tradeoff:** These guidelines bias toward caution over speed. For trivial tasks, use judgment.

### 1. Think Before Coding

**Don't assume. Don't hide confusion. Surface tradeoffs.**

Before implementing:
- State your assumptions explicitly. If uncertain, ask.
- If multiple interpretations exist, present them — don't pick silently.
- If a simpler approach exists, say so. Push back when warranted.
- If something is unclear, stop. Name what's confusing. Ask.

### 2. Simplicity First

**Minimum code that solves the problem. Nothing speculative.**

- No features beyond what was asked.
- No abstractions for single-use code.
- No "flexibility" or "configurability" that wasn't requested.
- No error handling for impossible scenarios.
- If you write 200 lines and it could be 50, rewrite it.

Ask yourself: "Would a senior engineer say this is overcomplicated?" If yes, simplify.

### 3. Surgical Changes

**Touch only what you must. Clean up only your own mess.**

When editing existing code:
- Don't "improve" adjacent code, comments, or formatting.
- Don't refactor things that aren't broken.
- Match existing style, even if you'd do it differently.
- If you notice unrelated dead code, mention it — don't delete it.

When your changes create orphans:
- Remove imports/variables/functions that YOUR changes made unused.
- Don't remove pre-existing dead code unless asked.

The test: Every changed line should trace directly to the user's request.

### 4. Goal-Driven Execution

**Define success criteria. Loop until verified.**

Transform tasks into verifiable goals:
- "Add validation" → "Write tests for invalid inputs, then make them pass"
- "Fix the bug" → "Write a test that reproduces it, then make it pass"
- "Refactor X" → "Ensure tests pass before and after"

For multi-step tasks, state a brief plan:
```
1. [Step] → verify: [check]
2. [Step] → verify: [check]
3. [Step] → verify: [check]
```

Strong success criteria let you loop independently. Weak criteria ("make it work") require constant clarification.

## Tone of voice

Always apply the Flowline tone-of-voice rules when writing or reviewing any user-facing CLI
message (`AnsiConsole.MarkupLine`, spinner labels, error messages, finish lines).

The full guide is at [`docs/tone-of-voice.md`](docs/tone-of-voice.md). Key rules:

- **Spinner label** — active verb + ellipsis + bold name: `Cloning [bold]CrO7982[/] from Dataverse...`
- **Spinner sub-item** — present tense, ≤ 30 chars, no punctuation: `Git's good`
- **Success** — `[green]`, no word "successfully", drop redundant metadata
- **Skip** — `[dim]`, phrased as `already there — skipping`
- **Error** — `[red]`, what happened + what to do next, stops the act immediately
- **Finish line** — last line only, one emoji, references the next command
- **Verbose** — always `[dim]`, always after the clean line, never changes structure

## Folder structure

Always follow the Flowline folder structure when creating, referencing, or reasoning about
solution files and paths. The full spec is at [`docs/folder-structure.md`](docs/folder-structure.md).

```
ProjectRoot/
├── .flowline                         ← project config
└── solutions/
    └── <SolutionName>/
        ├── <SolutionName>.sln
        ├── SolutionPackage/          ← SolutionPackage.cdsproj + unpacked XML source
        ├── Extensions/               ← Extensions.csproj (plugins, workflows, custom APIs)
        └── WebResources/             ← WebResources.csproj + src/ + public/ + dist/
```

Key rules:
- All solutions live under `solutions/<SolutionName>/` — never at the repo root
- The `.cdsproj` is always named `SolutionPackage.cdsproj` inside `SolutionPackage/`
- The plugins project is always named `Extensions` (`Extensions.csproj`)
- Web asset build output goes to `WebResources/dist/` — this is what syncs to Dataverse
- Multiple solutions can coexist as sibling folders under `solutions/`
