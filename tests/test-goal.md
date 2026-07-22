# Flowline CLI end-to-end test goal

Full `clone → push → sync → sln add → deploy` matrix against a live Dataverse DEV/PROD pair,
exercising real project-structure flexibility (move/rename/multi-project), not just the happy path.
Use this as the `/goal` input for future test runs; update it with new learnings each time.

## Environment

- DEV: `https://automatevalue-dev.crm4.dynamics.com`
- PROD: `https://automatevalue.crm4.dynamics.com`
- Solution: `Cr07982` (unmanaged)
- Test workspace: `E:\Code\TryOut\ClaudeFlowlineTest` — create/reset freely.
- **Never touch** `E:\Code\TryOut\MyFlowTest` (a separate, real project) or anything else under
  `E:\Code\TryOut\` outside the dedicated test workspace. Past incident: running `sln add` from a
  bare subfolder directly under `E:\Code\TryOut\` (not inside the test workspace) once walked
  upward and modified an unrelated file (`handwritten-backup.slnx`) sitting in that parent
  directory. The walk-up bug is now fixed (`sln add` only looks in the exact folder given), but as
  defense-in-depth, always run throwaway/negative-case tests for `sln add` and similar
  path-sensitive commands **inside** the test workspace, in a dedicated subfolder, never directly
  under `E:\Code\TryOut\`.

## DEV mutation permissions

DEV already has web resources, plugins, and custom APIs from prior test runs — treat all of it as
disposable test fixtures. Freely add, modify, or delete any component in DEV to exercise
push/sync/delete/orphan-cleanup scenarios. No need to preserve or restore DEV state between runs.

## Safety constraints (hard limits)

- PROD is off-limits for any real write. `deploy` has **no `--dry-run` flag** — do not run an actual
  deploy/import against DEV or PROD. Instead: exercise deploy's pre-flight rejection paths only
  (invalid target, dirty git tree, `dev` rejected as a deploy target — all fail before any
  Dataverse write), and use `flowline drift <target>` (genuinely read-only, safe against PROD too)
  as the preview-equivalent.
- Never delete or modify anything under `MyFlowTest`.
- Never force-push, never touch remote git state.
- Never commit in the Flowline source repo (`E:\Code\RemyDuijkeren\Flowline`) without being
  explicitly asked, even mid-session.

## Bug-fix policy

- On any bug/exception in Flowline source: attempt a fix if the root cause is clear and small
  (parsing error, null ref, obvious logic slip, wrong error-exit-code, misleading message). Re-run
  the exact failing scenario to confirm, then continue.
- **Verify the fix against the actual test/spec before committing to it** — one earlier "fix" this
  session (changing `[DefaultValue(true)]` to `false` on `--managed`) broke existing, correct,
  already-tested behavior (`ManagedFlagBindingTests`). Always run the full test suite after a fix,
  before rebuilding the CLI, and revert immediately if anything regresses.
- Don't fix anything requiring architectural judgment or deeper investigation (e.g. a false-positive
  orphan-assembly detection rooted in a PAC unpack naming quirk, or a raw unhandled
  `FaultException` surfacing through a rare Dataverse-side conflict) — log it as a finding instead.
- Run the full solution test suite (`dotnet test Flowline.slnx`) after every fix, not just the
  directly affected test file — cheap and has caught real regressions.
- After any fix: rebuild (`dotnet pack src/Flowline/Flowline.csproj -c Release`) and reinstall the
  global tool (`dotnet tool uninstall -g flowline` then `dotnet tool install -g flowline --add-source
  <nupkg-dir> --version <exact-version>` — pin the exact version explicitly, since `dotnet tool
  install -g flowline` with no version/source can silently resolve the real published package from
  nuget.org instead of the local build) before re-testing live.

## Test matrix

Cover both **fresh state** (wipe the test workspace, start clean) and **reused state** (idempotent
re-run against an already-cloned/pushed/synced folder) where relevant.

### `clone`

- Fresh empty folder, with/without each env URL (`--dev`, `--prod`, `--uat`, `--test`), with/without
  `--managed`.
- Idempotent re-clone into an already-cloned folder (expect skip messages, no errors).
- Requires an existing git repo first (`git init`) — this is intentional, not a bug.
- Managed-solution rejection, C#-keyword solution-name rejection (harder to trigger live without a
  matching real Dataverse solution — check the code path directly if a live repro isn't practical).
- Note: `--managed` bare (no value) sets `true`; `--managed false` explicitly resets; omitting the
  flag entirely leaves it unset, which downstream code treats as `false`. The CLI help's "DEFAULT"
  column reflects what bare `--managed` resolves to, not the omitted-flag default — don't "fix" this
  without re-running `ManagedFlagBindingTests` first.

### `push` — test **both modes explicitly**, they have different validation surfaces

**Project mode** (inside a cloned Flowline project folder):
- Full push (default scope), dry-run and real.
- Idempotency: re-running immediately after a real push should show "no changes."
- Each `--scope` value individually: `all`, `webresources`, `formevents`, `plugins`, `assemblyonly`.
- Invalid combo: `--scope assemblyonly --scope plugins` together → must reject
  (mutually exclusive).
- `--no-delete`, `--no-build`, `--no-publish`.
- Non-interactive confirmation gates: an unrecognized form-event handler requires
  `--force delete-form-handlers`; an orphaned plugin assembly requires `--force delete-orphans`.
  Confirm both are clearly reported and require the flag rather than silently proceeding or hanging.
- **Known unfixed minor issue**: `push` prints "Lookup form events..." twice and appears to re-fetch
  the same Dataverse snapshot once for orphan-cleanup and once for registration. Low severity, not
  deeply investigated: `tests/test-findings/push-form-events-snapshot-fetched-twice.md`.

**Standalone mode** (`--pluginFile`/`--webresources`, run from *outside* a Flowline project folder):
- Rejected when run *inside* a Flowline project folder (`.flowline` present) — must error clearly.
- Solution name is required as the first positional argument in standalone mode.
- `--scope plugins`/`assemblyonly` requires `--pluginFile`; `--scope webresources`/`formevents`
  requires `--webresources` — validate the error message names the missing flag, not a generic
  "no Flowline project found" (which is what you get if you omit `--pluginFile`/`--webresources`
  *and* the scope flag, since without either standalone flag there's no way to detect standalone
  intent at all — that's expected, not a bug).
- **Known unfixed issue**: pushing a `--pluginFile` standalone against an assembly already registered
  as a `PluginPackage` (nupkg) throws a raw, unhandled `FaultException` instead of a friendly message.
  Details/repro/suggested fix: `tests/test-findings/standalone-push-pluginpackage-raw-faultexception.md`.

### `sync`

- Clean tree: full sync, confirm the diff/drift summary looks right.
- Dirty tree: must reject with a clear message naming `Solution/src/...`, and the message must be
  **plain text, not raw Spectre markup tags** — this broke once (`ConsolePath.FormatRelativePath`'s
  markup embedded directly into a `FlowlineException` message, which gets escaped before display).
- `--bump patch|minor|major|none`, verify the version actually changes as expected.
- `--no-build`.
- Non-interactive `--managed` reconfirmation gate when the flag conflicts with the already-configured
  value — must reject cleanly, not hang or silently apply.
- If the WebResources project was moved/renamed since the last sync, confirm drift correctly still
  finds it (see "Project-structure flexibility" below) rather than reporting phantom drift.

### `sln add`

- Valid `.cdsproj` add, idempotent re-add ("already in ... — skipping", not an error).
- Wrong extension (`.csproj` → points at `dotnet sln add` instead).
- Nonexistent path.
- No solution file in the **exact** folder → must error, and must **not** search parent folders
  (regression test for the walk-up incident — run this specific case in an isolated subfolder inside
  the test workspace, per the safety note above).
- For `.csproj` (non-`.cdsproj`) additions to the solution file, use `dotnet sln add` — that's the
  correct tool, not `flowline sln add`.

### `deploy` — validation-only, per the safety constraints above

- Invalid target name (not `prod`/`uat`/`test`/`dev` and not a URL) → must give a clean validation
  error, not an opaque `MsalServiceException`/AADSTS stack trace (this was a real bug: garbage target
  strings fell through to being used as an OAuth token scope).
- `dev` as a deploy target → must be rejected ("use sync, not deploy").
- Dirty git tree → must reject before contacting *any* target environment. Note the dirty-check scope
  is `Solution/src/` (the Dataverse-solution folder) only — dirtying `Plugins/`/`Backend/` etc. does
  **not** trigger it, since deploy packs the Dataverse solution, not the plugin assembly.
- `flowline drift <target>` as the safe, read-only substitute for an actual deploy preview — confirm
  it works against both DEV and PROD with zero drift on a clean repo.

### Project-structure flexibility (`SolutionFileLayout` / multi-project support)

This is the core of the "big folder-structure change" — test it thoroughly, not just the scaffolded
default layout:

- **Move + rename the Plugins project**: relocate the folder and rename the `.csproj` (and its
  `.snk`, `PackageId`) to something with no "Plugins" in the name at all. `push` must still discover
  it via solution-file membership + `IPlugin`/`CodeActivity` reflection, build the right output, and
  register under the new package/assembly name — not by folder-name convention.
- **Move + rename the WebResources project**: relocate + rename so the folder name contains no
  "WebResources" substring either. Must still resolve via elimination + weighted signals (NoTargets
  SDK, `dist/`, bundler config, `package.json` build script, web asset files) — never a silent
  false-negative.
- **Two plugin projects, mixed shapes**: one nupkg-based (`PluginPackageMode.Auto` resolving to
  nupkg — the common shape for a project referencing `Microsoft.PowerApps.MSBuild.Plugin` with a
  `PackageId`), one classic/unpackaged (plain `.dll`, no NuGet packaging, signed assembly required —
  Dataverse rejects unsigned plugin assemblies with "Public assembly must have public key token").
  Both must discover, build, and register independently in **one** `flowline push` run —
  `PluginPackageMode.Auto` resolves per-project based on that project's own build output shape, not
  a single fixed shape for the whole solution.
- **Two WebResources-candidate projects**: a genuine ambiguity (matching score — same NoTargets SDK +
  `dist/` + bundler config + `package.json` build script signals on both) must throw `ConfigInvalid`
  naming both candidates. A *weak* second candidate (fewer matching signals) is correctly **not**
  flagged — the resolver only throws on an exact top-score tie, not merely "two plausible
  candidates"; it silently picks the clear winner. Don't mistake that design choice for a bug.
- **Zero plugin projects**: a solution with a Dataverse package + WebResources project but no plugin
  project at all must resolve fine, no error — `push` simply has nothing to register (R8/AE9).
  **Live-verified**: initially this was a real bug — default-scope (`all`) `push` threw
  `"No plugin project found..."` instead of skipping silently, because the throw condition didn't
  distinguish an implicit default scope from an explicit `--scope plugins`/`assemblyonly` request.
  Fixed in `PushCommand.PrepareProjectPluginsForPushAsync` (only throw when `settings.Scopes.Length >
  0`, i.e. the user actually asked for a plugins-only push). Confirmed after the fix: default-scope
  push with zero plugin projects succeeds (skips plugin work, pushes WebResources normally); explicit
  `--scope plugins` with zero plugin projects still correctly throws; `sync`/`drift` both already
  handled zero plugin projects fine without any fix needed.
- **Zero WebResources projects**: a solution with plugins but no WebResources project at all must
  resolve to `null` and skip web-resource work with a **loud warning**, not throw (R5, softened —
  WebResources is expected but not required). **Live-verified, works correctly as designed**:
  `push` prints `"Warning: No WebResources project found — skipping web resources. Plugins are still
  pushed."` and completes normally with just the plugin work.
- **Orphan/drift detection across renames**: after renaming a plugin project, the old assembly/package
  name becomes a genuine orphan in Dataverse — confirm `push`'s orphan warnings correctly name it and
  gate deletion behind `--force delete-orphans`.
- **Known unfixed gap**: a classic (non-package) plugin assembly whose `.NET AssemblyName` contains a
  period gets a **false-positive orphan flag** in `sync`/`push`/`deploy` drift checks — potentially
  dangerous (`--force delete-orphans` could delete a live registration). Details/repro/root
  cause/suggested fix: `tests/test-findings/false-positive-orphan-dotted-classic-assembly-name.md`.

## Operational notes

- **PAC auth profile ambiguity**: this machine has multiple PAC auth profiles that can resolve to the
  same environment URL (an unnamed one and a named one). Commands will error
  ("Multiple PAC auth profiles match ... run: pac auth select --index <n>") rather than guess — resolve
  with `pac auth select --index <n>` before proceeding, or pass `-a`/`--auto-select-auth-profile` to
  let Flowline switch automatically for that one command.
- Git hygiene in the test workspace: commit between test phases so `sync`'s dirty-check behaves
  predictably, and use `git checkout --`/`git status` before any destructive reset.
- Long-running commands (`clone`'s Dataverse export, `sync`'s export) can take several minutes — run
  them in the background and wait for completion rather than assuming a short timeout means failure.

## Way of working: unfixed findings

Every bug/issue found that isn't fixed inline (per the bug-fix policy above) gets its own file in
`tests/test-findings/`, named by slug (e.g. `false-positive-orphan-dotted-classic-assembly-name.md`),
not bundled into a single report. Each file should cover:

- **Status** (fixed/not fixed) and **severity**.
- **Repro** — exact steps/commands.
- **Root cause**, as far as it's understood.
- **Suggested fix direction**, if any, and why it wasn't attempted inline.

This test-goal document only ever *references* a finding file by path and a one-line summary — it
does not duplicate the full writeup. Before starting a new run, skim `tests/test-findings/` for
issues that might now be fixed (re-verify, then delete or update the file accordingly) and check
whether any still-open finding should be promoted to a fix this run instead.

## Deliverable

A findings report: what was tested, what passed, what failed and was fixed (with the fix and its
regression test), and a `tests/test-findings/<slug>.md` file for each finding that needed human
judgment instead. Update this file with anything newly learned before the next run.
