---
title: Project Layout and Naming Convention - Plan
type: refactor
date: 2026-07-20
topic: project-layout-naming
artifact_contract: ce-unified-plan/v1
artifact_readiness: implementation-ready
product_contract_source: ce-brainstorm
execution: code
planned: 2026-07-20
---

# Project Layout and Naming Convention - Plan

## Goal Capsule

- **Objective:** Scaffolded projects use role-based folder names and solution-identity project-file names — `Solution/DWE_Base.cdsproj`, `Plugins/DWE_Base.Plugins.csproj`, `WebResources/DWE_Base.WebResources.csproj` — with Flowline placing `pac`'s output rather than renaming it.
- **Product authority:** direct discussion with the maintainer; design rationale and counter-arguments in `docs/others/folder-structure-analysis.md` §4.1–§4.2.
- **Open blockers:** Two, both sequencing. Ships in v1.0 (2026-08-01) as the last of three, which is what keeps KD6 true — v1.0 scaffolds the final layout, so no project is ever created on the old shape. `docs/plans/2026-07-19-001-feat-sln-add-slnx-support-plan.md` ships first and owns the shared solution-file reader (its KD8), which KD5 consumes rather than reimplementing. Solution-file discovery must then cover all three project types before this can land. `docs/plans/2026-07-15-001-feat-multi-plugin-project-support-plan.md` establishes the principle (its KD1/KD3) for plugin projects and scopes the `WebResources`/`Package` extension to "a separate plan" — this is that plan, and it carries both the discovery extension and the naming change together, because the naming change is what makes the extension necessary.

## Product Contract

### Summary

Flowline currently hardcodes three folder names as `const` fields and renames `pac`'s generated output to match them. This plan replaces that with a single rule — **Flowline owns folder names, `pac` owns project-file names** — so that the compiled assembly and plugin package carry the Dataverse solution's identity instead of the anonymous name "Plugins".

Target layout:

```
ProjectRoot/
├── DWE_Base.slnx                      ← or .sln below the SDK floor
│
├── Solution/
│   ├── DWE_Base.cdsproj               ← pac's own file, unrenamed
│   └── src/                           ← unpacked solution XML
│
├── Plugins/
│   └── DWE_Base.Plugins.csproj        ← pac's own file, unrenamed
│
├── WebResources/
│   └── DWE_Base.WebResources.csproj
│
├── artifacts/
└── tests/
```

### Problem Frame

Two problems, one root cause.

**The assembly name is anonymous where it matters most.** `Plugins/Plugins.csproj` produces `Plugins.dll` with `<PackageId>Plugins</PackageId>`. That name escapes the repository into Dataverse's assembly and plugin-package lists, plugin trace logs, exception stack traces, and build output — none of which are scoped to one project. A production stack trace naming `Plugins.dll` identifies nothing; `DWE_Base.Plugins.dll` identifies the client and the solution. Inside the repo the folder `Plugins/` was never ambiguous, so the entire payoff of identity naming is extra-repo.

**Flowline maintains a private renaming of vendor output.** `pac solution clone` emits `<SolutionName>/<SolutionName>.cdsproj` plus `src/`; `CloneCommand.cs:366-370` moves the folder to `Package/` and renames the file to `Package.cdsproj`. `pac plugin init` derives its project name from the directory it runs in, and Flowline runs it in `Plugins/` to force the generic name. In both cases Flowline is fighting the tool to reach a convention that is less informative than the one `pac` produces by default.

The root cause of both is that the layout is welded into `const` fields (`FlowlineCommand.cs:25-29`) and detection depends on those names, so `pac`'s natural output cannot be kept.

### Key Decisions

- **KD1 — Folders are role-based and fixed; project files are identity-based.** `Solution/`, `Plugins/`, `WebResources/` answer "what kind of thing lives here?", which has the same answer in every Flowline repo — so the same names in every repo, and one teachable layout. Project files answer "which solution does this belong to?", which differs per repo and escapes into Dataverse. Identity goes only where it escapes.

- **KD2 — Mechanism is init-into-solution-named-directory, then rename the directory. Never rename the project file.** Verified against `pac` 2.9.3: `pac plugin init` takes no `--name` and derives everything from its working directory. Run in `DWE_Base.Plugins/`, it writes `<PackageId>DWE_Base.Plugins</PackageId>` and `namespace DWE_Base.Plugins` into the generated `Plugin1.cs`/`PluginBase.cs`, but writes **no `<AssemblyName>` and no `<RootNamespace>`** — both fall back to the MSBuild default, which is the `.csproj` filename.

  Two alternatives were tested and rejected:

  | | `AssemblyName` (from filename) | `PackageId` | Source namespaces | Content edits |
  |---|---|---|---|---|
  | Rename folder **and** `.csproj` | ✗ reverts to `Plugins` | ✓ | ✓ | none |
  | Generic `Plugins.csproj` + explicit properties | ✓ | needs setting | ✗ stays `Plugins` | several, incl. `.cs` files |
  | **Rename folder only** | ✓ | ✓ | ✓ | **none** |

  Renaming both is the trap: the assembly name silently reverts to `Plugins` while `PackageId` and the namespaces stay prefixed, leaving three identities disagreeing with nothing to signal it. Setting `<RootNamespace>` does not fix namespaces — it affects files an IDE template generates later, not existing declarations — so that route requires rewriting `pac`'s generated `.cs` files.

- **KD3 — `Package/` becomes `Solution/`, flat: the cdsproj and `src/` sit directly inside.** Not nested as `Solution/<SolutionName>/`. Flat is what one `Directory.Move` of `pac`'s output produces, and keeps paths shorter (unpacked solutions carry long `Other/Customizations/…` paths and Windows `MAX_PATH` is a known irritant). Contoso's nested `solution/<SolutionName>/` earns its level with three solutions; at N=1 it buys nothing. Multi-solution repos get their nesting from domain folders instead (`docs/others/folder-structure-analysis.md` §5).

- **KD4 — `WebResources` takes the prefix for symmetry only, and this is stated rather than dressed up.** `WebResources.csproj` is Flowline's own template, not `pac` output, and it is `Microsoft.Build.NoTargets` — it compiles nothing and produces no assembly, so no name escapes the repo and KD1's justification does not apply to it. It takes the prefix because the rule then has no exception and because a solution-named node is easier to pick out in an IDE with several projects open. Cost is zero: a template written to a parameterised filename.

- **KD5 — Solution-file-membership discovery extends to all three project types, and is a hard prerequisite.** Detection cannot depend on `PackageName`/`PluginsName`/`WebResourcesName` once the files are solution-named. All three are already registered in the generated solution file (`CloneCommand.cs:387-450`, `:501-509`, `:545-552`), so it already records the layout. Reading it is the shared reader's job (solution-file-wiring plan, KD8) and both formats are covered there; this plan supplies only the per-type resolution on top. This is the extension the multi-plugin-project plan scoped out; it is in scope here because the rename forces it.

- **KD6 — No backward compatibility and no migration tooling.** Flowline has no installed base depending on the current layout, so the change is made outright rather than behind detection-of-both-shapes. Consistent with the standing position that repository restructuring is the owner's judgement call, not a Flowline command.

- **KD7 — Dotted project and assembly names are accepted as safe.** `DWE_Base.Plugins.csproj` / `DWE_Base.Plugins.dll` put a dot in the project name. This is not a risk here: Flowline does not use `pac solution pack --map`, so the file-identity/name-mangling constraints that make dots hazardous in mapping workflows do not apply (see `docs/others/folder-structure-analysis.md` §2), and dotted assembly names are proven to register in Dataverse without issue. `pac` itself generates this shape.

### Requirements

**Scaffolding**
- R1. `clone` produces `Solution/<SolutionName>.cdsproj` and `Solution/src/` by moving `pac solution clone`'s output directory to `Solution/`, leaving the `.cdsproj` filename as `pac` wrote it.
- R2. `clone` produces `Plugins/<SolutionName>.Plugins.csproj` by running `pac plugin init` in a directory named `<SolutionName>.Plugins`, then renaming that directory to `Plugins`. No file inside is renamed or edited for naming purposes.
- R3. `clone` produces `WebResources/<SolutionName>.WebResources.csproj` from the existing embedded template, written to a parameterised filename.
- R4. All three projects are added to the solution file under their actual filenames, in whichever format `clone` produced (`.slnx` by default, `.sln` under the opt-out — solution-file-wiring plan, R7).

**Discovery**
- R5. Commands resolve the solution package folder, the plugin project(s), and the WebResources project from `.sln` membership rather than from the `PackageName`/`PluginsName`/`WebResourcesName` constants.
- R6. The cdsproj path is resolved by locating the `.cdsproj` entry in the `.sln` rather than by composing `{PackageName}.cdsproj` — today's pattern at `CloneCommand.cs:65`, `SyncCommand.cs:61`, `DeployCommand.cs:368`.
- R7. The three `const` fields at `FlowlineCommand.cs:25-27` are removed; `PackageFolder()` resolves the `Solution/` folder from the discovered `.cdsproj` location.

**Behaviour preservation**
- R8. `Solution/src/` remains the unpacked-solution location — `pac solution clone`/`sync` emit `src/`, and `HasManagedContent` reads it. Unchanged.
- R9. Drift checking, packing, deploy, and version reading operate against the renamed folder with no behavioural change (`PluginWebResourceDriftChecker`, `PacUtils.PackSolutionAsync`, `ReadLocalSolutionVersion`).
- R10. `dotnet build` at the project root still builds all three projects and packs the snapshot, per the no-`ProjectReference` property in `docs/others/folder-structure-analysis.md` §3.

**Documentation**
- R10a. The project-structure block and guidance prose that `clone` scaffolds into the user's repo (`CloneCommand.cs:182-199`, plus the `Package/src/` references in the surrounding rules text) are regenerated from the actual layout and project filenames, not hardcoded. This is user-visible output, not internal docs, and is currently the only place the literal strings `Package/Package.cdsproj`, `Plugins/Plugins.csproj`, and `WebResources/WebResources.csproj` are written into someone else's repository.
- R11. `docs/folder-structure.md` describes the new layout and the KD1 rule, replacing the fixed-name convention.
- R12. Wiki pages describing project structure are updated: `Getting-Started.md`, `WebResources-Project.md`, `Push-Plugins-and-Custom-APIs.md`.

### Acceptance Examples

- AE1. **Clone of solution `DWE_Base`.** Produces `DWE_Base.slnx` (or `DWE_Base.sln` under the opt-out), `Solution/DWE_Base.cdsproj`, `Solution/src/`, `Plugins/DWE_Base.Plugins.csproj`, `WebResources/DWE_Base.WebResources.csproj`. `Plugin1.cs` declares `namespace DWE_Base.Plugins`; the csproj carries `<PackageId>DWE_Base.Plugins</PackageId>`. Covers R1–R4.
- AE2. **Build output identity.** `dotnet build` on the scaffolded project emits `DWE_Base.Plugins.dll`, not `Plugins.dll` — confirming `AssemblyName` follows the unrenamed `.csproj` filename with no explicit property set. Covers R2, KD2.
- AE3. **Ugly solution name.** A solution named `DWE_Base` (leading publisher prefix, underscore) scaffolds without sanitisation, producing valid project and assembly names. Covers R1–R3.
- AE4. **Discovery after a manual move.** A user relocates `Plugins/` to `src/Plugins/` and updates the solution file in their IDE. All commands still find the project. Covers R5.
- AE4b. **The package folder relocates too.** A user moves `Solution/` to `src/Package/` and updates the solution file. Sync, deploy, drift, and status all resolve both the `.cdsproj` *and* the folder — so `src/` is read, packed, and drift-checked from the new location, not from a stale `Solution/`. Covers R5, R7, KTD3. *(Added during implementation: the original KTD3 would have left the folder composed while the cdsproj resolved, and no acceptance example existed that would have caught the resulting half-break.)*
- AE5. **Push registers the identity-named assembly.** `flowline push` registers `DWE_Base.Plugins` as the `pluginassembly` name in Dataverse, and plugin types register under `DWE_Base.Plugins.*`. Covers R2, R5.
- AE6. **Root build still packs the snapshot.** `dotnet build` at the root builds all three projects and produces the zip from `Solution/src/`, with no `ProjectReference` from the cdsproj. Covers R10.

### Scope Boundaries

- **Adopting `src/` for code projects** (`src/Plugins/`, `src/WebResources/`) — out of scope. Analysed and deferred to a future mid-size restructure; see `docs/others/folder-structure-analysis.md` §4 Option B. This plan keeps the flat root.
- **Multi-solution / domain-segmented layouts** (`src/<Domain>/`) — out of scope; documented as a target shape only, with no tooling (§5).
- **Migration of existing projects and any migration command** — out of scope per KD6.
- **Per-project naming overrides or a configurable project-name setting** — rejected, not deferred. The solution name is derived, not configured; adding a naming knob would reintroduce the config surface that `.sln`-driven discovery exists to remove (§4.1).
- **Repository-name-derived prefixes** (`CustomerPortal.Plugins` where the solution is `DWE_Base`) — rejected. It reads better when solution names are poor, but requires Flowline to track and reconcile a second identity, and would require rewriting `pac`'s generated files rather than accepting them.
- **Changing `src/` inside `Solution/`** — out of scope; `pac` owns that name (R8).
- **Retro-editing historical plans.** Fourteen plans under `docs/plans/` reference `Package/` as the then-current layout. They are records of decisions taken at a point in time and are not rewritten; only `docs/folder-structure.md`, the wiki, and the scaffolded output (R10a–R12) describe the live layout.

### Outstanding Questions

**Deferred to Planning:**
- Whether `clone` should scaffold into `<SolutionName>.Plugins/` and rename, or run `pac plugin init --outputDirectory` against a temporary directory and move the contents. Both satisfy KD2; the first is fewer operations, the second avoids a transient directory at the project root if `clone` fails mid-run.
- **RESOLVED — a solution name can be a C# keyword, and that breaks the scaffold.** Verified rather than inferred:

  **What Dataverse allows.** `solution.uniquename` is `[A-Za-z0-9_]`, first character a letter or underscore, `MaxLength: 65`, enforced server-side via the `InvalidSolutionUniqueName` platform error `0x8004F002` ([web service error codes](https://learn.microsoft.com/power-apps/developer/data-platform/reference/web-service-error-codes#microsoft-dataverse-errors)). **No reserved-word blocklist is documented.** A name cannot start with a digit, so `123Foo` is impossible — but `event`, `class`, `int`, `string`, `object`, `lock` and `params` are all legal solution names. The character set cannot protect us here: C# keywords are a strict subset of what Dataverse accepts.

  **What breaks.** Measured on SDK 10 / `pac` 2.9.3:

  | Namespace | Result |
  |---|---|
  | `event.Plugins` | `error CS1001: Identifier expected` |
  | `@event.Plugins` | builds |
  | `DWE_Base.Plugins`, `_123.Plugins` | build |

  Running `pac plugin init` in a directory named `event.Plugins` reports *"Dataverse plug-in class library ... created successfully"*, writes `namespace event.Plugins` unescaped into `Plugin1.cs` and `PluginBase.cs`, and the project does not compile. So KD2's mechanism inherits the failure directly, and the user sees a parser error in generated code they never wrote, after a command that claimed success.

  **Why the cheap fix does not fit.** A verbatim identifier (`@event`) compiles, but applying it means editing `pac`'s generated `.cs` files — and KD2 exists precisely so that nothing Flowline does edits or renames what `pac` produced. Naming the directory `@event.Plugins` would make `pac` emit a valid namespace, but it is a trick with no signpost.

  **Direction for planning:** validate the solution name against the C# keyword list *before* invoking `pac plugin init`, and fail with a message naming the keyword and the collision. Consistent with the refuse-rather-than-guess stance taken on discovery. Only `clone` needs the check — an existing project already has its names. Left to planning: whether the same check belongs on the `.cdsproj`/`.slnx` filenames (Windows reserved device names — `CON`, `PRN`, `AUX`, `NUL`, `COM1`-`COM9` — are legal Dataverse names and illegal filenames, and unlike the keyword case this one predates the plan).

- **RESOLVED — an underscore in the solution name is kept verbatim.** `DWE_Base` scaffolds `Plugins/DWE_Base.Plugins.csproj`, `namespace DWE_Base.Plugins`, and `DWE_Base.Plugins.dll`. Nothing is stripped, replaced, or PascalCased.

  This is the same question as the keyword case one level down — how much a tool should edit a name it did not choose — and it lands the other way, because an underscore breaks nothing.

  **Measured.** `namespace DWE_Base.Plugins` builds clean on a default `pac plugin init` project. The only objection is `CA1707: Remove the underscores from namespace name 'DWE_Base'`, and it fires **only** under `<AnalysisMode>All</AnalysisMode>` — the default build emits nothing, and `pac plugin init` scaffolds no analyzer settings. It is a warning, on a rule a team opting into full analysis can suppress in one line.

  **Why verbatim wins:**
  - *Round-tripping is the point.* The rename exists so `DWE_Base.Plugins.dll` in a 2am stack trace names the solution. Strip to `DWEBase.Plugins` and mapping back is guesswork — nothing records where the underscore was.
  - *Transformation collides.* `DWE_Base` and `DWEBase` are two distinct, legal Dataverse solutions that both become `DWEBase.Plugins.dll`. That is anonymous assembly identity — the defect this plan exists to remove — reintroduced by the fix for it.
  - *The underscore carries meaning.* `DWE_Base` reads as publisher `DWE` plus solution `Base`. `DWEBase` loses the boundary.
  - *Consistency with KD2.* The plan's spine is that `pac` owns project-file names and Flowline owns folders. Editing `pac`'s output was rejected; editing the input handed to `pac` is the same meddling one step earlier, and a transformation rule is something to specify, test and explain forever.

  **The honest cost.** .NET naming convention does say no underscores in identifiers, so `DWE_Base.Plugins` reads as non-idiomatic to a C# developer, and a shop running `AnalysisMode.All` gets a per-file warning until it suppresses CA1707 — friction Flowline introduced by putting the solution name there at all.

  **Rejected middle option:** `DWE.Base.Plugins` (underscore to dot) is idiomatic and satisfies CA1707, but is still lossy (`DWE.Base` and `DWE_Base` both map to it) and invents namespace nesting the user never asked for.

  **If the CA1707 friction proves real**, the fix is for `clone` to write `<NoWarn>CA1707</NoWarn>` into the scaffolded csproj with a comment explaining why — keeping the name faithful and silencing the noise at its source. Deliberately not done pre-emptively: no user has hit it, and a suppression nobody needed is its own small lie about the code.

---

## Planning Contract

**Product Contract preservation:** unchanged. KD5's wording widened from "`.sln`" to "solution file" per the wiring plan's R9a.

### Key Technical Decisions

- **KTD1 — "Package" is overloaded in this codebase; a blind rename sweep will corrupt it.** Three distinct meanings share the word, and only the first is being renamed:

  | Meaning | Where | Rename? |
  |---|---|---|
  | The `Package/` folder holding the unpacked solution | `FlowlineCommand.cs:25`, path literals, docs | **Yes → `Solution/`** |
  | A **Dataverse plugin package** (`pluginpackage`, the NuGet shape) | `src/Flowline.Core/Plugins/PluginService.cs:316,428,429,589,601` | **No — unrelated concept** |
  | `PackageSrcRoot` as a C# identifier | `Flowline.Core/OrphanCleanup/DetectionContext.cs:10,12`, `Services/IPostDeployService.cs:17,20,29`, `OrphanCleanupService.cs:178,322,471`, 5 handler files | Optional rename; nothing breaks, but the name misleads once the folder is `Solution/` |

  Also do not touch: the `Flowline.Core.Plugins` / `Flowline.Core.WebResources` namespaces, the `metadata.Plugins` collection property (`PluginAssemblyMetadata.cs:11`, `PluginPlanner.cs:42,53,85,454`, `PluginReader.cs:215,243,272`), or the `Package/src/WebResources` vs `WebResources/` distinction.

- **KTD2 — Core needs no changes; this is a `Flowline`-project refactor.** `Flowline.Core` is already path-parameterized — `OrphanCleanupService`, `ComponentClassifier`, and the handlers all take `packageSrcRoot` as an argument rather than composing it. Confirmed by the tests: `tests/Flowline.Core.Tests/**` passes fresh GUID temp dirs and mentions `Package/src` only in prose comments. The blast radius is `src/Flowline/` plus `tests/Flowline.Tests/`.

- **KTD3 — `PackageFolder()` is resolved, not composed — `Solution/` relocates exactly like `Plugins/`.** *(Reversed during implementation. The original KTD3 kept the helper a constant-derived static on the grounds that `Solution/` is a fixed role-based name, and contradicted R7. R7 is correct; the static is retired.)*

  Implementing U4 exposed why the original was untenable. Resolving the cdsproj *path* from solution-file membership while leaving the *folder* a composed constant produces the worst of both: relocate `Solution/` to `src/Package/`, update the solution file, and Flowline resolves your cdsproj correctly and then reads `src/` from a folder that is not there. Silent half-break — the failure mode this project rejects on principle. Either both resolve or neither does, and R5 already requires the cdsproj to.

  The call sites split by kind, and conflating them was the original error:

  - **Clone authors the layout.** `CloneCommand.cs:418` `Directory.Move`s `pac`'s output *into* the folder — it is what creates it, so there is nothing to resolve from. Every `PackageFolder(` in `CloneCommand.cs` stays a composed literal behind a helper named for authorship (not discovery), which is the one place permitted to name the folder. Same reasoning already recorded inline for `cdsprojPath` at `CloneCommand.cs:80`.
  - **Every later command reads it** — sync, deploy, drift, status — and resolves via `ProjectLayoutResolver.ResolvePackageFolderAsync`, once per invocation into a local rather than per use.

  `FlowlineCommand.PackageFolder` is deleted. After this no shared helper composes the package folder from a fixed name, which is what R7 asked for all along.

- **KTD4 — Nothing currently globs for projects, so nothing silently keeps working.** There is no dynamic `*.csproj`/`*.cdsproj` search anywhere in `src/**`; every project path is composed via `Path.Combine` with a constant or literal. That is precisely the coupling being replaced, and it means the compiler finds the call sites when the constants are deleted — the removal is compiler-enforced rather than grep-hoped.

- **KTD5 — Scaffolding renames directories, never files.** Verified against `pac` 2.9.3: `pac plugin init` derives everything from its working directory, writing `<PackageId>` and the generated `namespace` declarations from the folder name, but no `<AssemblyName>` or `<RootNamespace>` — those fall back to the `.csproj` filename. So renaming the file reverts the assembly name while `PackageId` and namespaces stay prefixed, leaving three identities disagreeing. `<RootNamespace>` cannot rescue it either: it affects files an IDE template generates later, not existing declarations. Init into `<SolutionName>.Plugins/`, rename the directory to `Plugins/`, touch nothing inside.

- **KTD6 — Caller sweep and test migration are their own units.** The repo has two documented incidents where an identity-key change under-enumerated its consumers (`docs/solutions/design-patterns/extending-identity-key-plan-files-list-incomplete.md`, `promoting-field-to-identity-key-changes-edit-semantics.md`). Project identity is moving from folder constants to solution-file membership, so the sweep (U6) and the ~13 affected test files (U7) are visible work rather than incidental edits inside other units.

- **KTD7 — Templates are safe; only the output filename is parameterized.** The seven `Templates/WebResources/` files carry no folder-name coupling in content, `rollup.config.mjs` derives output names dynamically, and the `<EmbeddedResource>` entries in `Flowline.csproj:21-43` are individual includes whose `Flowline.Templates.WebResources.*` logical names are independent of the scaffolded folder. Only `TemplateWriterTests.cs:19` asserts a logical name. So R3 changes the destination filename, not the template subtree.

### Patterns to Follow

- `src/Flowline/Commands/CloneCommand.cs:366-370` — the existing `Directory.Move` shape U1 retargets
- `src/Flowline/Commands/DriftCommand.cs` — `internal static` helper extraction for testability
- `docs/tone-of-voice.md` — every touched CLI string

### Risks

- **User-facing CLI strings are quoted verbatim in `docs/solutions/`.** `architecture-patterns/ai-agent-consumable-cli-contract-2026-06-07.md:165,217,239` reproduces help text and error strings; changing those messages invalidates the quotes. Six `docs/solutions/` files reference the layout in total.
- **`CONCEPTS.md:76-77` defines "Package folder" as domain vocabulary** — this is a glossary term, not incidental prose, so the rename changes a defined concept rather than a path string.
- **Assembly rename is a Dataverse identity change.** `Plugins.dll` → `DWE_Base.Plugins.dll` changes the assembly *name*; `DetectIdentityChanges` looks assemblies up by name (`PluginService.cs:778-786`), so a renamed assembly reads as new. Only bites an org already carrying a `Plugins.dll`; `--force recreate-assembly` covers it and steps re-derive from `[Step]` attributes.

---

## Implementation Units

### U1. `Package/` becomes `Solution/`, and clone stops renaming the cdsproj

**Goal:** `pac solution clone` output is placed, not rewritten.
**Requirements:** R1, KD3, KD5.
**Dependencies:** wiring plan U6 (clone writes solution entries directly).
**Files:** `src/Flowline/Commands/CloneCommand.cs`, `src/Flowline/Commands/FlowlineCommand.cs`, `tests/Flowline.Tests/CloneCommandTests.cs`
**Approach:** Delete the `File.Move` at `CloneCommand.cs:369` and retarget the `Directory.Move` to `Solution/`, leaving the `.cdsproj` under the name `pac` gave it. Rename the `PackageName` constant's value and `PackageFolder()` accordingly (KTD3).
**Test scenarios:**
- After clone, `Solution/<SolutionName>.cdsproj` and `Solution/src/` exist and no `Package/` folder is created.
- The cdsproj filename matches the Dataverse solution unique name exactly, including an underscore-bearing name like `DWE_Base`.
- `HasManagedContent` reads `Solution/src` correctly (its existing behaviour, new path).
- Re-running clone on an existing project is still a no-op.
**Verification:** clone produces the target tree with no rename step in the code path.

### U2. Plugins project scaffolds with solution identity

**Goal:** The built assembly is `<SolutionName>.Plugins.dll`.
**Requirements:** R2, KD1, KD2.
**Dependencies:** U1.
**Files:** `src/Flowline/Commands/CloneCommand.cs`, `tests/Flowline.Tests/CloneCommandTests.cs`
**Approach:** Create `<SolutionName>.Plugins/`, run `pac plugin init` there, then `Directory.Move` it to `Plugins/`. Nothing inside is renamed or edited (KTD5). The `File.Exists` guard at `CloneCommand.cs:455` currently expects `Plugins/Plugins.csproj` and must expect the new filename.
**Execution note:** Assert on the built assembly name, not just the csproj filename — the filename is the mechanism, the assembly name is the requirement.
**Test scenarios:**
- Scaffolded project is `Plugins/<SolutionName>.Plugins.csproj`; the folder is `Plugins/`, not `<SolutionName>.Plugins/`.
- `<PackageId><SolutionName>.Plugins</PackageId>` present in the csproj.
- Generated `Plugin1.cs`/`PluginBase.cs` declare `namespace <SolutionName>.Plugins`.
- **Building the scaffolded project emits `<SolutionName>.Plugins.dll`** — the regression guard for the rename-the-file trap (KTD5).
- A solution name with a leading publisher prefix and underscore (`DWE_Base`) produces a valid project and assembly name without sanitisation.
- Re-running clone skips the already-scaffolded project.
**Verification:** `dotnet build` on a scaffolded project emits the solution-prefixed assembly.

### U3. WebResources project scaffolds with solution identity

**Goal:** Filename symmetry with the other two projects.
**Requirements:** R3, KD4.
**Dependencies:** U1.
**Files:** `src/Flowline/Commands/CloneCommand.cs`, `tests/Flowline.Tests/TemplateWriterTests.cs`
**Approach:** Write the existing embedded template to `WebResources/<SolutionName>.WebResources.csproj`. Template content and logical names are untouched (KTD7). No assembly is produced — this is symmetry, per KD4.
**Test scenarios:**
- Scaffolded file is `WebResources/<SolutionName>.WebResources.csproj` with unchanged content.
- The embedded-resource logical name assertion at `TemplateWriterTests.cs:19` still passes.
- npm build output still lands in `WebResources/dist/`.
**Verification:** root `dotnet build` runs the npm target as before.

### U4. Resolve all three project types from the solution file

**Goal:** Delete the three constants; paths come from solution-file membership.
**Requirements:** R5, R6, R7, KD5.
**Dependencies:** wiring plan U2; multi-plugin plan U1-U3.
**Files:** `src/Flowline/Commands/FlowlineCommand.cs`, `src/Flowline/Commands/CloneCommand.cs`, `SyncCommand.cs`, `DeployCommand.cs`, `DriftCommand.cs`, `StatusCommand.cs`, `tests/Flowline.Tests/FlowlineCommandTests.cs`
**Approach:** Per-type resolution on top of the shared reader: the `.cdsproj` entry identifies the solution folder; plugin projects come from the multi-plugin plan's confirmed set; the WebResources project is identified by project identity. Remove `PackageName`/`PluginsName`/`WebResourcesName` (`FlowlineCommand.cs:25-27`). The compiler enumerates the call sites (KTD4). The three cdsproj-filename compositions (`CloneCommand.cs:65`, `SyncCommand.cs:61`, `DeployCommand.cs:368`) resolve from the solution file instead.
**Test scenarios:**
- The cdsproj is found when the solution folder is `Solution/` and the file is solution-named.
- The WebResources project is found under its new filename.
- A project relocated to a different folder with the solution file updated still resolves (covers R5/AE4) — proves the coupling is genuinely gone.
- A solution file missing the cdsproj entry produces an actionable error rather than a null path.
- Resolution works identically from `.sln` and `.slnx`.
**Verification:** no `const` folder names remain in `src/Flowline/`.

### U5. Regenerate scaffolded guidance from the real layout

**Goal:** What clone writes into the user's repo matches what it built.
**Requirements:** R10a.
**Dependencies:** U1-U3.
**Files:** `src/Flowline/Commands/CloneCommand.cs` (the block at `:182-199` and surrounding prose)
**Approach:** Derive the project-structure block and the `Package/src/` references in the rules text from the actual layout and filenames rather than hardcoding them. This is the only place these literals are written into someone else's repository. The `solutions/<Name>/` nesting sentence at `:182` also needs revisiting against the current single-solution reality.
**Test scenarios:**
- The scaffolded block names the real cdsproj, plugins, and webresources filenames for the cloned solution.
- No occurrence of `Package/` survives in scaffolded output.
- Text passes a `docs/tone-of-voice.md` review.
**Verification:** scaffolded guidance in a fresh clone matches the directory tree beside it.

### U6. Caller sweep and CLI-string audit

**Goal:** No consumer or user-facing message still assumes the old layout.
**Requirements:** KTD6.
**Dependencies:** U4.
**Files:** discovered by sweep — expected `src/Flowline/Utils/PluginWebResourceDriftChecker.cs`, `src/Flowline/Commands/DeployCommand.cs:352,361`, `SyncCommand.cs:73,78,80`, `CloneCommand.cs:286,293,319,458,463,501,512,527`, `StatusCommand.cs:214`
**Approach:** Grep for every remaining literal and every caller of the removed constants, including interpolated CLI strings that render the folder name. Explicitly exclude the KTD1 false positives — `PluginService.cs`'s Dataverse-plugin-package usage, the `Flowline.Core.*` namespaces, and `metadata.Plugins`. Record the enumeration in the PR.
**Execution note:** Sweep before editing and publish the call-site list; the deliverable is a reviewable enumeration, not an assurance.
**Test scenarios:**
- No `src/**` file composes a project path from a hardcoded folder name.
- Touched CLI strings reviewed against `docs/tone-of-voice.md`.
- The KTD1 false-positive sites are confirmed unchanged.
**Verification:** grep produces no unexpected hits; `PluginService.cs` diff is empty.

### U7. Migrate the affected tests

**Goal:** The suite tests the new layout, and the rename's blast radius is visible.
**Requirements:** R9.
**Dependencies:** U1-U4.
**Files:** `tests/Flowline.Tests/FlowlineCommandTests.cs:40-45`, `PluginWebResourceDriftCheckerTests.cs` (whole file), `DeployCommandArtifactCacheTests.cs:130-155`, `GitUtilsTests.cs:123,273-394`, `CloneCommandTests.cs:20,39-64`, `DeployCommandDtapGateTests.cs:175-236,296-309`, `NamespaceDeriverTests.cs:14-20,61-68,93-95`, `Services/DataverseContextGeneratorTests.cs:531`, `TemplateWriterTests.cs:19`
**Approach:** Mechanical path updates, with three that are not mechanical: `FlowlineCommandTests.cs:40-45` unit-tests the naming convention itself and must assert the new one; `NamespaceDeriverTests.cs:61-68` asserts the literal `"Plugins.Models"` and must follow the new project name; `DataverseContextGeneratorTests.cs:531` still builds the obsolete `solutions/MySolution/Package/src` shape and should move to the current root-level layout. `tests/Flowline.Core.Tests/**` needs no change (KTD2).
**Test scenarios:**
- All migrated tests pass against the new layout.
- Drift checking still compares `WebResources/dist` ↔ `Solution/src/WebResources` and plugin assembly sizes.
- Deployment input paths cover the renamed folder and the renamed project files, still excluding docs/tests/markdown.
- Git scoping helpers detect changes under the new paths.
**Verification:** `dotnet test Flowline.slnx` green with no skipped tests.

### U8. Documentation and vocabulary

**Goal:** Docs describe the shipped layout.
**Requirements:** R11, R12.
**Dependencies:** U1-U5.
**Files:** `docs/folder-structure.md`, `CONCEPTS.md:76-77`, `README.md:158`, wiki `Getting-Started.md` / `WebResources-Project.md` / `Push-Plugins-and-Custom-APIs.md`
**Approach:** Update the layout spec, the "Package folder" glossary entry (a defined term, not prose), and the README's `Plugins/Models/` reference. Coordinate with the multi-plugin plan's U6, which also edits `docs/folder-structure.md`. Per the Scope Boundaries, the fourteen historical plans and the six `docs/solutions/` files are **not** retro-edited — but `ai-agent-consumable-cli-contract-2026-06-07.md:165,217,239` quotes CLI strings verbatim, so if U6 changed those messages, note the drift rather than silently leaving stale quotes.
**Test expectation:** none — documentation.
**Verification:** wiki checkout updated, or its unavailability reported per AGENTS.md.

---

## Verification Contract

- `dotnet build Flowline.slnx` clean; `dotnet test Flowline.slnx` green.
- A fresh `clone` of a real solution produces the target tree, and root `dotnet build` emits the zip.
- The scaffolded plugin project builds to `<SolutionName>.Plugins.dll` — the assembly name is the requirement, not the filename.
- `flowline push` registers the solution-prefixed assembly against a real org.
- No `const` folder names remain in `src/Flowline/`; caller-sweep enumeration recorded in the PR.
- `PluginService.cs` is untouched by the rename (KTD1 guard).

## Definition of Done

- Scaffolded projects match the KD-defined layout, with identity on the assembly and role-based folder names.
- The three folder constants are deleted and every path resolves from the solution file.
- `pac` output is placed, never renamed — no `File.Move` of a project file anywhere.
- Tests, scaffolded guidance, `docs/folder-structure.md`, `CONCEPTS.md`, README, and wiki reflect the shipped layout.
- CLI text reviewed against `docs/tone-of-voice.md`.

---

### Sources / Research

- `docs/others/folder-structure-analysis.md` §4.1–§4.2 — full design rationale, seven counter-arguments with responses, and the rejected alternatives.
- `docs/plans/2026-07-15-001-feat-multi-plugin-project-support-plan.md` — KD1/KD3 discovery principle; its Scope Boundaries defer the `WebResources`/`Package` extension to this plan.
- `docs/plans/2026-07-19-001-feat-sln-add-slnx-support-plan.md` — ships first; its KD8 owns the shared solution-file reader consumed by KD5, and its R7 governs whether new projects scaffold `.slnx` or `.sln`.
- `src/Flowline/Commands/CloneCommand.cs:366-370` — the `Directory.Move`/`File.Move` pair that renames `pac solution clone` output; `:455` — `pac plugin init` invocation expecting `Plugins/Plugins.csproj`; `:387-450`, `:501-509`, `:545-552` — all three projects added to the `.sln`.
- `src/Flowline/Commands/FlowlineCommand.cs:25-29` — the three layout constants and `PackageFolder()`.
- `pac` CLI 2.9.3 — `pac plugin init` flag surface (`--signing-key-file-path`, `--outputDirectory`, `--author`, `--skip-signing`; no `--name`) and generated-file contents, verified directly for KD2.
