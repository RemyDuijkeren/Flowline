# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **`//!`/`/*! ... */` form for `// flowline:depends` annotations**: survives default minifier settings (Terser/esbuild/SWC preserve `!`-prefixed comments) — recommended for WebResources projects with a minification step; prefer the block form `/*! ... */`, since some minifier configs only apply this preservation to block comments, not line comments. The plain `//` form still works.
- **Web resource update reports now say why**: `push`/`sync` output lists the reason for each web resource update (`content`, `displayname`, `dependencies`, or a combination) instead of just the resource name.

### Changed

### Fixed

- **`// flowline:depends` annotations recognized anywhere in the file**: previously the parser stopped scanning at the first non-`//`-comment line, so a bundler-injected banner (e.g. Rollup's `banner` option, used by the default WebResources scaffold) silently dropped every annotation in the built file. Annotations are now found regardless of position.

### Security

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


[Unreleased]: https://github.com/RemyDuijkeren/Flowline/compare/0.9.0...HEAD
[0.9.0]: https://github.com/RemyDuijkeren/Flowline/compare/0.8.0...0.9.0
[0.8.0]: https://github.com/RemyDuijkeren/Flowline/compare/0.7.0...0.8.0
[0.7.0]: https://github.com/RemyDuijkeren/Flowline/compare/0.6.0...0.7.0
[0.6.0]: https://github.com/RemyDuijkeren/Flowline/compare/0.5.0...0.6.0
[0.5.0]: https://github.com/RemyDuijkeren/Flowline/compare/0.4.0...0.5.0
[0.4.0]: https://github.com/RemyDuijkeren/Flowline/compare/0.3.0...0.4.0
[0.3.0]: https://github.com/RemyDuijkeren/Flowline/compare/0.2.0...0.3.0
[0.2.0]: https://github.com/RemyDuijkeren/Flowline/compare/0.1.0...0.2.0
[0.1.0]: https://github.com/RemyDuijkeren/Flowline/releases/tag/0.1.0
