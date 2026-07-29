# Flowline CLI end-to-end test goal

Full `clone → push → sync → sln add → deploy` matrix against a live Dataverse DEV/PROD pair,
exercising real project-structure flexibility (move/rename/multi-project), not just the happy path.
Use this as the `/goal` input for future test runs; update it with new learnings each time.

## Environment

- DEV: `https://automatevalue-dev.crm4.dynamics.com`
- PROD: `https://automatevalue.crm4.dynamics.com`
- Solution: `Cr07982` (unmanaged)
- Test workspace: `E:\Code\TryOut\ClaudeFlowlineTest` or `C:\Code\FlowlineTryOutByClaude` depending on the machine — create/reset freely.

### Fixture state on `C:\Code\FlowlineTryOutByClaude` (built 2026-07-29, keep it)

The project-structure-flexibility fixture now exists on this machine — don't rebuild it, and don't
assume the scaffolded default layout when reading command output:

- `Backend/Cr07982.Backend.csproj` — the moved+renamed nupkg-mode plugin project (was
  `Plugins/Cr07982.Plugins.csproj`; `PackageId`, `.snk` and folder all renamed, no "Plugins" left in
  the name). Package in Dataverse: `av_Cr07982.Backend`.
- `LegacyPlugins/Cr07982.LegacyPlugins.csproj` — a second, **classic/unpackaged** plugin project
  (signed, plain `.dll`, no `Microsoft.PowerApps.MSBuild.Plugin`, no `PackageId`), holding
  `LegacyAuditPostUpdatePlugin` on `contact`. It signs with a **copy of Backend's `.snk`**, which is a
  different key than the `Cr07982.LegacyPlugins` already in DEV from the other machine — so a real push
  hits the identity-changed gate and needs `--force recreate-assembly`. That is fixture history, not a
  bug.
- `ClientAssets/Cr07982.ClientAssets.csproj` — the moved+renamed WebResources project (no
  "WebResources" substring anywhere).
- Both plugin projects reference the **published** `Flowline.Attributes 0.12.0` from nuget.org, not the
  current source tree — so attribute features newer than 0.12.0 aren't testable here without bumping it.
- Git history in the workspace is a plain local repo with no remote (`phase1`…`phase4` commits).

## DEV mutation permissions

DEV already has web resources, plugins, and custom APIs from prior test runs — treat all of it as
disposable test fixtures. Freely add, modify, or delete any component in DEV to exercise
push/sync/delete/orphan-cleanup scenarios. No need to preserve or restore DEV state between runs.

## Safety constraints (hard limits)

- PROD is off-limits for any real write. `deploy` supports `--dry-run` — **always** pass it for
  every deploy test against DEV or PROD, no exceptions. Never run `deploy` without `--dry-run`. Also
  still useful: `flowline drift <target>` (genuinely read-only, even lighter-weight) as an
  additional preview alongside `--dry-run`.
- Never force-push, never touch remote git state.
- Never commit in the Flowline source repo (`Code/Flowline/`) without being
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
- **Bug found and fixed this run (2026-07-23)**: idempotent re-clone of a project whose
  Plugins/WebResources projects were legitimately moved+renamed (this test workspace's own
  project-structure-flexibility history) used to scaffold brand-new duplicate default-named
  `Plugins/`/`WebResources/` projects and register them into the solution file — `clone`'s own
  scaffold-skip check used a hardcoded literal folder name instead of `SolutionFileLayout` discovery,
  unlike `push`/`sync`/`deploy`. Fixed (design confirmed with the user first, not assumed): the skip
  check now also asks `SolutionFileLayout`, OR'd with the original literal-folder check; a genuine
  WebResources tie or any other layout-load failure propagates and stops clone rather than silently
  falling back to scaffold. Live re-verified against the exact repro — now correctly prints "already
  there — skipping" for both, solution file untouched. A separate issue surfaced during
  re-verification and is now also fixed: `SeedWebResourceDistFromSrc` used to hardcode the
  `WebResources/public` seed destination, leaving stray untracked files when the real WebResources
  project had moved. `SetupWebResourcesProjectAsync` now returns the WebResources project's real
  folder (existing or freshly scaffolded) and the seed step writes into that instead of guessing.
  Live re-verified against the same repro — seed check now correctly evaluates the real project's
  `public/` folder, no stray `WebResources/` folder created. Full details:
  `docs/test-findings/clone-idempotent-reclone-duplicates-moved-plugins-webresources.md`.
- Fresh-state matrix (empty folder × env-URL combos × `--managed`) and the managed/C#-keyword
  rejection cases: **not re-verified live this run** — covered by existing unit tests
  (`CloneCommandTests.cs`) only this round.

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
- **Fixed** (2026-07-22, verified in source): the double form-events spinner now reads distinctly
  ("Checking form events..." vs "Registering form events...") — the underlying double-fetch is by
  design, not a bug. `docs/test-findings/push-form-events-snapshot-fetched-twice.md`.
- **Project mode fully live-verified 2026-07-29** against DEV, dry-run *and* real: default scope,
  idempotent re-run ("Nothing to push — already up to date."), all five `--scope` values individually,
  the mutually-exclusive combo (exit 15), `--no-build` / `--no-delete` / `--no-publish` (each printing
  its own "skipping (--flag active)" line), and real create/update/delete paths driven by actually
  adding a `[Step]` plugin class, editing a web resource, and deleting one. Web-resource counts are
  right, including `1 remove(s) … (still in other solution)` where a delete is not safe. The
  **`--force delete-form-handlers` gate is the one project-mode item still unverified** — it needs a
  Dataverse form whose handler points at a function no longer in local source, which this fixture has
  no cheap way to construct.

**Standalone mode** (`--pluginFile`/`--webresources`, run from *outside* a Flowline project folder):
- Rejected when run *inside* a Flowline project folder (`.flowline` present) — must error clearly.
- Solution name is required as the first positional argument in standalone mode.
- `--scope plugins`/`assemblyonly` requires `--pluginFile`; `--scope webresources`/`formevents`
  requires `--webresources` — validate the error message names the missing flag, not a generic
  "no Flowline project found" (which is what you get if you omit `--pluginFile`/`--webresources`
  *and* the scope flag, since without either standalone flag there's no way to detect standalone
  intent at all — that's expected, not a bug).
- **Fixed** (2026-07-22, verified in source: `PluginService.cs` now throws `FlowlineException` on a
  `packageid`-owned assembly before any Dataverse write, in both classic-assembly write paths).
  `docs/test-findings/standalone-push-pluginpackage-raw-faultexception.md`.
- **Live-verified this run (2026-07-23), project mode, `--dry-run`**: ran against both plugin projects
  in this workspace. `Cr07982.Backend` (moved/renamed nupkg-mode plugin project) succeeded cleanly.
  `Cr07982.LegacyPlugins` (classic/unpackaged) hit the classic-vs-package conflict guard with the
  expected clean friendly error — confirms that guard works correctly, not a bug.
- **Live-verified this run (2026-07-23), standalone mode negative cases**: run inside a Flowline
  project folder → rejected with the exact "cannot be used inside a Flowline project folder" message.
  Missing solution name positional arg → rejected. `--scope webresources` with `--pluginFile` only
  (once auth/env resolution passed) → `"--scope webresources/formevents requires --webresources."`.
  `--scope plugins` with `--webresources` only → `"--scope plugins/assemblyonly requires
  --pluginFile."`. Both name the missing flag exactly, not a generic project-not-found message. Note:
  the scope/flag-mismatch check runs *after* env-URL and auth-profile resolution in
  `PushCommand.ExecuteAsync` (`ResolveStandaloneEnvironmentUrl` before `ResolveScope`), so reaching it
  live requires a resolvable `--dev`/auth profile first — not a bug, just an ordering note for future
  live repro.

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
- **Live-verified this run (2026-07-23)**: full `sync` against DEV succeeded (~4 min), version bumped
  2.0.3→2.0.4, drift detection accurate for the real fixture state. Dirty-tree rejection re-confirmed
  with a plain entity-file change under `Solution/src/` — exit 12, clean plain-text message, no
  Dataverse contact.
- `--bump patch|minor|major|none`, `--no-build`, and the non-interactive `--managed` reconfirmation
  gate: **not re-verified live this run** — see prior session's `sync bump tests` git history in the
  test workspace (commit `516b445`) for earlier live coverage; not re-run this round.

### `sln add`

- Valid `.cdsproj` add, idempotent re-add ("already in ... — skipping", not an error).
- Wrong extension (`.csproj` → points at `dotnet sln add` instead).
- Nonexistent path.
- No solution file in the **exact** folder → must error, and must **not** search parent folders
  (regression test for the walk-up incident — run this specific case in an isolated subfolder inside
  the test workspace, per the safety note above). **Live-verified (2026-07-23)**: ran in
  `ClaudeFlowlineTest/tmp-sln-add-negative-test/` (isolated throwaway subfolder, deleted after) with a
  genuine `.cdsproj` present — error named the exact folder searched
  (`E:\Code\TryOut\ClaudeFlowlineTest\tmp-sln-add-negative-test`), no parent-folder mention; confirmed
  `E:\Code\TryOut\handwritten-backup.slnx` (the file the original incident modified) untouched.
- For `.csproj` (non-`.cdsproj`) additions to the solution file, use `dotnet sln add` — that's the
  correct tool, not `flowline sln add`.
- **Live-verified this run (2026-07-23)**: idempotent re-add of the already-present `.cdsproj` →
  `"already in Cr07982.slnx — skipping"`, exit 0. Wrong extension (a real `.csproj`) →
  `"'Cr07982.Backend.csproj' is a C# project — use 'dotnet sln add' for those..."`, exit 15.
  Nonexistent path → `"No project at '...' — check the path."`, exit 3. Workspace tree confirmed
  unchanged (`git status --short` clean) after all three.

### `deploy` — always with `--dry-run`, per the safety constraints above

- Invalid target name (not `prod`/`uat`/`test`/`dev` and not a URL) → must give a clean validation
  error, not an opaque `MsalServiceException`/AADSTS stack trace (this was a real bug: garbage target
  strings fell through to being used as an OAuth token scope). **Live-verified (2026-07-23)**:
  `deploy garbage-target-xyz --dry-run` → clean `'garbage-target-xyz' isn't a known target...` error,
  exit 15.
- `dev` as a deploy target → must be rejected ("use sync, not deploy") — the DTAP gate blocks this
  before `--dry-run` even becomes relevant, and blocks it **regardless of `--dry-run`** — `deploy dev`
  is never a valid target at all, dry-run or real. (Corrects this doc's earlier "run against DEV"
  wording below — there's no such thing; `flowline drift dev` is the DEV-target preview tool instead.)
  **Live-verified**: `deploy dev --dry-run` → `Dev is a development environment — use 'sync'...`,
  exit 15, no Dataverse contact.
- Dirty git tree → must reject before contacting *any* target environment. Note the dirty-check scope
  is `Solution/src/` (the Dataverse-solution folder) only — dirtying `Plugins/`/`Backend/` etc. does
  **not** trigger it, since deploy packs the Dataverse solution, not the plugin assembly.
  **Live-verified**: dirtying `Solution/src/Other/Customizations.xml` and running
  `deploy prod --dry-run` → rejects immediately after the prerequisites check (no "Checking prod..."
  line at all — zero Dataverse contact), plain-text message naming the file, exit 12.
- Full `--dry-run` run against PROD: confirm it runs every pre-flight check — DTAP gate, git-clean,
  local plugin/web-resource drift, packing, the solution checker gate, and the orphan-cleanup report —
  and takes a labeled environment backup before stopping short of import. Confirm the backup label is
  actually `flowline-dryrun-<solution>-<timestamp>`, distinct from a real deploy's
  `flowline-deploy-<solution>-<timestamp>` (`PacUtils.BuildBackupLabel`). Confirm the final message
  reads "Dry run complete — ... would deploy cleanly ..." and exit code is 0. **Live-verified**: ran
  `deploy prod --dry-run --skip-dtap-check --force drift -a` (DTAP predecessor check and local drift
  both bypassed via flags — see profile-ambiguity note below) against real PROD. Full pipeline ran:
  packed fresh ("No cached build yet"), solution checker ("3 findings, 0 Critical"), real
  `pac admin backup` call confirmed via `--verbose` (returned a genuine Environment Id/Backup Expiry
  record), label was exactly `flowline-dryrun-Cr07982-20260723T005539Z`, orphan report showed 2 real
  Prio1 orphans (`Plugins` assembly + one step — stale from an earlier plugin-project rename, expected
  given this workspace's git history) with "would delete" phrasing and the `(--dry-run preview)` hint,
  final line exactly `🚀 Dry run complete — 'Cr07982' would deploy cleanly to AutomateValue. Run
  without --dry-run to make it real.`, exit 0.
- Confirm `--dry-run` never calls `pac solution import`, never runs post-import cleanup, and never
  emits a CI artifact-publish signal (`##vso[artifact.upload...]` line or a `$GITHUB_OUTPUT` write) —
  check console output for the absence of both. **Live-verified**: none of the three appeared in any
  `--dry-run` run's output (verbose or not) — output stops at the completion message.
- First-import scenario under `--dry-run` (target has no existing copy of the solution yet): confirm
  it prints an informational note ("the real deploy will ask you to confirm...") instead of blocking
  on the interactive first-import confirmation prompt — `--dry-run` never performs the irreversible
  action that prompt guards, so it can't hang waiting for input. **Not live-tested this run** — no
  practical way to construct a genuine "never deployed here before" target against the two real
  configured environments (both already have `Cr07982`). Covered by
  `DeployCommandFirstImportTests.cs` instead (unit-level, same pattern this doc already uses for the
  C#-keyword-solution-name case).
- `--dry-run` combined with `--no-delete`: confirm orphan cleanup still only reports, never deletes —
  `--dry-run` alone already forces report-only mode (`ResolveRunMode` gives dry-run precedence over
  `--no-delete`/managed), so this combo should behave identically to `--dry-run` alone. **Live-verified**:
  ran the same PROD scenario with `--no-delete` added — identical output (same 2 orphans, same
  `(--dry-run preview)` hint, not `(--no-delete active)`), confirming dry-run precedence.
- `flowline drift <target>` as an additional, even-lighter-weight read-only preview alongside
  `deploy --dry-run` — confirm it still works against both DEV and PROD with zero drift on a clean
  repo. **Live-verified against PROD only** (partial): `drift prod` reported the same 2 orphans as
  `deploy --dry-run`'s report, consistent between the two mechanisms, exit 15 (drift found). Not yet
  run against DEV or on a genuinely clean/zero-drift repo state.
- **Operational blocker — resolved (2026-07-23)**: the previous run hit two PAC auth profiles
  resolving to the same Dev URL, blocking the DTAP gate's predecessor check (worked around with
  `--skip-dtap-check`). The user removed the duplicate profile between sessions. **Live-verified this
  run**: `deploy prod --dry-run` (no `--skip-dtap-check`) now runs the real predecessor check —
  correctly rejected when Dev was at solution version 2.0.3.0 but the local package's DTAP gate
  version was 2.0.2.0 (a real, expected mismatch at that point in testing, not a bug). See "Operational
  notes" below for the full profile-ambiguity resolution and the `status` fix it surfaced.
- **New bug found and fixed this run** (unrelated to `--dry-run` itself, surfaced by verbose-mode
  testing): `console.Verbose(...)` leaked raw `[bold]...[/]` Spectre markup tags when fed
  `ConsolePath.ShortenPath`'s output (the "Loaded N PAC auth profile(s) from ..." line). Root cause,
  fix, and live re-verification: `docs/test-findings/verbose-shortenpath-leaks-raw-markup-tags.md`.
- **Re-verified live 2026-07-29, and the earlier `--force drift`/`--skip-dtap-check` workarounds are no
  longer needed**: `deploy prod --dry-run` with **no** force/skip flags ran the whole pipeline clean —
  DTAP gate (Dev 2.0.5 vs local 2.0.5), git-clean, local drift, fresh pack, solution checker
  ("3 findings, 0 Critical"), a real `pac admin backup` labelled exactly
  `flowline-dryrun-Cr07982-20260729T073411Z`, and an orphan report of 2 Prio1 orphans with the
  `(--dry-run preview)` hint. Final line `🚀 Dry run complete — 'Cr07982' would deploy cleanly to
  AutomateValue.`, exit 0. Grepped the same output for `solution import`, `##vso`, and `GITHUB_OUTPUT`
  — none present, so `--dry-run` still performs no import and emits no CI artifact signal. The
  `--force drift` that earlier sessions needed was **the phantom-drift bug**
  (`docs/test-findings/webresource-drift-solution-name-derived-from-repo-folder.md`), not an
  environment quirk.
- `drift dev` **live-verified 2026-07-29** (the DEV half that was previously untested): reports orphans
  correctly and exits 15 when it finds any. Worth knowing for both agents and humans — drift compares
  Dataverse against the **committed `Solution/src/`**, not against local build output, so anything
  pushed but not yet `sync`ed shows up as an orphan. That is correct behavior, not a false positive.

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
- **Fixed and live-verified (2026-07-23)**: a classic (non-package) plugin assembly whose `.NET
  AssemblyName` contains a period used to get a **false-positive orphan flag** in `sync`/`push`/`deploy`
  drift checks — potentially dangerous (`--force delete-orphans` could delete a live registration).
  `PluginWebResourceDriftChecker` now resolves each unpacked `PluginAssemblies/` DLL to its true name
  (companion `<dll>.data.xml` `FullName`, simple name before the first comma) before comparing, falling
  back to the on-disk filename when metadata is absent/unreadable — so `Cr07982.LegacyPlugins` (unpacked
  dot-stripped as `Cr07982LegacyPlugins.dll`) now matches build output and is no longer flagged. 5 new
  regression tests, full suite green (bar the 2 pre-existing live-MSAL `ConnectToDataverseAsync_*`
  failures, unrelated). Fix rebuilt + reinstalled + confirmed present in the running binary this run.
  **Live-verified against real PAC output**: with Flowline's own MSAL session expired, the check was
  routed through PAC's still-valid connection — `pac solution export --name Cr07982` (DEV) +
  `pac solution unpack`. Real output confirmed the exact bug shape: `Cr07982.LegacyPlugins` unpacks
  dot-stripped to `Cr07982LegacyPlugins.dll`, its `.data.xml` carries `FullName` as an attribute on the
  root `<PluginAssembly>` element (`Cr07982.LegacyPlugins, Version=1.0.0.0, ...`) plus a UTF-8 BOM. A
  throwaway probe drove the **deployed** `CheckAsync` against the real exported folder → **empty (no
  orphan)**; discriminating (without the fix the dot-stripped key wouldn't match). Full `sync`/`push`/`drift`
  command runs still pending Flowline auth, but they invoke the exact code path the probe exercised.
  Uncommitted in the source working tree; awaits explicit commit authorization. Details:
  `docs/test-findings/false-positive-orphan-dotted-classic-assembly-name.md` (file since deleted with
  the other resolved findings — see the note in Operational notes).
- **Whole section live-verified end-to-end on 2026-07-29** with the fixture described under
  "Environment" above, all in real `push` runs against DEV:
  - Move+rename of **both** projects: discovered purely through the solution file
    (`Building Backend…`, `Building ClientAssets…`), built, and registered under the new package name
    `av_Cr07982.Backend`. No folder-name convention involved.
  - **Two plugin projects, mixed shapes, one push**: `Cr07982.Backend` (nupkg) and
    `Cr07982.LegacyPlugins` (classic, signed, plain `.dll`) both planned and registered independently
    in a single `flowline push --scope plugins`, each with its own plan tree —
    `PluginPackageMode.Auto` resolved per project as documented.
  - Orphan detection across the rename named the stale assemblies (`Plugins.dll`,
    `Cr07982.Plugins.dll`) and their cascades, and gated deletion behind `--force delete-orphans`
    (report-only, exit 0, without it). **But the deletion itself is broken for package-owned orphans**
    — see `docs/test-findings/push-delete-orphans-fails-on-package-owned-assembly.md`.
  - Zero plugin projects, zero WebResources projects, and the tied two-candidate WebResources
    ambiguity all behaved exactly as this section specifies (details in Operational notes, session 4).
  - Note on the classic path: because push's per-assembly orphan check runs for the classic assembly
    only, a nupkg-mode push on its own never emits these warnings. That is the documented design
    (`PluginService.cs:273-276`), not a gap — deploy's orphan cleanup covers the package case.

## Output modes: run every phase both without and with `-v`/`--verbose`

Every command in the test matrix above gets run twice: once plain, once with `--verbose`. These
exercise different UX contracts and both need explicit judgment, not just "did it not crash":

- **Without `--verbose`** — this is what a normal user sees. Judge it as a UX reviewer would: is the
  output clean, well-formatted, free of noise/clutter, and does it read as polished/professional
  output? Flag anything that looks unfinished, inconsistent, or like leaked internal detail.
- **With `--verbose`** — this exposes the real step-by-step work (`VerboseRenderable` output that's
  otherwise filtered from the console by `VerboseFilterHook`
  (`src/Flowline.Core/Console/VerboseFilterHook.cs`)). Confirm the extra detail is accurate, actually
  reflects the real Dataverse calls/results made, and isn't just restating the non-verbose summary.
- **Possible finding, not confirmed this run (2026-07-23)**: verbose `push` output for a WebResources
  project with a rollup build step showed mojibake (`ΓåÆ` instead of `→`) and literal ANSI escape codes
  in the passed-through npm/rollup build log. Couldn't conclusively attribute this to Flowline vs. this
  session's own Bash-tool terminal (cp437) — needs re-confirmation from a real interactive terminal
  before treating as a real bug. `docs/test-findings/verbose-build-output-mojibake-cp437.md`.
- `--verbose` is a convenience, not a requirement for auditing: every run also writes a full log file
  (path is printed at the end of every invocation, verbose or not — see
  `FlowlineStoragePaths.GetLogsPath` via `src/Flowline/Program.cs`), and `LoggingRenderHook`
  (`src/Flowline.Core/Console/LoggingRenderHook.cs`) captures `VerboseRenderable` content into that
  log regardless of the console's verbose filter. So: for a subset of runs, skip `--verbose` entirely
  and instead open the printed log file afterward to confirm the same step-by-step detail is present
  there — this validates the "log has everything" guarantee independently of the console flag.

## Agent UX: judge every command as an AI coding agent would consume it

A large share of Flowline invocations happen from an AI agent (Claude Code, Copilot, an unattended CI
agent), not a human at a terminal. That is a *different* consumer with different failure modes than
the plain/`--verbose` human split above, so it gets its own explicit pass: for every command in the
matrix, ask "could an agent drive this reliably, unattended, and correctly interpret what came back?"

Test-run this pass from the agent's own position (the agent running this test goal *is* the consumer —
use that directly as evidence, don't simulate it).

What to check, and what counts as a defect:

- **Never blocks.** No command may wait on an interactive prompt when stdin isn't a TTY. Every
  confirmation gate must have a flag equivalent (`--force <specifier>`, `--dry-run`,
  `-a/--auto-select-auth-profile`), an invalid `--force` value must list the valid specifiers, and a
  gated hazard must *fail with the flag named* rather than hang. A hang is the single worst agent-UX
  failure — it burns the whole session, not one command.
- **Exit codes are the agent's primary signal.** They must be distinct, stable across runs, and mean
  the same thing every time (e.g. 3 not-found, 11 no-project/no-repo, 12 dirty tree, 15 validation).
  Record the exit code for every case in the matrix; flag any two genuinely different failure classes
  that collapse onto one code, and any success path that returns non-zero (`drift` returning 15 when
  drift is *found* is intentional — document it as such so agents don't read it as an error).
- **Output survives capture.** An agent captures stdout/stderr as plain text with no TTY. Anything the
  agent must extract programmatically — log file paths, environment URLs, solution names, version
  numbers — must survive that capture as a single unbroken token. **Known-bad today: Spectre wraps at
  ~80 columns when not attached to a TTY, splitting the printed log path and the example URLs in
  `--help` mid-token across lines.** Judge how bad that is per command and whether a
  width/no-wrap/`NO_COLOR`-style escape hatch exists.
- **Machine-readable mode.** Check whether any command offers structured output (`--json`, porcelain,
  or an exit-code-plus-file contract) and, if not, note where an agent is forced into brittle prose
  parsing — the drift/orphan report and the push plan tree are the highest-value candidates.
- **Working-directory coupling.** Commands act on the current directory with no `--project`/`--path`
  argument, and some agent harnesses reset cwd between calls, so every invocation needs its own
  `cd`/`Push-Location` wrapper. Confirm whether that is genuinely unavoidable and whether errors name
  the *exact* folder they searched (they must — that is what lets an agent self-correct).
- **Errors must be actionable verbatim.** The message should contain the literal next command
  (`git init`, `pac auth create --url <url>`, `dotnet sln add`, `--force delete-orphans`), not a
  description of one. An agent copies the named command; a paraphrase makes it guess.
- **Repeated runs must be honest.** Idempotent re-runs have to say "already up to date / skipping", not
  silently re-do work — an agent has no way to tell a no-op from a repeat write except by the text.
  Watch for cached pre-flight checks changing the output between two otherwise-identical runs (the git
  repo/remote check is TTL-cached, so the "No remote configured" warning appears on the first run and
  vanishes on the next — correct by design, but an agent diffing two runs will notice).
- **`clone` generates `AGENTS.md`** into the user's project. Review it as the artifact an agent will
  actually be steered by: is it accurate against current CLI behavior, and does it tell an agent the
  right command order and the hazards?

### First pass, 2026-07-29 — what this surface already gets right, and the two real gaps

Judged from an agent actually driving the whole matrix through captured stdout, no TTY.

Good, and worth not regressing:

- **Nothing ever blocked.** Every gate hit this run — `--force recreate-assembly`,
  `--force delete-orphans`, `--force config` on `sync --managed`, `--force drift`, first-import —
  failed fast with the flag named in the message instead of waiting on a prompt.
- **Exit codes are usable as the primary signal** and were stable across ~40 invocations:
  3 not-found, 11 no-project / bad config / ambiguous layout, 12 dirty tree, 15 validation,
  17 force-required. `drift` returning 15 *because drift was found* is the one that needs documenting
  rather than fixing — it is not an error.
- **Errors name the literal next command** (`git init`, `dotnet sln add`, `--force <specifier>`,
  `pac auth create --url …`), which is exactly what lets an agent self-correct without guessing.
- **Every run writes a full log** whose `[DBG]` content matches what `--verbose` would have shown
  (verified against a plain `deploy` run's log), and it scrubs URLs/emails to hashes. An agent can skip
  `--verbose` entirely and read the log.
- **`AGENTS.md` is accurate**, including the line saying the solution file outranks the folder list —
  which is what keeps an agent from "fixing" a deliberately moved project back.

Gaps found:

- **Output is hard-wrapped at 80 columns with no escape hatch**, splitting the printed log path and
  `--help`'s example URLs mid-token. `COLUMNS` has no effect.
  `docs/test-findings/agent-ux-output-hard-wrapped-at-80-columns.md`.
- **No machine-readable mode anywhere** — no `--json`/porcelain on drift, the orphan report, or the
  push plan, so prose parsing is the only option and the prose is reflowed. Same finding.
- Minor, no finding filed: commands act on the current directory with no `--project`/`--path`, so an
  agent whose harness resets cwd must wrap every call (`Push-Location`); and PAC's pass-through
  progress lines (`Syncing solution… (execution time: …)`) become ~60 near-identical lines in a
  captured `sync`, since the spinner cannot repaint without a TTY.

## Operational notes

- **PAC MSAL auth blocker — RESOLVED (2026-07-29), don't plan around it any more.** Sessions 2-3
  (2026-07-23) were blocked entirely: Flowline failed every connect with `Session expired for
  'remy@automatevalue.com'` because PAC 2.9's own connect/refresh never wrote user tokens back to the
  shared `tokencache_msalv3.dat` that `DataverseConnector` read, and the suspected cause was a WAM/OS
  broker Flowline didn't opt into. That suspicion was right and the fix has since landed
  (`e22b6df fix(auth): connect via WAM broker for PAC OperatingSystem-type profiles`). **Live state
  2026-07-29**: one unnamed `UNIVERSAL` profile of kind `OperatingSystem` (`remy@automatevalue.com`);
  `flowline status` connects to **both** Dev and Prod first try, no browser login, no `pac auth create`.
  Every live phase of this run went through it. Verbose output confirms the mechanism —
  `Token acquired silently for remy@automatevalue.com (expires …)`.
- **The `ConnectToDataverseAsync_*` tests now SKIP, they do not fail.** Baseline at HEAD is
  **1965 passed / 0 failed / 4 skipped**. Every older note in this document that says "bar the 2
  pre-existing live-MSAL failures" is stale — a red test is now a real regression, treat it as one.
- **Finding-file references in this document are historical.** Commit `403ecc7` deleted the finding
  files for issues that were fixed, so most paths cited in the sections above no longer exist
  (`clone-idempotent-reclone-*`, `push-form-events-*`, `standalone-push-pluginpackage-*`,
  `verbose-shortenpath-*`, `false-positive-orphan-dotted-*`, `pac-2.9-user-token-*`,
  `status-empty-profile-name-*`, `connect-verbose-empty-profile-name-*`). The prose describing each
  fix is kept because it is still true; the paths are not. `docs/test-findings/` is the authority for
  what is currently open.
- **Offline matrix re-verified this run (session 3, against the rebuilt binary w/ the dotted-classic
  fix)** — everything reachable without a Dataverse connection, all matching prior documented behavior:
  `clone` in a non-git folder → `No Git repo found...`, exit 11. `sln add` **5/5**: valid add (`✓ ...
  added`, exit 0), idempotent re-add (`↷ ... already in ... — skipping`, exit 0), wrong extension
  `.csproj` (`... is a C# project — use 'dotnet sln add'...`, exit 15), nonexistent path (`No project
  at '...' — check the path.`, exit 3), and the **walk-up regression** (subfolder w/ no solution file
  whose parent has one → errors naming the exact folder, parent `.slnx` untouched, exit 3). `push`
  standalone inside a `.flowline` project → `--pluginFile and --webresources cannot be used inside a
  Flowline project folder...`, exit 15; standalone missing/nonexistent plugin file → `Plugin file not
  found: ...`, exit 3. Confirmed ordering (not a bug): `deploy` resolves the project **before** target
  validation (folder w/ `.slnx` but no `.flowline` → `No Flowline project found`, exit 11), and `push`
  scope mutual-exclusion / scope-flag-mismatch run **after** auth resolution (hit session-expiry before
  the check) — both remain live-only. Not re-reachable offline: full clone/push/sync/deploy pipeline,
  dotted-classic live verify, verbose-vs-plain output judgment — all need live auth.
- **PAC auth profile ambiguity — resolved (2026-07-23)**: the user removed the duplicate PAC auth
  profile that caused the ambiguity block described below; the DTAP gate's predecessor check now runs
  live cleanly without `--skip-dtap-check` (confirmed: it correctly rejected a real version mismatch,
  Dev at 2.0.3.0 vs local gate version 2.0.2.0 — correct behavior, not a bug).
- **New bug found and fixed this run (2026-07-23)**: while cleaning up the duplicate profile, `flowline
  status` printed `PAC auth profile mismatch — active identity may not be ''` (empty quotes) for the
  one remaining unnamed profile — PAC's `authprofiles_v2.json` gives an unnamed profile `Name: ""`
  (empty string, not null), so `StatusCommand.FormatProfileNote`'s bare `??` chain never fell through
  to `User`. Fixed with an explicit `IsNullOrWhiteSpace`-based fallback; live re-verified (now prints
  the real user email). Not yet committed — needs explicit commit authorization.
  `docs/test-findings/status-empty-profile-name-breaks-fallback.md`.
- **Second instance of the same bug, found and fixed this run (2026-07-23)**: `--verbose` output for
  `push`/`sync`/`deploy` (any command connecting via PAC) showed `Connecting via PAC auth profile ''`
  for the same unnamed profile shape — same `Name ?? User` root cause, different call site
  (`DataverseConnector.ConnectViaPacAsync`). Fixed with an extracted `ResolveProfileLabel` helper,
  live re-verified. Not yet committed. `docs/test-findings/connect-verbose-empty-profile-name-shows-blank.md`.
  A proactive grep sweep for the same `Name ?? ...` pattern across `src/` (per the "discovered this
  one by accident" risk) found and fixed two more instances of the identical bug —
  `DataverseConnector.cs:233` (rare AADSTS tenant-mismatch error message) and `SecretResolver.cs`
  (service-principal client-secret prompt, `Name ?? ApplicationId`) — both unit-tested but not
  live-verified (their trigger conditions aren't practical to contrive against real Dataverse).
  Confirmed no further instances: `ProfileResolutionService.cs`/`PacUtils.cs` already handle this
  correctly.
- **Historical note (superseded by the fix above)**: this machine previously had multiple PAC auth
  profiles that could resolve to the same environment URL (an unnamed one and a named one). Commands
  error ("Multiple PAC auth profiles match ... run: pac auth select --index <n>") rather than guess —
  resolve with `pac auth select --index <n>` before proceeding, or pass
  `-a`/`--auto-select-auth-profile` to let Flowline switch automatically for that one command.
- **Run of 2026-07-29 (session 4, `C:\Code\FlowlineTryOutByClaude`, tool packed from HEAD +
  fixes)** — first fully live run since the auth fix. What it established, beyond the per-section
  notes above:
  - **Three bugs found and fixed, all with regression tests, suite green at 1974/0/4**:
    phantom web-resource drift (`docs/test-findings/webresource-drift-solution-name-derived-from-repo-folder.md`),
    `--version` reporting the 4-part file version (`docs/test-findings/version-flag-shows-file-version-not-package-version.md`),
    and raw `[bold]…[/]` markup leaking into two exception messages (`PluginService.cs:886`/`:902`).
  - **Three findings logged, not fixed**: dry-run summary omits the package/assembly content update;
    `push --force delete-orphans` fails on a package-owned orphan *after* deleting its children;
    stale-`.nupkg` guard trips on the normal build path after any MinVer bump. Plus one agent-UX
    finding (80-column hard wrap).
  - **The `--force drift` workaround in the deploy section above is now explained**: it was the
    phantom-drift bug, not an environment quirk. With the fix, `deploy prod --dry-run` runs the local
    drift check clean and needs neither `--force drift` nor `--skip-dtap-check`.
  - **Mojibake finding closed as not-ours** — reproduces with plain `dotnet build`, no Flowline in the
    chain (`docs/test-findings/verbose-build-output-mojibake-cp437.md`).
  - **Gates verified live**: `--force recreate-assembly` (exit 17, message names the flag),
    `--force delete-orphans` (report-only without it, exit 0), `sync --managed` against a conflicting
    `IncludeManaged` (exit 17, `Use --force config`, rejected **before** the ~4-minute export, no
    hang), two tied WebResources candidates (exit 11, names both projects), zero plugin projects
    (default scope skips silently, explicit `--scope plugins` → exit 3), zero WebResources projects
    (loud warning, plugins still pushed).
  - **Idempotent re-clone over the moved/renamed fixture**: correctly skipped both projects, created
    no duplicate `Plugins/`/`WebResources/`, left the solution file untouched, and self-healed the
    missing `AGENTS.md`/`CLAUDE.md`. The 2026-07-23 fix holds on this machine too.
  - **Not covered this run**: `--bump minor|major`, `--managed` on `clone`, the C#-keyword
    solution-name rejection, first-import `--dry-run` against a target that has never seen the
    solution, and a systematic plain-vs-`--verbose` pass over *every* command (verbose was judged on
    `push --scope webresources` and `drift dev` only).
- Git hygiene in the test workspace: commit between test phases so `sync`'s dirty-check behaves
  predictably, and use `git checkout --`/`git status` before any destructive reset.
- Long-running commands (`clone`'s Dataverse export, `sync`'s export) can take several minutes — run
  them in the background and wait for completion rather than assuming a short timeout means failure.

## Way of working: unfixed findings

Every bug/issue found that isn't fixed inline (per the bug-fix policy above) gets its own file in
`docs/test-findings/`, named by slug (e.g. `false-positive-orphan-dotted-classic-assembly-name.md`),
not bundled into a single report. Each file should cover:

- **Status** (fixed/not fixed) and **severity**.
- **Repro** — exact steps/commands.
- **Root cause**, as far as it's understood.
- **Suggested fix direction**, if any, and why it wasn't attempted inline.

This test-goal document only ever *references* a finding file by path and a one-line summary — it
does not duplicate the full writeup. Before starting a new run, skim `docs/test-findings/` for
issues that might now be fixed (re-verify, then delete or update the file accordingly) and check
whether any still-open finding should be promoted to a fix this run instead.

## Deliverable

A findings report: what was tested, what passed, what failed and was fixed (with the fix and its
regression test), and a `docs/test-findings/<slug>.md` file for each finding that needed human
judgment instead. Update this file with anything newly learned before the next run.

The report must also state, explicitly and without hedging, **what was not covered** — every matrix
item skipped and why. A run that reports only its successes is indistinguishable from a run that
covered everything, which is the failure mode this whole document exists to prevent.
