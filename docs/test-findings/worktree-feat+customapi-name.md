# Residual review findings — CustomApi UniqueName override

Source: `ce-code-review` (mode:agent), run `20260713-171452-b944303f`, branch `worktree-feat+customapi-name`,
head `437cfee` at time of review. 8 reviewers (correctness, testing, maintainability, project-standards,
agent-native, learnings, adversarial, api-contract) plus a Codex cross-model adversarial pass.

Two actionable gaps were found and fixed during this review (see commits `798b806` and `437cfee`):
a missing assertion on the redundancy-warning console output, and a missing test for a pure
derived-vs-derived name collision. Everything below is either a rejected false positive or an
accepted, pre-documented scope limitation — no further action planned.

## Re-verified 2026-07-24 — all still open, none acted on

Re-checked every item below against the current codebase (file `PluginAssemblyReader.cs` was since
split — the validators now live in `PluginTypeMetadataScanner.cs`, line numbers shifted, no other
drift). All six still hold exactly as documented; nothing here was fixed incidentally by later work.

**Fixed 2026-07-24 — exception routing (see "Fixed" section below).**

**Recommendation — leave as documented, don't fix:**

- **Cross-assembly / unrelated-live-record collision.** Explicit scope boundary in the approved plan,
  loud (not silent) failure mode, narrower residual already rated advisory. Fixing it well means
  querying every live Custom API on every push, not just this assembly's — real cost for a rejection
  that already surfaces clearly at push time.
- **Case-sensitive comparison.** Genuinely can't be fixed correctly without confirming Dataverse's
  actual `uniquename` collation live first — fixing blind risks encoding wrong behavior.
- **`resolved[pluginType.FullName]` invariant.** Already enforced by the C# type system
  (`AllowMultiple = false` on `CustomApiAttribute`) — the failure mode requires an attribute contract
  change that hasn't happened and isn't planned.

**Recommendation — low priority, optional:**

- **Format-validation tests bypass `Analyze()`.** Real gap but matches a pre-existing, accepted
  convention (`ValidateSecondaryTable` has the same shape). Worth an end-to-end regression test if
  someone's touching this area anyway; not worth a standalone task.

## Fixed 2026-07-24

**Exception routing bypasses the clean `FlowlineException` CLI error path — fixed.**

`PluginService.AnalyzeAssembly` (new private helper, `PluginService.cs`) now wraps the
`_assemblyReader.Analyze(dllPath)` call and catches `InvalidOperationException`, rewrapping it as
`FlowlineException(ExitCode.ValidationFailed, ex.Message, ex)`. Both public dllPath-taking call sites
(`SyncAssemblyOnlyAsync`, `SyncSolutionAsync`) route through it. This is the single choke point for
every `Validate*` throw in `PluginTypeMetadataScanner.cs` (~20 sites, all plain
`InvalidOperationException` by convention) — the fix covers all of them, not just CustomApi's, without
touching any of those throw sites. Regression test:
`PluginServiceTests.SyncAssemblyOnlyAsync_InvalidCustomApiUniqueNameFormat_ThrowsFlowlineExceptionWithOriginalMessage`
builds a real minimal plugin assembly on disk with an invalid `[CustomApi(UniqueName = ...)]` and
drives it through the actual `Analyze()` reflection pipeline (not a direct validator call), proving the
rewrap end-to-end.

## Rejected — false positive

**"Double-bracket typo `[[CustomApi]]`" (correctness P3/conf 100, maintainability P3/conf 100)**

Two reviewers independently flagged `PluginPlanner.cs:461`'s `[[CustomApi]]` as a bracket typo that
should read `[CustomApi]`. This is intentional Spectre.Console markup escaping: `console.Warning(...)`
renders through `AnsiConsole.MarkupLine`, which parses single `[...]` as style tags, so a literal
bracket must be doubled — the same convention already used for `[[Handles]]` warnings elsewhere in
this file. Confirmed independently by the project-standards reviewer in this same run and by the
code-quality reviewer in the pre-review simplify pass. Not applied. **Re-verified 2026-07-24: still
present, now at `PluginPlanner.cs:545` — confirmed intentional markup escaping, still correctly
rejected.**

## Accepted — documented scope limitations (no fix planned)

**Cross-assembly / unrelated-live-record name collision not locally detected (adversarial P1/conf 75)**

`ResolveCustomApiNames`'s duplicate check (`PluginPlanner.cs:473-484`) only compares Custom APIs
declared within the current assembly against each other — it does not check a resolved name against
the full set of live Dataverse Custom APIs (`snapshot.CustomApis`) regardless of which plugin type
or assembly they belong to. An override that happens to match an unrelated, already-live Custom API
would be planned as a create and surface only as an opaque Dataverse uniqueness-constraint rejection
at push time.

Accepted as residual, not fixed, for three combined reasons: (1) the cross-assembly subset of this
is the scope boundary the approved plan states explicitly (Scope Boundaries, line 58 — "two different
plugin assemblies targeting the same solution with colliding unique names are not locally detectable
and would surface as a Dataverse-level rejection at push time instead"); (2) the remaining subset — a
collision with a manually-created or otherwise-unrelated live record within a single push — is a
narrower case the reviewer itself rated `advisory`/`owner: human`, not must-fix; (3) the failure mode
is a loud Dataverse create-rejection at push time, not silent data loss — the silent-data-loss risk
(wrongful delete of an adopted live record) is the one this feature explicitly guards against and is
covered by the `Plan_CustomApiUniqueNameOverride_MatchesExistingLiveRecord_UpdatesInPlace_NoDelete`
regression test. **Re-verified 2026-07-24: still true, now `PluginPlanner.cs:557-560`. Recommend
leaving as documented — see summary above.**

**Case-sensitive prefix/duplicate comparison (adversarial P2/conf 50)**

`PluginPlanner.cs:450` (`StartsWith(..., StringComparison.Ordinal)`) and `:474` (`GroupBy` with the
default ordinal comparer) don't account for a possible case-insensitive `uniquename` collation in
Dataverse. Below the review's confidence-gate threshold (anchor 50, not P0) — Dataverse's actual
collation behavior for `customapi.uniquename` is unconfirmed, so "fixing" this without verifying
against live Dataverse risks introducing behavior that doesn't match the server. Left as a residual
risk pending live verification, consistent with this project's existing convention of verifying
Dataverse behavior against a real environment before asserting it (see project memory
`feedback_verify_dataverse_live`). **Re-verified 2026-07-24: still true, now `PluginPlanner.cs:534`
and `:558`. Recommend leaving as documented — see summary above.**

**Format-validation tests bypass the real `Analyze()` reflection pipeline (testing P2/conf 75)**

All 7 new `ValidateCustomApiUniqueNameFormat` error-path tests call the validator directly as a
static method rather than through `Analyze()`/`TryBuildCustomApi`, so the production wiring call at
`PluginAssemblyReader.cs:218` isn't exercised by them (only the happy-path tests go through
`Analyze()`). Verified this matches a pre-existing, identical convention already used for
`ValidateSecondaryTable` in the same file (direct unit tests of the validator, no end-to-end
wiring test) — not a new gap introduced by this diff, just consistency with the established pattern.
**Re-verified 2026-07-24: still true — validators moved to `PluginTypeMetadataScanner.cs`, wiring call
now at `PluginTypeMetadataScanner.cs:146`, still not exercised by these tests. Recommend low
priority — see summary above.**

**`resolved[pluginType.FullName]` dictionary keyed by plugin type, relies on one-CustomApi-per-type
invariant (maintainability + correctness residual risk)**

Safe today only because `[CustomApi]` is `[AttributeUsage(AttributeTargets.Class)]` with default
`AllowMultiple = false` — a plugin type can carry at most one `CustomApiMetadata`. If ever relaxed,
the dictionary assignment would silently overwrite rather than error. Already documented as an
accepted trade-off in the plan (KTD4). No fix planned. **Re-verified 2026-07-24: still true, now
`PluginPlanner.cs:553`; `CustomApiAttribute.cs:57` confirms `AllowMultiple` still defaults to `false`.
Recommend leaving as documented — see summary above.**

**Exception routing bypasses the clean `FlowlineException` CLI error path** — moved to the "Fixed
2026-07-24" section above.
