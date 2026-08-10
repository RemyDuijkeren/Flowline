# Flowline — Agent Instructions

Respond like smart caveman. Cut all filler, keep technical substance.

Drop articles (a, an, the), filler (just, really, basically, actually).
Drop pleasantries (sure, certainly, happy to).
No hedging. Fragments fine. Short synonyms.
Technical terms stay exact. Code blocks unchanged.
Pattern: [thing] [action] [reason]. [next step].

## Product orientation

- [`README.md`](README.md) — product purpose, public workflow, and command surface
- [`STRATEGY.md`](STRATEGY.md) — target problem, product boundaries, and architectural direction
- [`CONCEPTS.md`](CONCEPTS.md) — shared domain vocabulary and project-specific concepts
- [`docs/ALM-strategy.md`](docs/ALM-strategy.md) — how Dataverse ALM works: layers, environments,
  versioning, import semantics, and the strategies teams run. Read before reasoning about managed
  vs unmanaged, solution versioning, deploy semantics, or why Flowline's model differs from
  Microsoft's default

Flowline is a .NET Dataverse ALM CLI. It wraps PAC CLI primitives into a Git-based
`clone -> push -> sync -> deploy` workflow for unmanaged solutions.

### Source-of-truth model (default)

By default Flowline treats **PROD as the source of truth** — PROD holds the *unmanaged*
solution, and it plays the role `master` plays in Git. A **DEV environment is a branch of
PROD**: you `provision dev` to spin one up, make changes there, then `deploy` to PROD
("merge into master"), and optionally re-provision DEV for the next change. `sync` shows what
changed in DEV against that PROD baseline.

This is the default, not a requirement. Users who keep only *managed* in PROD and treat DEV
as the truth are also supported — Flowline just doesn't assume it. Practical consequences:

- **Clone** normally pulls from PROD (that's where the unmanaged source lives). Don't restrict
  clone's environment choice to DEV-type environments, and don't assume the cloned-from
  environment is the DEV role — in the default model it's PROD.
- The **DEV-only guard** (Sandbox/Developer whitelist) applies to *greenfield create* writes
  only, never to clone-existing, which writes nothing to Dataverse.

Environment-type facts that constrain the flow (verified against `pac`):

- `pac solution clone` and `pac solution sync` work against a **Developer** environment — a Developer
  env is a valid clone/sync target.
- `pac admin copy` **cannot target a Developer environment** (copying a Production or Sandbox into a
  Developer env is disallowed). Because `provision` (`ProvisionCommand`) branches DEV from PROD via
  `pac admin copy`, a *provisioned* DEV is always a **Sandbox**, never a Developer env. Developer envs
  are usable as clone/sync sources but can't be `provision` targets.

Background: https://automatevalue.com/blog/everyone-got-alm-wrong-in-dynamics-365-dataverse/

Full treatment in [`docs/ALM-strategy.md`](docs/ALM-strategy.md): §8.2 places this model against
Microsoft's DEV-as-truth default, §9 covers what it costs (no delete propagation, no solution-level
rollback, merge-behavior components), and §10.4 gives the rule for when managed solutions are
actually required.

## Repository map

- `Flowline.slnx` — main solution
- `src/Flowline/` — CLI executable; command registration in `Program.cs`, command implementations in `Commands/`
- `src/Flowline.Core/` — engine: Dataverse services, domain logic, console rendering primitives
- `src/Flowline.Attributes/` — public plugin and Custom API attributes

### Project boundary rule

`Flowline.Core` = everything that could run without a terminal attached — engine, Dataverse
operations, rendering primitives. `Flowline` = Spectre.Cli wiring only: `Program.cs`, `Commands/`,
settings types, `Templates/`.

Core is the engine, not a UI-free domain layer — `Spectre.Console` in Core is correct and expected
(`Console/` holds render hooks and path formatting). The one hard constraint: **Core must never
reference `Flowline`**. Dependency direction is one-way and compiler-enforced; that enforcement is
why the two projects exist.

New file placement: if it needs `CommandContext`, `CommandSettings`, or command registration, it
belongs in `Flowline`. Otherwise it belongs in `Flowline.Core`. Known misfiled today (move
opportunistically, not as a big-bang refactor): `Flowline/Services/`, `Flowline/Generators/`,
`Flowline/Validation/`, and engine parts of `Flowline/Utils/` (`PacUtils`, `GitUtils`,
`SolutionChangeSummary`).
- `tests/Flowline.Tests/` — CLI and command tests
- `tests/Flowline.Core.Tests/` — core service tests; also covers `Flowline.Attributes` contracts via the metadata scanner
- `docs/solutions/` — prior bug fixes, architectural patterns, and workflow solutions
- `.github/workflows/ci.yml` — authoritative CI pipeline

## Orientation order

For unfamiliar work, read only relevant context in this order:

1. `README.md` for product purpose and public commands.
2. `STRATEGY.md` when work may affect product scope or architectural direction.
3. `CONCEPTS.md` for domain vocabulary.
4. `src/Flowline/Program.cs` for command registration, then relevant command implementation.
5. Relevant core service and corresponding tests.
6. Search `docs/solutions/` for matching modules, tags, or problem types.

## Build and verification

- Restore: `dotnet restore Flowline.slnx`
- Build: `dotnet build Flowline.slnx`
- Full test suite: `dotnet test Flowline.slnx`
- Prefer targeted test projects or `--filter` while iterating.
- Run full relevant test projects before finishing. Run full suite for cross-cutting changes.
- Treat `.github/workflows/ci.yml` as source of truth for CI configuration.
- Do not edit generated output in `bin/`, `obj/`, `artifacts/`, or `.nupkg/`.
- Preserve unrelated working-tree changes.
- **Run the CLI from a Release build when checking user-facing output.** `Program.cs` calls
  `config.PropagateExceptions()` inside `#if DEBUG`, and propagation beats the `SetExceptionHandler`
  that renders `Error: <message>` with a typed `ExitCode`. So a Debug build prints a raw stack trace
  for every `FlowlineException` — correct error handling looks broken. Use `-c Release` to verify
  messages, wording, or exit codes; use Debug only when you want the stack trace.

## Branching

**Never create a branch on your own initiative.** Ask whether the work should land on `master` or on
its own branch, and let the size of the work set the recommendation:

- **Small work** — a bug fix, a test alignment, a doc edit, a one-file change. Does not justify a
  branch; default to `master`.
- **Bigger work** — a feature, a multi-file refactor, anything spanning several commits. Warrants a
  branch, with a worktree alongside it.
- **`/ce-work` is the signal** that the work is big enough to warrant branch + worktree. Set that up
  without asking.

Being told "commit" is not authorization to branch. Ask before the first commit of a task, name which
option you'd recommend, and say why.

## Definition of done

- Changed behavior has focused test coverage.
- Relevant build and tests pass.
- User-facing CLI text follows `docs/tone-of-voice.md`.
- README, wiki, and CHANGELOG are updated when their documented behavior or public contracts change.
- Final response lists validation run and anything not verified.

## Tone of voice

Always apply tone-of-voice rules when writing any user-facing CLI message. Full guide: `docs/tone-of-voice.md`.

## Agent-drivable command surface

Most Flowline runs are unattended. When adding or changing a command, flag, prompt,
confirmation, exit code, or help text, follow
[`.claude/skills/cli-for-agents/SKILL.md`](.claude/skills/cli-for-agents/SKILL.md) — it maps each
rule (flag-first input, `ConfirmGated`, scoped `--force`, `--dry-run`, `ExitCode` selection,
`CannotContinue`) to the existing Flowline mechanism, so none get reinvented.

## Optional agent commands

- `/tone` — reviews CLI messages in changed files against the tone-of-voice guide.

Slash commands depend on installed agent plugins. When `/tone` is unavailable, review changed
messages directly against `docs/tone-of-voice.md` instead. Suggest `/tone` after writing or
changing user-facing output only when command is available.

## Folder structure when a user uses Flowline.

Always follow the Flowline folder structure when creating, referencing, or reasoning about
solution files and paths. The full spec is at [`docs/folder-structure.md`](docs/folder-structure.md).

```
ProjectRoot/
├── .flowline                         ← project config
├── <SolutionName>.slnx               ← solution file (an existing .sln is reused, never converted)
├── Solution/                         ← PAC-managed (do not edit manually)
│   ├── <SolutionName>.cdsproj        ← solution package project
│   └── src/                          ← unpacked solution XML (git-diffable)
├── Plugins/                          ← <SolutionName>.Plugins.csproj (plugins, workflows, custom APIs)
├── WebResources/                     ← <SolutionName>.WebResources.csproj + src/ + public/ + dist/
├── artifacts/                        ← packed solution zips (gitignored)
├── CHANGES.md
├── docs/                             ← not scaffolded; created by clone/sync as needed (DATAVERSE_CONTEXT.md)
└── tests/                            ← not scaffolded; recognized if present
```

Key rules:
- Exactly one Dataverse solution lives directly at the project root — never under a `solutions/<Name>/` wrapper
- This tree is what `clone` scaffolds, not what the commands require: every command after `clone` locates the three projects by reading the solution file, so any of them can be moved
- The cdsproj is `Solution/<SolutionName>.cdsproj` — PAC-managed, never edit manually; `pac` writes that filename and Flowline never renames it
- Unpacked solution XML lives in `Solution/src/` — committed to source control
- Folders are role-based and fixed (`Solution/`, `Plugins/`, `WebResources/`); project files carry the solution's identity, because that is the name that escapes into Dataverse
- Web asset build output goes to `WebResources/dist/` — this is what syncs to Dataverse
- A repo with no solution file at all is an error, not a fallback: every command but `clone` needs the solution file, so a folder without one throws `NotFound` naming stand-alone mode (`flowline push --pluginFile <dll>`) as the way to push without one
- A second solution is a separate repo, or (rarer) a nested `solutions/<Name>/` folder of independent Flowline projects — see `docs/folder-structure.md` §4

## GitHub Wiki

The GitHub Wiki commonly lives in sibling folder `..\Flowline.wiki\` on the
primary Windows development machine. Do not assume this path exists on other machines. If wiki
checkout is unavailable or outside writable workspace, report that before completion; do not
silently skip required wiki updates or create a replacement folder.

When changing code that affects user-facing behavior — commands, flags, plugin registration, web
resource handling, project structure — update the relevant wiki page(s) alongside any README changes.

Wiki pages and their scope:
- `Getting-Started.md` — install, auth, project workflow
- `Command-Reference.md` — all commands and flags
- `Push-Plugins-and-Custom-APIs.md` — `[Step]`, `[Filter]`, `[CustomApi]` attribute reference
- `Push-WebResources.md` — form event auto-wiring, web resource dependencies, push/deploy mechanics
- `WebResources-Project.md` — TypeScript setup, Rollup build, folder structure
- `Migration-from-spkl.md`, `Migration-from-Daxif.md`, `Migration-from-PACX.md` — migration guides
- `Known-Limitations.md` — unsupported features and planned work

## Compound Engineering Workflow

Before choosing `/ce-ideate`, `/ce-brainstorm`, `/ce-plan`, or going straight to implementation, consult [`docs/compound-engineering-workflow.md`](docs/compound-engineering-workflow.md). Covers when to use each skill, when to skip, and why certainty drives the choice more than size.

These slash commands depend on installed agent plugins. If unavailable, follow the documented
decision process manually without inventing command behavior.

## Documented Solutions

`docs/solutions/` — solutions to past problems (bugs, best practices, workflow patterns), organized by category with YAML frontmatter (`module`, `tags`, `problem_type`). Relevant when implementing or debugging in documented areas.

`CONCEPTS.md` — shared domain vocabulary (entities, named processes, status concepts with project-specific meaning). Relevant when orienting to the codebase or discussing domain concepts.
