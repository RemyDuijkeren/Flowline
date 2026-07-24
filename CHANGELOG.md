# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.13.0] - 2026-07-24

### Removed

- **`PreImageAttribute.Name` / `PostImageAttribute.Name`**: get-only duplicates of `Alias` that nothing read. C# forbids setting a read-only property as a named attribute argument, so they could never be assigned in `[PreImage(...)]`/`[PostImage(...)]`, and the metadata scanner only ever read `Alias`. Dataverse's image `name` field is unaffected — it's still written on every registration, defaulting to `"Pre Image"`/`"Post Image"` and following `Alias` when set. Only breaks code that *read* `.Name` off an attribute instance; substitute `.Alias`, which returns the identical value.
- **`HandlesAttribute.IsCustomMessage`**: never read by anything. It was assigned in the constructor body, and the metadata scanner reads attributes via `CustomAttributeData` (constructor arguments only) — so a ctor-body assignment was never visible to it in the first place. Distinguishing a Custom API message from a built-in one is done by inspecting the constructor argument's type instead, which is unaffected. Only breaks code that *read* `.IsCustomMessage` off an attribute instance.
- **Unused package references**: `Microsoft.Extensions.Logging.Console` (no `AddConsole` anywhere — logging goes through Serilog) and three `PackageVersion` entries pinning nothing (`Microsoft.CrmSdk.CoreAssemblies`, `Microsoft.CodeAnalysis.CSharp`, `Microsoft.CodeAnalysis.Analyzers`). No behavior change.

### Added

- **`deploy --dry-run`**: runs every pre-deploy check — DTAP gate, git-clean, local plugin/web-resource drift, packing, the solution checker gate, and the orphan-cleanup report — plus a labeled environment backup (`flowline-dryrun-...`, distinct from a real deploy's `flowline-deploy-...`), then stops before importing. Never calls `pac solution import`, never runs post-import cleanup, and never publishes a CI artifact-publish signal. The first-import confirmation prompt becomes an informational note instead of a block, since dry-run never performs the irreversible action it guards.
- **Multiple plugin projects per solution**: `push` now discovers plugin projects by reading the solution file and reflecting each candidate's build output for `IPlugin`/`CodeActivity` types, rather than assuming one project named `Plugins` producing an assembly named `Plugins` at `bin/Release/net462/publish/`. Each discovered project registers independently — its own assembly, plugin types, steps, and Custom APIs — in a single `push`. Project names, folder locations, a custom `<AssemblyName>`, and non-standard build output paths all work as long as the solution file references the project. A project whose build output reflects cleanly and bears neither type is skipped; `--verbose` reports what was skipped and why. `PluginPackageMode` remains one setting per solution.

- **`clone` also scaffolds `CLAUDE.md`**: `AGENTS.md` alone isn't picked up by Claude Code, which only auto-loads `CLAUDE.md` — clone now writes a one-line `CLAUDE.md` (`@AGENTS.md`) alongside it so Claude Code loads the same instructions. Skipped if `CLAUDE.md` already exists.
- **`flowline sln add <path>`**: wires a `.cdsproj` into the project's solution file. `dotnet sln add` refuses a `.cdsproj` — and exits 0 while doing it, so a script can't detect the failure ([dotnet/sdk#47638](https://github.com/dotnet/sdk/issues/47638)) — so Flowline writes the entry itself. `clone` already does this for projects it creates; the command exists for the ones it didn't, like a repo migrating off spkl, Daxif, or PACX. Runs standalone: no `.flowline`, no git repo, no PAC login. Works with `.sln` and `.slnx`, finds the solution file in the current directory (matching plain `dotnet sln add`'s own scoping — it does not walk upward), and never converts an existing file. It won't create a solution file — with none in the current directory it exits `3` and points at `dotnet new sln`.

- **Guards the active PAC auth profile before every Dataverse call**: `clone`, `deploy`, `drift`, `generate`, `provision`, `push`, and `sync` now check that PAC CLI's currently active `pac auth` profile actually matches the target environment before doing anything — previously a mismatched profile connected silently as whatever identity `pac` happened to have selected. Interactively, a mismatch shows the active and target profile side by side and prompts to switch (default no); declining, or running non-interactively, fails with a `pac auth select --name/--index ...` remediation message. Pass `--auto-select-auth-profile` to switch automatically without a prompt. `deploy` also guards the DTAP predecessor environment's profile before its own `pac.exe solution-list` probe. `flowline status` reports a profile mismatch too, but only as an advisory note — it never blocks. User-facing text now says "PAC auth profile" throughout, not "PAC profile". **Breaking for CI**: a non-interactive run against an environment whose profile isn't currently active now fails instead of silently connecting as the wrong identity — add `--auto-select-auth-profile` to existing pipelines that switch environments without also switching `pac auth select` first.
- **Two-tier fallback for a web resource file with no recognizable type**: a local file with no extension, or an unrecognized one, used to leave `push`/`sync` unable to determine its Dataverse `webresourcetype` at all. Tier 1 resolves it from an existing Dataverse record — checking this solution's own records first, then any other solution's, since a match there means the file is really an update to a record this solution doesn't own. Tier 2 falls back to sniffing the file's own bytes: magic-byte checks for PNG/JPG/GIF/ICO, and for text files a schema/doctype/syntax check for RESX, HTML, and JavaScript — only signals at 90%+ confidence count, so the weaker SVG/XML/CSS heuristics stay disabled. A file neither tier can resolve still fails the push, now with a clear "metadata lookup and content sniffing were both tried and neither resolved a type" message instead of a generic error.

### Changed

- **`clone` scaffolds a `.slnx` by default**: the .NET 10 default format, replacing the classic `.sln`. Flowline previously opted out on the assumption that a `.slnx` can't hold a `.cdsproj` — it can (verified on SDK 10.0.302: `dotnet sln list` enumerates the entry and `dotnet build` runs SolutionPackager through to the zip). Only `dotnet sln add` refuses a `.cdsproj`, and Flowline writes that entry itself now. Existing projects are untouched — Flowline reads both formats and never converts one. A project that already has a `.sln` keeps it — clone writes into whatever solution file is there, and only creates one when there is none.

- **Exactly one Dataverse solution per project, always at the root (BREAKING CHANGE!)**: Flowline no longer supports multiple solutions in one project. `.flowline`'s `Solutions` array collapses to a single `Solution` object (plus a required `SchemaVersion`); `deploy`, `sync`, and `drift` drop their solution-name selector entirely, and `push`/`generate`'s selector now validates against the configured solution instead of picking among several. An old multi-solution `.flowline` fails fast on load, naming the fix: delete the project's config and its `solutions/<Name>/` folder, then `flowline clone <solution>` again — there's no auto-migration. Alongside this, the `Solution/`, `Plugins/`, and `WebResources/` projects are now located by reading the `.slnx`/`.sln` file itself instead of assuming fixed folder names — renaming or moving any of them no longer breaks `clone`/`push`/`sync`/`deploy`, and `clone` now recognizes a project that's already registered even if it moved. A project with no WebResources project is no longer a hard error: `push`, `sync`, and `deploy` now warn and skip the web-resource side instead of blocking (a genuine ambiguous match between two candidate projects still fails). A PowerApps Component Framework project sitting alongside the real WebResources project is no longer misdetected as it.

- **`push` no longer requires a plugin project on a WebResources-only solution**: under the default scope, zero discovered plugin projects is a valid, common case — it only failed with "No plugin project found" when the user explicitly asked for one via `--scope plugins`/`--scope assemblyonly`.

### Fixed

- **`push` refuses to guess a project out of discovery**: the discovered project set also decides what the orphan sweeps treat as having local source, so a project silently dropped from discovery had its live assembly, steps, and Custom APIs deleted — and the Custom API sweep isn't gated by `--force`. Dropping is now allowed only when it's certain. A project whose build output reflects cleanly and carries no `IPlugin`/`CodeActivity` type is still skipped silently (the common case: WebResources, test projects, shared libraries). A project Flowline *couldn't* classify fails the push instead, naming the project: when no assembly in its output could be reflected at all (usually `Microsoft.Xrm.Sdk.dll` isn't copy-local, which hides a real plugin project's base types), or when `--no-build` left nothing in `bin/Release` to reflect. The cheap csproj-text pre-filter now defers to reflection whenever it can't be confident — a `ProjectReference` or a `Directory.Build.props` above the project means the SDK reference may never appear in the csproj itself. Standalone mode (`--pluginFile`) runs no discovery and is unaffected.

- **`push` only deletes a Custom API it can prove is its own**: the Custom API sweep treated "this API's plugin type isn't one I recognise" as "this API is orphaned" and deleted it as part of the normal plan, no `--force delete-orphans` involved. Two things made that wrong. A Custom API *references* a plugin type as its implementation (`customapi.plugintypeid` points down), unlike a step, which is *owned by* its type — so losing an implementation doesn't orphan the contract. And a Custom API has no assembly-shaped handle, so Flowline can only find them publisher-wide: the sweep's input was every Custom API the publisher ever created, including other projects' and other repos'. A Custom API is public API surface with external callers, so the bar to delete one is now higher than for a step, not lower.

  What changes: an API whose plugin type belongs to this push and is no longer declared in source is still deleted on a normal push — unchanged. An API whose plugin type isn't one this push owns is **never** deleted, with or without `--force`; `--verbose` says why it was left. An API with no `plugintypeid` at all — which may be a contract waiting for an implementation — now needs `--force delete-orphans`, the same gate the orphan assembly and step sweeps use. This supersedes the earlier fix that protected a sibling project's Custom APIs by feeding every pushed project's plugin type ids into the planner; that widening is removed, since under positive attribution there is no "unknown" bucket to shrink. Single-project and standalone pushes see the same protection.

- **`push` now protects sibling projects on the plugin-package (`.nupkg`) path too**: the package push path had its own unlinked-Custom-API sweep that didn't get the sibling-assembly protection the classic `.dll` path above already has, so a `.nupkg` project pushed alongside a `.dll` project in the same command could delete the `.dll` project's Custom APIs as unowned orphans — ungated by `--force`. The package path now feeds sibling plugin-type ids into the sweep the same way, and reflects once at prepare time so a multi-assembly package's non-primary assemblies are visible to it.
- **`push` rejects a raw assembly already registered as part of a plugin package**: pushing a `.dll` directly (`--scope assemblyonly`) when Dataverse already has it registered under a `PluginPackage` used to silently override the package-owned assembly. It now fails with a clear message to push the `.nupkg` instead — automated migration isn't supported. The same push also detects a secondary DLL in a multi-assembly package that's already registered classic (non-package) in Dataverse, and fails with a clear message instead of the earlier confusing failure at content-write time.

- **Orphan cleanup no longer misclassifies a reconciled Role as orphaned**: Role was the only orphan-detected type still matched purely by the raw id declared in Solution.xml. Dataverse reconciles security roles by name on import when a role of that name already exists in the target, so a role synced from one environment could carry a different live id in another — the raw id-match alone would then report a still-assigned role as removable. Role is now also resolved live by name (its local `Roles/<name>.xml` file name), the same way WebResource/Entity/OptionSet already are, in addition to the existing id-match.

- **`push` no longer prints a raw stack trace for a bad `[CustomApi]`/`[Step]`/etc. attribute**: every validation error `PluginTypeMetadataScanner` raises while reading a plugin assembly (invalid `[CustomApi(UniqueName = ...)]` format, a bad secondary table, and about 20 other checks) used a plain `InvalidOperationException`, which `Program.cs`'s global handler doesn't recognize — it fell through to a full raw exception dump instead of a clean `Error: ...` line. Both places that analyze a plugin assembly (`push`'s normal and `--scope assemblyonly` paths) now catch that exception and rewrap it as a `FlowlineException`, so any misconfigured attribute reads as a one-line validation error like every other rejected push.

- **Connects via the Windows account broker (WAM) for PAC's "OperatingSystem"-type profiles**: PAC CLI 2.9+ defaults a new profile to storing its refresh token in the Windows account broker rather than PAC's own file-based token cache, which Flowline read exclusively — those profiles failed with a session-expired error even though `pac` itself still worked. Flowline now tries the WAM broker first on Windows, then the OS account, then falls back to the file cache.
- **No longer connects using a sample/tutorial Azure AD app id**: Flowline used Microsoft's own "Dynamics 365 Example Client Application" sample app id, which lacks admin consent in most tenants and could fail with `AADSTS90072` even though `pac`'s own calls worked. It now uses PAC CLI's real registered app id.
- **Picks the cached sign-in by tenant, not by username alone**: the same email cached under more than one tenant — e.g. a stale guest account alongside the real member account — could resolve to the wrong one and fail with `AADSTS90072` ("account doesn't exist in tenant"). The cache lookup now prefers the account whose tenant matches the profile's own, falling back to username-only and then any account.
- **No longer tells users to authenticate against an internal BAP admin URL**: a session-expired error used to suggest `pac auth create --url https://api.bap.microsoft.com`, an internal admin endpoint rather than a real Dataverse environment. Environment lookups now use the target environment's own URL throughout, so the remediation message points at something a user can actually authenticate against.
- **`sync`'s dirty-check no longer ignores changes it can't map to a single component**: files under `Solution/src/Other/` (`Solution.xml`, `Customizations.xml`) have no one-to-one component mapping, so they were excluded from the dirty-tree file count entirely — a change there could silently bypass the safety gate that blocks syncing over uncommitted work. They're now counted toward the total; only the displayed per-file breakdown still depends on whether the path resolves to a component.

- **`drift` no longer false-flags a dotted classic plugin assembly as orphaned**: PAC's solution-unpack strips periods from a classic PluginAssembly's on-disk filename (`Cr07982.LegacyPlugins` unpacks to `Cr07982LegacyPlugins.dll`), while build output keeps the real, dotted name — a raw filename comparison misreported any dotted classic assembly as orphaned, risking its accidental deletion. Drift now resolves the assembly's true name from its companion `.dll.data.xml` metadata before comparing. Nupkg `PluginPackage` exports, which don't strip the dot, and any DLL with no readable metadata are unaffected.
- **`deploy --path` no longer requires a solution file to be present**: it always resolved the solution's package folder from the `.slnx`/`.sln` file even when the deploy source was an already-built zip and didn't need it, so a `--path` deploy from an artifact-only checkout (no solution file in the repo) failed before it started. The solution file is now only resolved on the paths that actually need it (packing, drift, version checks).

## [0.12.0] - 2026-07-17

### Fixed

- **Bare `--managed` no longer crashes `clone`/`sync`**: passing `--managed` with no value threw an unhandled `NullReferenceException` — the option bound to Spectre's optional-value flag syntax without a `[DefaultValue]`, so `IncludeManaged` was left in a state neither command's `.IsSet` check handled. Bare `--managed` now defaults to `true`; `--managed false` and omitting the flag entirely are unaffected.
- **`sync --managed`/`--managed false` on a single-solution project now actually applies**: when no solution name is passed (the common case), `ProjectConfig.GetOrUpdateSolution`'s no-name shortcut returned the resolved solution immediately, skipping the managed-mode conflict check below it — so the requested mode was silently ignored, with no prompt, no `--force config` requirement, and no update. Passing an explicit solution name was unaffected.
- **`sync` now writes `.flowline` back to disk on every run**: a confirmed managed-mode change previously only lived in memory for that invocation — `Config.Save()` was never called, so the change was lost the moment the process exited.
- **`clone` re-syncs an already-cloned solution when it's missing the managed layer**: re-running `clone --managed` on a solution that was previously cloned unmanaged now re-fetches `Package/src/` from Dataverse to pick up the managed layer, instead of silently skipping. Detected by checking for PAC's `*_managed.xml` sibling files on disk — no re-sync (and no extra Dataverse round-trip) when the local source already has what the current mode needs. Fixes a bug where `clone --managed` after an initial unmanaged clone updated `.flowline` but left `Package/src/` unpacked for unmanaged only, causing a later managed `pac solution pack` to fail with a cryptic error. Re-syncing overwrites `Package/src/` — commit local edits there first.
- **Managed/unmanaged status message no longer prints raw C# bool casing**: `Solution X (managed: True) exists` is now `Solution X (managed) exists` / `Solution X (unmanaged) exists`.
- **Log-file pointer now prints on every failing run**: a command that returns a non-zero exit code directly (e.g. a build/pack failure) instead of throwing a `FlowlineException` used to skip the "Log: ..." line entirely, leaving no pointer to the invocation log to debug from.
- **Raw git stderr no longer printed in red for expected-to-fail probe commands**: git-context enrichment (branch/remote detection for invocation logs) and the pre-sync change-summary's `git diff --numstat` probe both hit real, expected non-zero exits on a repo with no commits yet or no upstream configured — output now routes through `--verbose` logging instead of the console's red error styling.

## [0.11.0] - 2026-07-16

### Added

- **`deploy` confirms before a solution's first import to a target**: the first time a solution is imported to an environment that doesn't have it yet, `deploy` asks for confirmation — worded per managed/unmanaged mode, since Flowline can't switch a target's mode back once set. Doesn't fire for `dev` (already rejected by the DTAP gate) or when the solution already exists in the target (the existing, non-bypassable managed/unmanaged mismatch guard covers that case). **Breaking for CI**: a pipeline doing a first-time deploy of a solution to a target now exits `17` unless it already passes `--force first-import` (or `--force all`) — add it to existing non-interactive first-deploy scripts.
- **`deploy` publishes the packed solution zip to CI natively**: on Azure Pipelines, the zip is attached as a build artifact automatically, named `<SolutionName>-<Version>` so the version is visible in the Artifacts tab without changing the underlying zip's own filename; on GitHub Actions, its resolved path is written to the step output `artifact-path-<SolutionName>` for the workflow's own `actions/upload-artifact` step to reference. No new flag, fires regardless of the subsequent import's outcome, and a failure to emit either signal is a warning, never a deploy-blocking error.
- **`[CustomApi(UniqueName = "...")]`**: override a Custom API's unique name with the complete Dataverse name, publisher prefix included — lets a class migrated from another registration tool (spkl, Daxif) adopt an already-live Custom API in place when Flowline's derived name wouldn't match it. The prefix is validated against the live solution's publisher prefix and `push` fails with a clear error on mismatch; two Custom APIs resolving to the same final unique name — derived or explicit — now fail the push instead of silently colliding.
- **`// flowline:onchange` annotation**: bind a web resource function to a specific field's OnChange event, the same way `// flowline:onload`/`onsave` bind to the form's OnLoad/OnSave. Scoped per attribute — two annotations targeting different fields on the same form register and update independently instead of clobbering each other. `push`'s dry-run/verbose change report and the rename-aware diagnostics introduced for onload/onsave in 0.10.0 both cover onchange as well.
- **Tab and IFRAME form events**: form-event annotations now also bind to a Tab's `TabStateChange` event and an IFRAME's `OnReadyStateComplete` event, scoped by tab name or control id the same way onchange is scoped by attribute. IFRAME control ids match prefix-insensitively, so an annotation can use either the bare suffix a maker actually types or the Maker Portal's fully-prefixed `IFRAME_...` control name.
- **`[bulkEdit]` and `[order:N]` annotation modifiers**: append to any `flowline:onload`/`onsave`/`onchange` annotation. `[bulkEdit]` (onload only) toggles the form library's "Enable for bulk edit form" setting. `[order:N]` sets explicit cross-file handler ordering, overriding the default annotation-encounter order — `push` fails if two annotations for the same event/scope claim the same order value. Dataverse's 50-handler-per-event cap is now enforced before any write.
- **Push plugin assemblies packaged as a NuGet package (`PluginPackage`)**: when the Plugins project's build produces a `.nupkg` (e.g. `pac plugin init`'s default project shape), `push` auto-detects it and registers a Dataverse `PluginPackage` instead of a classic assembly — supports multiple plugin-bearing DLLs in one package, each synced independently. Change detection hashes the whole package, so an unchanged package skips the content write and only syncs each assembly's own drifted steps/Custom APIs. Standalone `--pluginFile` now also accepts a `.nupkg` path. New `.flowline` key `PluginPackageMode` (`Auto` default / `Nupkg` / `Dll`) controls the behavior: `Auto` uses a package when the build produced one, else falls back to the classic assembly; `Nupkg` requires a package and fails loudly if the build didn't produce one; `Dll` forces the classic path even when a package exists — the only option for on-premises environments, since Dependent Assemblies packages are cloud-only. Orphan cleanup redirects a package-owned assembly's deletion to its parent package (Dataverse rejects a direct delete on a package-owned assembly) and deletes any Custom API still bound to it first. `push` fails clearly if the build output contains more than one `.nupkg` (a stale package left over from a previous version bump) and self-heals a `.nupkg` that's older than the assembly it was built from (NuGet's incremental Pack step can skip regenerating it) by forcing one full rebuild.
- **Flowline ships as a Claude Code / Codex agent plugin**: adds `flowline` and `flowline-migration` skills plus marketplace catalogs for both ecosystems, so an agent knows how to drive Flowline commands and how to migrate an existing spkl/Daxif/PACX/ALM Accelerator project before a Flowline project even exists. See the README for install instructions.

### Changed

- **Form event handler merge preserves a foreign handler's position**: an out-of-the-box or ISV-authored handler already registered on an event now keeps its original position in the form's handler list instead of always being pushed after Flowline's own handlers — it keeps running exactly where it always ran relative to Flowline-managed handlers.
- **`deploy` no longer accepts `--managed`** (breaking change, no deprecation window): managed/unmanaged mode was already a single project-wide setting read from `.flowline`, decided at `clone`/`sync` time — `deploy --managed` only ever mutated `.flowline` via a confirm prompt, it was never a genuine per-run override. Configure managed/unmanaged exclusively via `clone --managed`/`sync --managed`. `config` is also dropped from `deploy`'s valid `--force` specifiers, since it has no remaining config-write hazard.
- **`deploy`'s artifact-reuse cache now reports its outcome on every run, not just on a hit**: a fresh pack used to print nothing, so a cache miss was indistinguishable from a broken feature. Every non-`--path` deploy now prints the real outcome, naming the reason on a miss (no cached build yet, source changed, unresolvable commit, `--no-cache`, managed-flag mismatch, or a missing artifact file). Projects with Test and/or UAT configured also get a longer "build once, reused across every stage" explanation; on CI the line still reports the real outcome (a self-hosted or persisted-workspace runner can genuinely hit) with a note appended pointing at `--path` for reusing one build across ephemeral per-stage jobs. Cache mechanics are unchanged — this is messaging-only.

### Fixed
### Security

## [0.10.0] - 2026-07-12

### Added

- **Automatic form event registration**: `// flowline:onload`/`// flowline:onsave` annotations in a web resource bind one of its exported functions to a form's OnLoad/OnSave event. `push` registers and keeps in sync the resulting Form Library and Form Event Handler entries in the form's XML — no more manual wiring through the Maker Portal's Configure Event dialog. Function names are matched case-insensitively against the built file's real exports, so a typo or missing function fails the push with a clear error instead of registering a broken reference. Entries `push` created are cleaned up automatically once their annotation or source file is removed; handlers and libraries belonging to other solutions or ISVs are always left untouched, and anything that looks stale but wasn't clearly created by Flowline needs interactive confirmation or `--force` before removal.
- **`push --scope formevents`**: runs form event registration on its own against an already-built `dist/`, without also syncing web resource content. `--dry-run` and `--no-publish` apply to it the same as web resources.
- **Rename-aware diagnostics for form event lookup misses**: when `// flowline:onload`/`onsave` names a form that can no longer be found, `push` now checks three signals — a live form still carrying the annotation's own previously-registered handler fingerprint (strongest), a cached prior resolution for that `(entity, name)` pair (probable), or the sole remaining form on that entity (hedged) — and appends a concrete "update your annotation to: ..." suggestion to the failure message. The push still fails; this never auto-resolves the form, only points at the likely fix.
- **`flowline drift` command**: compares committed source against an arbitrary environment (`prod`, `uat`, `test`, `dev`, or a raw URL) and reports components present there but not declared locally. Read-only — never deletes or modifies anything; always bypasses the solution-info cache so a deleted or renamed target solution can't read as "no drift."
- **Bot and Connection Reference orphan detection**: orphaned Bots and Connection References are now identified via entity-side queries and surfaced in the orphan-cleanup report as manual-removal candidates.
- **Role orphans reported by name**: a Role removed from source now resolves to its display name in the orphan-cleanup report instead of falling through to a generic, verbose-only "unrecognized type" line.
- **Orphan risk tiers in the cleanup report**: automated findings are now grouped by priority — blocks deployment, still running deleted logic, safe to clean up — so the report distinguishes dangerous orphans from hygiene.
- **Verbose preview of unsupported orphan types**: `--verbose` now logs orphan candidates of types Flowline doesn't yet act on, with their resolved type and instance name where possible.
- **`deploy --path <zip>`**: import a pre-built solution artifact directly instead of packing from source. Skips the git-clean and drift checks (they assume the package came from the checked-out `Package/src`), validates the artifact's managed/unmanaged flag against the solution's configured setting, and unpacks the actual imported artifact for post-deploy checks.
- **`deploy` reuses a cached build artifact by default**: a repeat deploy of the same solution skips `pac solution pack` and reuses the last artifact packed for the current source commit (keyed on commit SHA + managed flag). `--no-cache` forces a fresh pack.
- **Unmanaged deploys auto-publish pending changes**: `deploy` now passes `--publish-changes` to `pac solution import` for unmanaged solutions.
- **`deploy` activates plugins and workflows on import**: `pac solution import` now always runs with `--activate-plugins`.
- **`//!`/`/*! ... */` form for `// flowline:depends` annotations**: survives default minifier settings (Terser/esbuild/SWC preserve `!`-prefixed comments) — recommended for WebResources projects with a minification step; prefer the block form `/*! ... */`, since some minifier configs only apply this preservation to block comments, not line comments. The plain `//` form still works.
- **Web resource update reports now say why**: `push`/`sync` output lists the reason for each web resource update (`content`, `displayname`, `dependencies`, or a combination) instead of just the resource name.

### Changed

- **Plugin step/image matching keyed by identity, not name**: `push` now matches an existing step to its assembly-side declaration solely by `(message, table filter, stage, mode)`, and an image solely by `(step, image type)` — `name` is now write-only. Renaming a step (e.g. a multi-`[Handles]` class whose generated name changes) updates in place instead of risking a mismatch. Trade-off: changing a step's **message, table filter, stage, or mode** now always deletes and recreates the step rather than updating it, since those fields are the identity key and can no longer be edited on an existing row.
- **Web resource plan/summary lines now show counts**: "Web resources found (N Dataverse, M local)" and "Web resource plan ready: N creates, M updates, K deletes" replace the previous name-only lines.
- **`--force`/`-f` now requires a value naming the specific hazard being approved** (e.g. `--force delete-orphans`, `--force dirty`, `--force config`, or `--force all`) instead of a bare boolean flag — breaking change, no deprecation window. Bare `--force` is now a parse error on every command. Each command accepts only the specifiers it actually gates; an unrecognized value fails listing the valid ones. See `flowline <command> --help` or the Command Reference wiki for each command's vocabulary.

### Fixed

- **`// flowline:depends` annotations recognized anywhere in the file**: previously the parser stopped scanning at the first non-`//`-comment line, so a bundler-injected banner (e.g. Rollup's `banner` option, used by the default WebResources scaffold) silently dropped every annotation in the built file. Annotations are now found regardless of position.
- **`// flowline:depends` bare filenames now resolve to the Maker Portal's fully-qualified name**: a dependency written as `lib.js` used to be stored as the literal bare filename, which didn't match how the Maker Portal's own dependency editor writes `Library@name`. Flowline now qualifies the bare name against the sibling web resource it matches, warning and leaving it unqualified if the match is ambiguous.
- **Orphan cleanup no longer aborts a deploy on a transient Dataverse fault**: a single failed live query used to abort orphan detection before other component types were even checked, discarding all already-computed findings. Each handler now catches and degrades to its existing "record not found" fallback instead of crashing.
- **`deploy`'s DTAP gate closes a downgrade gap**: it previously only blocked promoting a version the predecessor tier hadn't verified yet; deploying an *older* version than what the predecessor had already verified passed silently. The gate now requires an exact version match between the predecessor tier and the version being promoted.
- **`sync` reports when there's nothing to deploy**: when a sync produces no component changes, the completion message says so directly instead of prompting to commit and tag a no-op.
- **Managed deploy's no-delete report no longer says `(--no-delete active)`**: that text was always shown for managed solutions' preview-only orphan report even though nothing set `--no-delete`. It now distinguishes a first install from a preview of what an upgrade import will remove.
- Subprocess output in verbose mode no longer shows literal `[dim]...[/]` markup instead of dim-colored text.
- WebResources scaffold: the generated ESLint config only linted `.ts` files — `.js` files are now linted too.

### Security

- **Log-file redaction hardened**: email addresses in the per-invocation log are now redacted (new), and email/URL redaction switched from an unsalted hash to a per-install, randomly generated salt (HMAC) — output is no longer correlatable across installs or crackable via a precomputed table.

## [0.9.0] - 2026-07-05

### Added

- **Run commands from any subdirectory**: Flowline walks up from the current directory to find `.flowline` — no need to `cd` back to the project root before running a command.
- **Solution/environment status grid**: `flowline status` renders a solution × environment matrix (Spectre.Console table) with drift detection, dirty-repo indicator, and a legend below the grid — replaces the old nested per-solution output.
- **Deploy Solution Checker gate**: `flowline deploy` runs `pac solution check` before import by default and aborts on failure. Use `--skip-solution-check` to opt out.
- **Deploy pre-import environment backup**: `flowline deploy` takes a `pac admin backup` of the target environment before import, as a safety net ahead of orphan-cleanup deletions. Use `--no-backup` to opt out.
- **`sync --bump none`**: skip version bumping for a sync run.
- **`sync --no-build`**: skip build validation, matching the existing `push --no-build`.
- **`[Step].Description`**: description text is now pushed to the step's description field in Dataverse, visible in the Plugin Registration Tool.
- **`clone` scaffolds a root `.gitignore`**: replaces the previous per-project `.gitignore` files with one at the repo root.
- **Invocation logs enriched with CI, git, and trace context**: each run's structured log now includes CI platform detection, the current git branch, detected tool versions (PAC CLI/dotnet/npm), and a W3C `Activity.TraceId` for correlating log lines within a run.
- **Subprocess output captured to the invocation log**: `dotnet`/`npm`/PAC CLI subprocess output is captured into the structured log file, not just echoed to console, with client secrets, tokens, and URLs redacted before writing.

### Changed

- **`[Step]` and `SecondaryTable` require explicit opt-in for "all tables"**: omitting the table on `[Step]`, or `SecondaryTable` on an Associate/Disassociate step, now fails at push time instead of emitting a warning. Use `[Step("none")]` / `SecondaryTable = "none"` to register on all tables explicitly.
- Dependencies bumped: Serilog, Microsoft.PowerPlatform.Dataverse.Client, Microsoft.Identity.Client.Extensions.Msal, Spectre.Console, Microsoft.Extensions.Logging.Abstractions.
- Progress spinner defaults and sync status messaging streamlined for clearer feedback during `push` and `sync`.
- PAC CLI log prefix relabeled in `dnx`-wrapped subprocess output for clarity.

### Fixed

- `deploy`'s DTAP predecessor-version check now always bypasses the validation cache — prevents promoting against stale cached solution info.
- Solution Checker gate fails closed (blocks the deploy) when `pac solution check`'s summary table can't be parsed, instead of silently passing.
- `status` grid degrades to a dash instead of erroring when a local `Solution.xml` is malformed.

## [0.8.0] - 2026-06-29

### Added

- **`push --no-publish`**: skip `PublishXml` after web resource sync — useful when chaining `push` into a pipeline that handles publish separately.
- **Per-invocation log file**: Flowline writes a structured log file (Serilog) for each run to the Flowline storage path. Console output is tee'd to `ILogger` via a render hook — every command produces a machine-readable trace.
- **Custom API grouping in plugin planner**: custom APIs are grouped and rendered as a tree alongside plugin steps — clearer output when a solution has many registrations.

### Changed

- **`[Handles].On` renamed to `[Handles].Message`**: aligns with the `Message` naming convention used by `[Step]`. Update any plugin class that uses `[Handles(On = "...")]` to `[Handles(Message = "...")]`.
- **Multi-`[Handles]` step names are stage-qualified**: when a plugin class handles multiple messages, the registered step name now includes the stage suffix to ensure uniqueness.

### Fixed

- Orphan plugin assembly deletion no longer blocked by dependent steps — steps are cascade-deleted first.
- `friendlyname` uniqueness validated across namespaces before execution, not mid-run.
- `generate`: user-owned files preserved during temp-swap; deletions scoped to generator-owned files only; empty directories cleaned up after swap.
- `generate`: output path handling corrected for standalone mode; success message formatting improved.
- `generate`: exit codes and error messages corrected for `xrmcontext3` auth failures and general validation errors.
- `provision`: region consistency validated — cross-region environment URL mismatch now fails early with a clear error.
- Sensitive arguments (client secrets, tokens) redacted in verbose subprocess output.

## [0.7.0] - 2026-06-21

### Added

- **AI-native schema context**: `sync` writes `DATAVERSE_CONTEXT.md` — entities, attributes, option sets, forms, views, workflows, and plugin steps extracted from solution XML. Claude Code, Copilot, and Codex load it automatically via `AGENTS.md`.
- **AGENTS.md scaffolding and self-healing**: `clone` creates `AGENTS.md` at the repo root; `sync` keeps it up to date with a pointer to `DATAVERSE_CONTEXT.md`.
- **`generate --output` saved to `.flowline`**: the output path is persisted in the project config and reused on subsequent runs.
- **Deploy orphan cleanup**: `deploy` detects solution components removed since the last import and removes or reports them.
- **DTAP gate on deploy**: `deploy` refuses to promote unless the source environment matches what has been synced — prevents deploying untested configuration drift.
- **Managed/unmanaged type guard on deploy**: pre-flight check blocks deploying a managed solution to an unmanaged target and vice versa.
- **Sync sub-change summaries and `CHANGES.md`**: `sync` drills into attribute, option set, and view column changes — full detail written to `CHANGES.md` after every sync.
- **XrmContext v4 generator** (`--generator xrmcontext`): uses the `xrmcontext` dotnet global tool for early-bound type generation. Supports `--service-context-name`. Binary auto-downloaded and cached via NuGet.
- **Auth: automatic profile selection**: Flowline picks the best PAC auth profile automatically (active profile preferred), shows an interactive picker when ambiguous, and warns when tokens are nearing expiry.
- **Auth: client secret resolution chain**: `--client-secret` flag → environment variable → interactive prompt → fail.
- **`--client-id` and `--client-secret` flags** on `generate` for service-principal auth to XrmContext.
- **Bulk operation messages**: `[Step]` and `[CustomApi]` attributes now accept bulk operation messages (`BulkDetect`, `BulkExport`, etc.).
- **`push --no-build`**: skip the npm/dotnet build and push the existing `dist/` directly. Includes a mass-delete guard when `dist/` is empty.
- **UAT environment**: `deploy uat` promotes to a UAT tier alongside `test` and `prod`.
- **Web resource dependency enrichment**: annotation parser and dependency diffing harden dependency registration. Annotation-referenced web resources are exempted from orphan deletion.
- **Verbatim mode**: web resource folders already carrying the publisher prefix are pushed as-is, without double-prefixing.
- **Typed exit codes**: `Success`, `ConfigInvalid`, `AuthFailed`, `BuildFailed`, `PartialSuccess` — CI pipelines and agents can distinguish failure modes.

### Changed

- Generator name `xrmcontext` (v3) renamed to `xrmcontext3`; `--generator xrmcontext` now targets XrmContext v4.
- `--secret` renamed to `--client-secret` on `generate`.
- Dependencies bumped: CliWrap, Spectre.Console, Microsoft.Extensions.*, Microsoft.Identity.Client.Extensions.Msal, System.Security.Cryptography.Xml.

### Fixed

- GUID-based link-entity alias prefix (`a_<32hex>.fieldname`) stripped from view column names in `DATAVERSE_CONTEXT.md`.
- UTF-8 output encoding set explicitly — prevents logo and spinner corruption in some Windows terminals.
- Deleted plugin step names resolved from git history when generating sync change summaries.
- UTF-8 BOM stripped from solution XML before parsing change summaries.
- CRLF line-ending warning suppressed during sync change summary generation.
- Web resource dependency enrichment hardened; RESX cross-folder matching fixed.
- XrmContext NuGet extraction path corrected — binary lands in the expected `content/XrmContext/` directory.
- XrmContext authentication uses `method:OAuth` for MFA-enforced tenants.
- Orphan cleanup hardened: component ID count limit enforced, unknown components handled gracefully, `PartialSuccess` exit code returned on partial failure.

### Security

- CI workflows: `contents: read` permission scoped explicitly; GitHub Actions pinned to latest versions; `GITHUB_TOKEN` added to NuGet cache cleanup step.

---

## [0.6.0] - 2026-06-05

### Added

- **Scaffolded WebResources project**: `clone` creates a TypeScript + Rollup project under `WebResources/` — wired to `push` from day one.
- **`push --assemblyonly`**: push only the plugin assembly, skipping web resource sync.
- **`push --force`**: force-register a plugin assembly even when the content hash has not changed.
- **Solution version in `status`**: `flowline status` shows the solution version alongside environment and auth info.
- **`generate` standalone mode**: `flowline generate` runs outside a full Flowline project context.

### Changed

- `push --save` renamed to `push --no-delete` — opts out of orphan cleanup.
- `push --dll` renamed to `push --pluginFile`.
- PAC clone/sync output moved into `Package/` subfolder — repo root stays clean.
- `--json` flag removed from commands.

### Fixed

- `Flowline.Attributes` NuGet: `PackagePath` corrected for content files so the source-only package distributes correctly.

---

## [0.5.0] - 2026-05-29

### Added

- **`flowline generate`**: generates early-bound C# types into `Plugins/Models/`. Supports PAC generator (`--generator pac`) and XrmContext v3 (`--generator xrmcontext3`). Output path configurable and persisted in `.flowline`.
- **Sync change summary**: `sync` translates the XML diff into plain language — entities and components added, changed, or removed. Written to `CHANGES.md` after every sync.
- **Pre-sync dirty-tree guard**: `sync` refuses if the working tree has uncommitted changes. `--force` bypasses.
- **Deploy guard**: `deploy` blocks if local changes have not been synced — enforces `push → sync → deploy`. `--force` bypasses.
- **Solution versioning**: `sync --bump` auto-increments the patch version in Dataverse and tags the commit. `--no-tag` skips the git tag.
- **`[Handles]` attribute**: annotate plugin classes that handle multiple messages — no more duplicating `[Step]` for each message.
- **`flowline status`**: shows environment connectivity, auth profile details, Dataverse health, and solution version.
- **No mapping files**: `deploy` packs via `pac solution pack` — `MappingPac.xml` and `MappingBuild.xml` no longer generated or needed.
- **Publisher customization prefix**: fetched from Dataverse and applied automatically for web resource naming.
- **Unmanaged solution guard on `provision`**: `provision` validates the source is an unmanaged solution before copying.

### Changed

- `SecondaryEntityAttribute` renamed to `SecondaryTableAttribute`; migrated into `[Step]` as `SecondaryTable`.
- `MessageName` and `ParameterName` enums removed — use `Message` string constants instead.
- Environment name "Staging" standardized to "Test" throughout.

### Fixed

- R7 false positive in `[Handles]` attribute validation.
- Empty `[Handles]` message now produces a clear validation error.
- Drift checker includes the release folder in orphan assembly detection.
- Unlinked custom APIs cleaned up in verbose tree output.

---

## [0.4.0] - 2026-05-07

### Added

- **Tree-based operation output**: plugin and web resource operations render as a tree — readable for large solutions.
- **Global web resources to solution**: web resources found outside a solution component are added to the solution automatically during sync.

### Fixed

- `FaultException<OrganizationServiceFault>` caught specifically — meaningful error messages instead of generic exceptions.
- Invalid web resource file names reported as errors instead of unhandled exceptions.
- Deprecated Silverlight/XAP files detected and skipped with a warning.

---

## [0.3.0] - 2026-05-05

### Added

- **`[CustomApi]` attribute**: define Dataverse Custom APIs directly in C# — request parameters, response properties, and binding. Registered alongside plugin steps in one pass.
- **`[PreImage]` and `[PostImage]`**: registered in Dataverse automatically.
- **`[RunAs]` on plugin steps**: impersonate a system user during step execution.
- **`DeleteJobOnSuccess`**: auto-delete the async system job on success for async post-operation steps. Defaults to `true`.
- **`push --dry-run`**: shows every registration, update, and deletion Flowline would perform — without touching Dataverse.
- **Hash-based change detection**: plugin assembly updates trigger a Dataverse write only when content has changed.
- **FQN change handling**: `push` handles assembly identity changes (public key token, culture, version) with delete-and-recreate.
- **Standalone `push`**: `push --dll <path>` and `push --webresources` work outside a full project context.
- **Managed solution awareness**: `clone`, `push`, and `sync` detect managed solutions and adapt.
- **Web resource sync**: orphan detection and planning for web resources — resources absent from source are detected and cleaned up.

### Changed

- `[Entity]` attribute renamed to `[Step]` — matches Dataverse terminology.

### Fixed

- Filtering attribute values trimmed and de-duped on load.
- Plugin step registration runs sequentially — prevents `GrantInheritedAccess` race conditions in Dataverse.

---

## [0.2.0] - 2026-04-19

### Added

- **`Flowline.Attributes` NuGet**: source-only package with `[Step]`, `[Filter]`, and `[Image]` for attribute-driven plugin registration. No `spkl.json`, no Plugin Registration Tool.
- **`flowline deploy`**: pack from the repo and import to any target environment.
- **`flowline clone`**: bootstrap an existing Dataverse solution into the repo from a source environment.
- **`flowline provision`**: provision a DEV or TEST environment by copying from production.

### Changed

- Project renamed to **Flowline** and namespaces reorganized.
- `BootstrapCommand`, `StageCommand`, `ReleaseCommand` removed — superseded by `clone` and `deploy`.

---

## [0.1.0] - 2025-06-28

### Added

- Initial Flowline CLI project scaffolding.
- GitHub Actions CI and release workflows.


[Unreleased]: https://github.com/RemyDuijkeren/Flowline/compare/0.12.0...HEAD
[0.12.0]: https://github.com/RemyDuijkeren/Flowline/compare/0.11.0...0.12.0
[0.11.0]: https://github.com/RemyDuijkeren/Flowline/compare/0.10.0...0.11.0
[0.10.0]: https://github.com/RemyDuijkeren/Flowline/compare/0.9.0...0.10.0
[0.9.0]: https://github.com/RemyDuijkeren/Flowline/compare/0.8.0...0.9.0
[0.8.0]: https://github.com/RemyDuijkeren/Flowline/compare/0.7.0...0.8.0
[0.7.0]: https://github.com/RemyDuijkeren/Flowline/compare/0.6.0...0.7.0
[0.6.0]: https://github.com/RemyDuijkeren/Flowline/compare/0.5.0...0.6.0
[0.5.0]: https://github.com/RemyDuijkeren/Flowline/compare/0.4.0...0.5.0
[0.4.0]: https://github.com/RemyDuijkeren/Flowline/compare/0.3.0...0.4.0
[0.3.0]: https://github.com/RemyDuijkeren/Flowline/compare/0.2.0...0.3.0
[0.2.0]: https://github.com/RemyDuijkeren/Flowline/compare/0.1.0...0.2.0
[0.1.0]: https://github.com/RemyDuijkeren/Flowline/releases/tag/0.1.0
