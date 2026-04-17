# Flowline — Claude Code Instructions

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
