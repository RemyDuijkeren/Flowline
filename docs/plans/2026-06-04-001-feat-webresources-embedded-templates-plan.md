---
title: "feat: WebResources embedded template scaffolding"
type: feat
status: completed
date: 2026-06-04
---

# feat: WebResources embedded template scaffolding

## Summary

Replace `dotnet new classlib` in `SetupWebResourcesProjectAsync()` with embedded resource templates that emit a correct `Microsoft.Build.NoTargets`-based project alongside a full TypeScript + Rollup toolchain — so the generated WebResources project builds and syncs from day one.

---

## Problem Frame

`flowline clone` creates the WebResources project by running `dotnet new classlib`, which emits an `Microsoft.NET.Sdk` project that tries to compile C#. That is the wrong SDK, wrong behavior. No TypeScript tooling is generated (`package.json`, `rollup.config.mjs`, `tsconfig.json`, `eslint.config.mjs`), leaving developers with an empty shell that requires manual setup before it can build or sync web resources to Dataverse.

---

## Requirements

- **R1.** Generated `WebResources.csproj` uses `Sdk="Microsoft.Build.NoTargets/3.7.134"` with an incremental `NpmBuild` target (stamp-file pattern) that runs `npm run build` before MSBuild's `Build` target
- **R2.** `package.json`, `rollup.config.mjs`, `tsconfig.json`, `eslint.config.mjs` generated in the WebResources folder during `flowline clone`
- **R3.** `src/`, `public/`, `dist/` directories created (existing behavior preserved)
- **R4.** All template files maintained as embedded resources in the `Flowline` assembly — no external file dependencies at runtime
- **R5.** `npm install` not run during scaffold; generated project is ready for the developer to run manually
- **R6.** Project registered in `.sln` via `dotnet sln add` (unchanged)

---

## Scope Boundaries

**In scope:** WebResources project scaffolding only.

**Out of scope:**
- Plugins scaffolding — uses `pac plugin init`; no change needed
- Running `npm install` or `npm run build` during clone
- Token substitution in template files — current templates contain no per-solution variables
- SolutionPackage subfolder reorganization — separate architectural change

### Deferred to Follow-Up Work

- **SolutionPackage subfolder:** Moving PAC clone output (`src/`, `<SolutionName>.cdsproj`) into a `SolutionPackage/` subfolder for a cleaner IDE solution view. Requires updating PAC command working directories throughout `CloneCommand` and `SyncCommand`.
- **Publisher prefix in rollup:** `rollup.config.mjs` has `namespacePrefix = ''`; could be populated from publisher prefix. Defer until explicitly needed — no token infrastructure required now.
- **npm PATH guard:** `NpmBuild` MSBuild target fails if `npm` is not installed. A `Condition` or try-exec wrapper could make it skip gracefully. Known limitation at scaffold time.
- **`--npm-install` flag:** Opt-in install during clone. Defer until template is stable.

---

## Output Structure

```
src/Flowline/
├── Templates/
│   └── WebResources/
│       ├── WebResources.csproj   ← Microsoft.Build.NoTargets + incremental NpmBuild
│       ├── package.json          ← TypeScript + Rollup + Power Apps ESLint devDependencies
│       ├── rollup.config.mjs     ← auto-discovers src/*.ts, bundles to dist/ as IIFE
│       ├── tsconfig.json         ← ES2022, strict, noEmit
│       └── eslint.config.mjs     ← Power Apps ESLint rules
└── Utils/
    └── TemplateWriter.cs         ← new: loads embedded resource by logical name, writes to disk
```

---

## Context & Research

**Reference implementation:** `E:\Code\AutomateValue\SpotlerAutomate.Dataverse\src\WebResources\` — production Dataverse WebResources project. Template content sourced verbatim from this reference.

**Current scaffolding flow** (`src/Flowline/Commands/CloneCommand.cs`, `SetupWebResourcesProjectAsync()`, ~lines 324–366):
1. `dotnet new classlib --name WebResources` — spawns process, creates wrong SDK project
2. `dotnet sln add` — registers in solution
3. `File.Delete("Class1.cs")` — removes generated stub
4. `Directory.CreateDirectory` ×3 — creates `src/`, `public/`, `dist/`

**New flow:**
1. Write 5 template files via `TemplateWriter` — no process spawn needed
2. `dotnet sln add` — unchanged
3. `Directory.CreateDirectory` ×3 — unchanged

**No existing embedded resource pattern** in codebase — this introduces it for the first time. Minimal surface: one static utility class, no framework.

**`Microsoft.Build.NoTargets` SDK:** Participates in MSBuild solution builds without compiling C#. Requires `<TargetFramework>` syntactically; the value has no functional effect for this project type. Match project-wide TFM (`net10.0`).

**Incremental `NpmBuild` target** (from reference `WebResources.csproj`): Uses `Inputs`/`Outputs` with a stamp file in `$(BaseIntermediateOutputPath)` — skips `npm run build` when source files are unchanged. Correct MSBuild incrementality pattern.

**Existing utility classes:** `src/Flowline/Utils/` contains `PacUtils`, `DotNetUtils`, `ConsoleHelper` — all static. `TemplateWriter` follows the same pattern.

---

## Key Technical Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Template storage | Embedded resources in Flowline assembly | No external deps, versions with CLI, readable git diffs |
| Template format | Literal files, no token substitution | Current templates have no per-solution variables |
| Resource logical names | `Flowline.Templates.WebResources.<filename>` | Clean namespace; extensible for future types (e.g., `Flowline.Templates.Plugins.*`) |
| EmbeddedResource declaration | Explicit `<LogicalName>` per item in Flowline.csproj | Avoids path-separator differences between Windows/Linux in manifest names |
| npm install at clone time | Not run | Slow; requires npm in PATH; not Flowline's responsibility |
| Process spawn removal | Eliminated for project creation | Template write is simpler, faster, no dotnet SDK dependency for this step |

---

## Implementation Units

### U1. Add WebResources template files as embedded resources

**Goal:** Create the 5 template files and register them in `Flowline.csproj` as embedded resources with explicit logical names.

**Requirements:** R1, R2, R4

**Dependencies:** none

**Files:**
- `src/Flowline/Templates/WebResources/WebResources.csproj` (new)
- `src/Flowline/Templates/WebResources/package.json` (new)
- `src/Flowline/Templates/WebResources/rollup.config.mjs` (new)
- `src/Flowline/Templates/WebResources/tsconfig.json` (new)
- `src/Flowline/Templates/WebResources/eslint.config.mjs` (new)
- `src/Flowline/Flowline.csproj` (modify — add `<EmbeddedResource>` ItemGroup)

**Approach:** Each template file contains the exact content from the SpotlerAutomate reference. `WebResources.csproj` template uses `Sdk="Microsoft.Build.NoTargets/3.7.134"`, `<TargetFramework>net10.0</TargetFramework>`, an `<ItemGroup>` with `<NpmInputFiles>` glob, and a `NpmBuild` target with `BeforeTargets="Build"`, `Inputs="@(NpmInputFiles)"`, `Outputs` pointing to a stamp file in `$(BaseIntermediateOutputPath)`. In `Flowline.csproj`, add one `<EmbeddedResource>` entry per file with `<LogicalName>Flowline.Templates.WebResources.<filename></LogicalName>`.

**Test expectation:** none — scaffolding/registration; verified below.

**Verification:** `dotnet build src/Flowline/Flowline.csproj` succeeds. Enumerating `typeof(CloneCommand).Assembly.GetManifestResourceNames()` at runtime (or via a quick test) shows all 5 logical names present.

---

### U2. Add TemplateWriter utility

**Goal:** Static utility that loads an embedded resource by logical name and writes its content to a target file path.

**Requirements:** R4

**Dependencies:** U1

**Files:**
- `src/Flowline/Utils/TemplateWriter.cs` (new)
- `tests/Flowline.Tests/Utils/TemplateWriterTests.cs` (new)

**Approach:** Single static class with one `public static async Task WriteAsync(string logicalName, string targetPath)` method. Loads resource stream from `typeof(TemplateWriter).Assembly.GetManifestResourceStream(logicalName)`. If stream is null (resource not found), throws `FlowlineException` — this is a code bug, not a user error. Creates target file's parent directory if absent. Writes stream content via `FileStream`. No token substitution.

**Patterns to follow:** Static utility pattern from `PacUtils`, `DotNetUtils` in `src/Flowline/Utils/`. `FlowlineException` for precondition failures (see `src/Flowline/Commands/CloneCommand.cs`).

**Test scenarios:**
- Given a logical name that exists in the assembly, `WriteAsync` creates the file at the target path with content matching the embedded resource
- Given a target path whose parent directory does not exist, the directory is created before writing
- Given a logical name that does not exist in the assembly, `WriteAsync` throws `FlowlineException`
- Written file content is byte-for-byte identical to the embedded resource stream

**Verification:** `dotnet test tests/Flowline.Tests/` green for `TemplateWriterTests`.

---

### U3. Refactor SetupWebResourcesProjectAsync

**Goal:** Replace `dotnet new classlib` + `File.Delete("Class1.cs")` with `TemplateWriter`-based file generation.

**Requirements:** R1, R2, R3, R5, R6

**Dependencies:** U1, U2

**Files:**
- `src/Flowline/Commands/CloneCommand.cs` (modify — `SetupWebResourcesProjectAsync()`, ~lines 324–366)

**Approach:** Remove the `dotnet new classlib --name WebResources` CliWrap call. Remove `File.Delete("Class1.cs")`. Add 5 `await TemplateWriter.WriteAsync(...)` calls, one per template file, targeting paths under `<slnFolder>/WebResources/`. The WebResources directory must exist before writing — create it explicitly (or it is created as a side effect of the first `Directory.CreateDirectory` on a subfolder; prefer explicit). Keep `dotnet sln add` call unchanged. Keep 3 `Directory.CreateDirectory` calls for `src/`, `public/`, `dist/` unchanged.

**Patterns to follow:** Existing CliWrap + console output pattern in `SetupWebResourcesProjectAsync()`. Console steps via `IAnsiConsole` methods (`.Ok()`, `.Info()`, `.Verbose()`). Tone-of-voice rules in `docs/tone-of-voice.md`.

**Test scenarios:**
- After `flowline clone`, `WebResources/` contains: `WebResources.csproj`, `package.json`, `rollup.config.mjs`, `tsconfig.json`, `eslint.config.mjs`
- After `flowline clone`, `WebResources/src/`, `WebResources/public/`, `WebResources/dist/` directories exist
- `WebResources/WebResources.csproj` contains `Sdk="Microsoft.Build.NoTargets"` — not `Microsoft.NET.Sdk`
- `WebResources.csproj` is registered in `<SolutionName>.sln` (verify with `dotnet sln list`)
- No `Class1.cs` anywhere in `WebResources/`
- `dotnet build solutions/<SolutionName>/WebResources/WebResources.csproj` exits 0 when npm is installed; NpmBuild target executes

**Verification:** Run `flowline clone` against a test Dataverse environment. Inspect generated file tree. Open solution in Rider — WebResources project shows as NoTargets type, not C# class library. `dotnet sln list` includes `WebResources/WebResources.csproj`.

---

## Deferred Implementation Notes

- Exact logical name constant placement (inline strings vs. a `TemplateNames` static class) — implementer's judgment; prefer constants if 3+ call sites reference the same name.
- Whether `TemplateWriter` warrants an interface for testability — current design is static; add interface only if test isolation requires it.
- Behavior of `NpmBuild` target when `npm` is not in PATH — currently fails at build time, not at scaffold time. Known limitation; no action now.
