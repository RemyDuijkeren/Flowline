---
name: flowline
description: Dataverse ALM via the Flowline CLI — greenfield solution creation, plugin registration, web resource sync, and solution deploy for Git-tracked Dataverse solutions. Use when the repo has a `.flowline` file at its root, when the user mentions Dataverse plugins, web resources, Custom APIs, or solution deploy, or when spkl/PAC plugin registration workflows come up.
---

# Flowline — deterministic Dataverse ALM

## Detect

A Flowline project has `.flowline` at the repo root and **exactly one** Dataverse solution directly at
that root: `Solution/` (PAC-managed solution XML), `Plugins/`, `WebResources/`. There is no
`solutions/<Name>/` wrapper — a second solution is a separate repo.

Those three folders are the scaffolded default, not a requirement: every command after `clone`/`init`
locates the projects by reading the `.slnx`/`.sln` file, so they can be moved or renamed freely. Never
assume a path — read the solution file.

No `.flowline`? Route in this order:

1. **Migration candidate** — `spkl.json`, a Daxif `_Config.fsx`/`*.daxif`, a `.pacxproj`, or an ALM
   Accelerator pipeline: defer to the `flowline-migration` skill. Don't offer `clone`/`init`.
2. **Plugin-DLL-only task** (register a compiled assembly, no project wanted): standalone mode —
   `flowline push <SolutionName> --pluginFile <dll|nupkg> --dev <url>`, or `--webresources <folder>`.
   No `clone`, no `.flowline`. The solution must already exist in the environment.
3. **The solution already exists in Dataverse** → `flowline clone <SolutionName> --prod <url>`. Bare
   `flowline clone` picks interactively (environment, then one of its unmanaged solutions). Clone only
   *adopts* — it never creates a solution.
4. **Nothing to adopt — true greenfield** → `flowline init <Name> --dev <url> --publisher-prefix <prefix>`.
   This creates the publisher and an empty unmanaged solution in Dataverse, then scaffolds the repo
   around it. Sandbox and Developer environments only; it refuses a Production target.

`clone` needs an existing git repo — run `git init` first if there is none.

## Source-of-truth model

By default **PROD holds the unmanaged solution and plays the role `master` plays in Git**; a DEV
environment is a branch of it. So `clone` normally pulls from PROD, `push`/`sync` work against DEV, and
`deploy` promotes DEV's work onward. A team keeping only managed in PROD and treating DEV as truth is
also supported — don't assume either way beyond what `.flowline` says.

`flowline provision [dev|test|uat]` is how a DEV gets branched off PROD — it copies PROD into the
target with `pac admin copy`, creating the environment as a **Sandbox** when it doesn't exist yet —
so a provisioned DEV is always a Sandbox. A Developer environment is a fine `clone`/`sync` source but
can't be reached this way. Copy mode defaults to
minimal (schema, no data) for `dev` and full for `test`/`uat`; `--copy full|minimal` overrides.
An existing target is refused unless you pass `--allow-overwrite`.

Re-provisioning is the intended way to resync DEV with PROD after promoting — but **it is slow**:
typically 30 minutes to 2 hours, and Flowline raises `pac`'s wait ceiling to 8 hours for the rare
long copy rather than because one is expected. In practice you make several changes against one DEV and
re-provision only when it has drifted far enough from PROD to be misleading. Never suggest it as a
routine post-deploy step.

## Core loop

1. **Edit code.** Registration intent lives in the code, never in the Plugin Registration Tool:
   - C# plugin classes: `[Step]`, `[Filter]`, `[PreImage]`, `[PostImage]`, `[Handles]`,
     `[CustomApi]` (+ `[Input]`/`[Output]`). `[CustomApi(UniqueName = "prefix_Name")]` adopts an
     already-live Custom API whose name doesn't match the class-name convention.
   - JS web resources: `// flowline:onload`, `onsave`, `onchange`, `depends`, plus tab
     (`tabstatechange`) and IFRAME (`onreadystatecomplete`) events. Append `[order:N]` to fix
     cross-file handler order, `[bulkEdit]` (onload only) to toggle the library's bulk-edit setting.
   - Generated early-bound classes under `Models/` are `flowline generate` output — never hand-edit
     them, and see the `flowline-generate` skill when they're stale, missing, or won't compile.
2. `flowline push --dry-run` → read the printed plan (creates, updates, deletes, orphans) and show it
   to the user before anything mutating. It ends with `Air push complete`; nothing was written.
3. `flowline push` → deterministic sync to DEV, including orphan cleanup. A second run says
   `Nothing to push — already up to date.`
4. `flowline sync` (alias `flowline pull`) after any Maker Portal change; commit the result.
5. Promote: `flowline deploy test` → `flowline deploy prod`, DTAP-gated. `flowline deploy <env> --dry-run`
   runs every pre-flight (DTAP gate, git-clean, drift, pack, solution checker, orphan report) plus a
   labeled backup, then stops before importing; it ends with `Dry run complete`.

`flowline drift <env>` is the read-only preview of what a deploy would flag — safe against prod at any
time. `flowline status` reports environments, auth and git state without touching anything.

If the solution's schema changed *and* the plugin code uses early-bound types, run `flowline generate`
before building — see the `flowline-generate` skill. Late-bound plugins never need it.

## Contract

- **Branch on exit codes, not output text.** Non-zero is always a named code; the error message embeds
  the fix command verbatim — run it, don't parse prose for it. Output wraps at 80 columns in a non-TTY,
  so prose parsing is unreliable by construction.
- **There is no machine-readable mode and no blast-radius field.** `--dry-run` prints a human-readable
  plan. Read it, summarize it for the user, and get approval before the real run — don't look for a
  field to branch on automatically.
- **`push` exiting 0 doesn't mean the task is done.** It means registration succeeded. Verify the actual
  behavior (the step fires, the form loads the script) before reporting the change complete. A deploy
  can also exit 0 having only *reported* some orphans — see below.
- **Authority rule:** `push` treats the repo as authoritative — anything in DEV not present in source
  gets deleted (that's the point of orphan cleanup). `sync` treats DEV as authoritative for solution
  metadata — the repo gets updated. TEST/UAT/PROD are never authoritative: a change made directly there
  is drift, surfaced by `flowline drift`, fixed by porting it to DEV first. Never push over DEV changes
  that haven't been synced.
- **Auth profile is guarded.** Every Dataverse-touching command checks that PAC CLI's *active* auth
  profile matches the target environment. Non-interactively a mismatch fails with a `pac auth select`
  remediation — pass `--auto-select-auth-profile` (`-a`) in CI to switch automatically.
- **Diagnose before guessing.** `flowline status` is read-only and answers environment/auth/git
  questions in one call — run it first when an exit code points at auth or connectivity (4, 10).
- **Every run writes a full log** — with the verbose detail regardless of the console filter — but the
  path is printed *only when the command fails*. After a successful run, construct it:
  `<root>/Flowline/logs/<yyyy-MM-ddTHHmmss>Z-<command>.log`, where `<root>` is `%LOCALAPPDATA%` on
  Windows, else `$XDG_CACHE_HOME`, else `~/.cache`. Reading the log is the alternative to re-running
  with `--verbose`.

## What `sync` writes, and how to read it

Every `sync` rewrites two files in the repo. Both are generated output — edit the source, never these.

**`CHANGES.md`** — the same tree `sync` prints, as a file: components added, modified, or removed in
DEV since the last commit. It describes *this sync*, so it is replaced every run, not appended to.

**`docs/DATAVERSE_CONTEXT.md`** — a schema digest built from `Solution/src/`. Read it for logical
names, types, option set values, and which columns a form or view uses.

Its one limit, and it causes real mistakes: **it documents what the solution owns, not what a user
sees.** A form's XML carries only the cells this solution touches, so a field that exists on the
form but was never customized here simply isn't listed — its absence is not evidence it doesn't
exist. Conversely a listed field can still be hidden at runtime by a form script. Confirm against
Dataverse before concluding a column or control is missing.

Two more reading notes:

- `~ entity metadata` in a change report means the entity's XML changed without a listed
  attribute-level change — often ordering or a dependency block, not a customization.
- Flipping `--managed` changes the *extraction format* (`Solution.xml` goes `<Managed>0</Managed>` →
  `2`), so the first sync after it reports a large one-time diff that isn't a customization change.
  Commit it once and move on.

## Orphan cleanup — what actually gets deleted

`push` reconciles what it owns automatically; deleting a whole orphaned assembly or an unattributable
Custom API needs `--force delete-orphans`.

On `deploy`, the action depends on how confidently the component can be identified:

| Component | On deploy |
|---|---|
| Plugin assemblies, Custom APIs | deleted automatically |
| Web resources | deleted only with `--force delete-orphans` |
| Bots, connection references, tables, security roles, workflows | **reported only** — a human removes them |

Anything left in place reads as `detected, not auto-removed`. Treat a deploy that reports those as
"partially reconciled, go look", not as finished. Exit 18 (`PartialSuccess`) means the import landed but
some cleanup failed.

## Flags worth knowing

- `push`: `--scope all|plugins|assemblyonly|webresources|formevents`, `--pluginFile <dll|nupkg>`,
  `--webresources <path>`, `--no-build`, `--no-delete`, `--no-publish`, `--dry-run`, `--dev <url>`.
- `deploy`: `--dry-run`, `--path <zip>`, `--no-delete`, `--no-backup`, `--skip-dtap-check`,
  `--skip-solution-check`.
- `sync`: `--bump patch|minor|major|none`, `--managed [false]`, `--no-build`, `--dev <url>`.
- `init`: `--dev <url>`, `--publisher-prefix <prefix>`, `--publisher-name`, `--display-name`.
- Global: `--verbose` (`-v`), `--force <specifier>` (`-f`), `--no-cache`,
  `--auto-select-auth-profile` (`-a`).
- `flowline sln add <path.cdsproj>` wires a `.cdsproj` into the solution file — `dotnet sln add`
  refuses `.cdsproj` *and exits 0 while refusing*. Runs standalone: no `.flowline`, no git, no login.

## `--force` specifiers

Exit 17 (`ForceRequired`) always names the specifier to add. Passing one that isn't valid for that
command fails with the valid list.

| Command | Valid specifiers |
|---|---|
| `push` | `delete-orphans`, `recreate-assembly`, `delete-form-handlers`, `config`, `all` |
| `deploy` | `drift`, `first-import`, `delete-orphans`, `all` |
| `sync` | `dirty`, `config`, `all` |
| `clone`, `init`, `drift`, `generate`, `provision` | `config`, `all` |

`deploy`'s `delete-orphans` is narrower than `push`'s: it gates exactly one unattributable case (a
Custom API with no plugin type) plus web-resource orphans. Everything else follows the table above.

## Exit codes

Exit codes are a stable public API — they don't change meaning across Flowline versions.

| Code | Name | Meaning | Corrective action |
|------|------|---------|-------------------|
| 0 | Success | Command completed | — |
| 1 | GeneralError | Unexpected/unhandled error | Check error output |
| 3 | NotFound | A Dataverse solution, or a local file the command needs, wasn't found | Verify the name or path named in the error |
| 4 | NotAuthenticated | No usable PAC auth profile | Run: `pac auth create --environment <url>` |
| 10 | ConnectionFailed | Dataverse environment unreachable | Check the environment URL in `.flowline` |
| 11 | ConfigInvalid | `.flowline` or the `.sln`/`.slnx` is missing or malformed | Check the file named in the error |
| 12 | DirtyWorkingDirectory | Uncommitted git changes block the operation | `git commit` or `git stash` first (`sync` also accepts `--force dirty`; `deploy` does not) |
| 13 | BuildFailed | `dotnet build` or PAC pack failed | Fix the build errors and retry |
| 14 | VersionConflict | Target has a newer solution version | Add the `--force` specifier the error names |
| 15 | ValidationFailed | Drift detected, missing dependencies, invalid `--force` value, or schema mismatch | For `drift`, **15 means drift was found — that's success, not an error.** Otherwise read the error |
| 16 | Timeout | PAC CLI 60-minute limit exceeded | Retry; check environment health |
| 17 | ForceRequired | Destructive operation needs explicit confirmation | Add the `--force <specifier>` the message names |
| 18 | PartialSuccess | Deploy imported, but some orphan cleanup failed | Check output for components to remove manually |
| 19 | Inconclusive | A check couldn't run to completion (empty-input guard skipped the comparison) | Not a pass/fail signal — read the printed reason before trusting the result |
| 130 | Cancelled | Ctrl+C / SIGINT, or `deploy`'s first-import confirmation declined | For the confirmation case: re-run with `--force first-import` |

Codes 2 and 5 are intentionally unused.

## Gotchas that look like bugs

- `deploy dev` is rejected outright, `--dry-run` or not. Use `flowline sync`, or `flowline drift dev`
  as the preview.
- `drift`/`deploy` compare Dataverse against **committed** `Solution/src/`, not build output — anything
  pushed but not yet `sync`ed and committed shows as an orphan. Correct behavior.
- A long `deploy`/`clone`/`sync` is genuinely slow (a real import can exceed 10 minutes, silent for
  minutes at a stretch). Don't kill it on apparent idleness — a kill mid-publish leaves the import
  committed with post-import cleanup unrun.
- Commands act on the current directory; there is no `--project` flag. A harness that resets cwd must
  wrap every call.
- Plugin **package** (`.nupkg`) content syncs by content, not version — the version can stay fixed
  while code changes apply. A package's name and version can't be changed after creation.
