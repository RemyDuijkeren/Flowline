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

Flowline is a .NET Dataverse ALM CLI. It wraps PAC CLI primitives into a Git-based
`clone -> push -> sync -> deploy` workflow for unmanaged solutions.

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

## Definition of done

- Changed behavior has focused test coverage.
- Relevant build and tests pass.
- User-facing CLI text follows `docs/tone-of-voice.md`.
- README, wiki, and CHANGELOG are updated when their documented behavior or public contracts change.
- Final response lists validation run and anything not verified.

## Tone of voice

Always apply tone-of-voice rules when writing any user-facing CLI message. Full guide: `docs/tone-of-voice.md`.

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
├── <SolutionName>.slnx               ← .sln with `clone --sln`
├── Package/                          ← PAC-managed (do not edit manually)
│   ├── Package.cdsproj               ← solution package project
│   └── src/                          ← unpacked solution XML (git-diffable)
├── Plugins/                          ← Plugins.csproj (plugins, workflows, custom APIs)
├── WebResources/                     ← WebResources.csproj + src/ + public/ + dist/
├── artifacts/                        ← packed solution zips (gitignored)
├── CHANGES.md
├── docs/                             ← not scaffolded; created by clone/sync as needed (DATAVERSE_CONTEXT.md)
└── tests/                            ← not scaffolded; recognized if present
```

Key rules:
- Exactly one Dataverse solution lives directly at the project root — never under a `solutions/<Name>/` wrapper
- The cdsproj is always `Package/Package.cdsproj` — PAC-managed, never edit manually
- Unpacked solution XML lives in `Package/src/` — committed to source control
- The plugins project is always named `Plugins` (`Plugins.csproj`)
- Web asset build output goes to `WebResources/dist/` — this is what syncs to Dataverse
- A second solution is a separate repo, or (rarer) a nested `solutions/<Name>/` folder of independent Flowline projects — see `docs/folder-structure.md` §4

## GitHub Wiki

The GitHub Wiki commonly lives in sibling folder `E:\Code\RemyDuijkeren\Flowline.wiki\` on the
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
