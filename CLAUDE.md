# Flowline — Claude Code Instructions

Respond like smart caveman. Cut all filler, keep technical substance.

Drop articles (a, an, the), filler (just, really, basically, actually).
Drop pleasantries (sure, certainly, happy to).
No hedging. Fragments fine. Short synonyms.
Technical terms stay exact. Code blocks unchanged.
Pattern: [thing] [action] [reason]. [next step].

## Goal of Flowline CLI

See for the goal [`README.md`](README.md)
See for the brainstorm when starting Flowline [`docs/BRAINSTORM.md`](docs/BRAINSTORM.md)

## Tone of voice

Always apply tone-of-voice rules when writing any user-facing CLI message. Full guide: `docs/tone-of-voice.md`.

## Available Commands

- `/tone` — reviews CLI messages in changed files against the tone-of-voice guide. Suggest this after writing or changing any user-facing output.

## Folder structure when a user uses Flowline.

Always follow the Flowline folder structure when creating, referencing, or reasoning about
solution files and paths. The full spec is at [`docs/folder-structure.md`](docs/folder-structure.md).

```
ProjectRoot/
├── .flowline                         ← project config
└── solutions/
    └── <SolutionName>/
        ├── <SolutionName>.sln
        ├── Package/                  ← PAC-managed (do not edit manually)
        │   ├── Package.cdsproj       ← solution package project
        │   └── src/                  ← unpacked solution XML (git-diffable)
        ├── Plugins/                  ← Plugins.csproj (plugins, workflows, custom APIs)
        └── WebResources/             ← WebResources.csproj + src/ + public/ + dist/
```

Key rules:
- All solutions live under `solutions/<SolutionName>/` — never at the repo root
- The cdsproj is always `Package/Package.cdsproj` — PAC-managed, never edit manually
- Unpacked solution XML lives in `Package/src/` — committed to source control
- The plugins project is always named `Plugins` (`Plugins.csproj`)
- Web asset build output goes to `WebResources/dist/` — this is what syncs to Dataverse
- Multiple solutions can coexist as sibling folders under `solutions/`

## GitHub Wiki

The GitHub Wiki lives in a sibling folder: `E:\Code\RemyDuijkeren\Flowline.wiki\`

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

## Documented Solutions

`docs/solutions/` — solutions to past problems (bugs, best practices, workflow patterns), organized by category with YAML frontmatter (`module`, `tags`, `problem_type`). Relevant when implementing or debugging in documented areas.

`CONCEPTS.md` — shared domain vocabulary (entities, named processes, status concepts with project-specific meaning). Relevant when orienting to the codebase or discussing domain concepts.
