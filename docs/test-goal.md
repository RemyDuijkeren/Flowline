# Live test — changes since 0.16.0

Manual end-to-end exercise of the work released after tag `0.16.0`, run against a real Dataverse
environment and real folders rather than only through the unit suite.

- **Date:** 2026-08-15
- **Build:** `src/Flowline/Flowline.csproj`, Release, at commit `4409fc4` (`0.16.1-alpha.0.68`)
- **Range under test:** `0.16.0..HEAD` — 70 commits, matching the CHANGELOG `[Unreleased]` section
- **Test project:** `FlowlineTests/solutions/FlowlineDeployTest`, outside this repo
- **Target:** the DEV environment. PROD and TEST were never written to.
- **Writes:** none. Every Dataverse call in this round was read-only (`drift`, `status`).

The earlier round of this document covered one post-0.16.0 feature — the deploy missing-component
gate — against TEST. Those results are kept below rather than discarded; the feature is in the same
range and the runs still stand.

## Results

| # | Case | Expected | Observed | Exit |
|---|---|---|---|---|
| A | `drift dev` on a project with a deliberately orphaned Custom API | Orphan flagged with a provenance verdict naming the removal commit | `CustomApi 'av_KeepMe'` reported at Prio2, with author, date, and the commit subject as the stated reason | 15 |
| B | Resolved-profile line | Names the environment, not the profile | `Resolved PAC auth profile #1 (unnamed, UNIVERSAL) — AutomateValue Dev (…)` | 0 |
| C | `scaffold webresources` in an empty folder | Writes the template alone, generic project name | 8 files, `WebResources/WebResources.csproj`, no solution file, no `.flowline` | 0 |
| D | Re-run over the same folder | Reports already-there, writes nothing | `↷ WebResources project already there — skipping` | 0 |
| E | Folder holding a stray `tsconfig.json`, no project file | Refuse by name, write nothing | Refused naming `tsconfig.json`; the file's contents unchanged; no other file created | 11 |
| F | `scaffold plugins` | Reject, naming accepted values | `'plugins' isn't something scaffold can write — pass one of: webresources.` | 15 |
| G | `scaffold webresources` inside a project | Name after the solution, register in the solution file | `Contoso.WebResources.csproj`, one entry added to `Contoso.slnx` | 0 |
| H | Same, run from `src/deep/nested` | Resolve upward, write at the project root | `✓ Flowline project: ../../../` — 9 entries at the root, **0** in the nested folder | 0 |
| I | `new webresources` (alias) | Identical to `scaffold` | Same output and same files | 0 |
| J | `push --scope formevents` (removed) | Rejected, naming valid values | `Failed to convert 'formevents' to PushScope[]. Valid values are 'None', 'AssemblyOnly', 'Plugins', 'WebResources', 'All'` | 15 |

## Case A is the one worth trusting

The orphan provenance verdict is the largest thing in this range, and it is the hardest to fake.
`FlowlineDeployTest` carries a commit that removed a Custom API from source specifically to leave a
survivor in the environment. `drift dev` found it and answered *why*:

```
Orphan components (1):
  Prio2 — still running deleted logic:
    CustomApi 'av_KeepMe' (27b8f322-941b-49b4-a681-74cd94f12a09) — would delete
      Removed by RemyDuijkeren on 2026-08-01 — "Remove av_KeepMe from source —
      probe: drift must flag it live in TEST (survivor proof)"
```

The commit subject is the entire point of the feature. *Removed on purpose* and *never yours* are
indistinguishable from the orphan alone, and the operator was closing that gap from memory. Here the
reason for the removal is read out of the repository's own history and printed next to the component
that would be deleted.

**The filtering held.** The run checked 2 orphan candidates and reported 1. A pass that reported both
would be indistinguishable from one that echoes its input; reporting one means the comparison
actually resolved against the environment and the checkout.

## Case H is the one that justifies the mode announcement

`scaffold` finds a Flowline project by walking *upward*, which means a run from a subdirectory can
land in project mode when the folder in front of you looks empty. Run from `src/deep/nested`, it
announced `Flowline project: ../../../`, wrote nine entries at the project root, and wrote nothing at
all in the nested folder.

That behaviour is correct and would be surprising without the announcement — which is why the mode is
stated before anything is written rather than inferred afterwards from where the files landed.

## Case E — the collision guard

The already-there check only sees the project file. A folder holding template-named files *without*
one sails past it, and the template writer replaces files rather than merging them. Case E put a
hand-written `tsconfig.json` in an otherwise empty `WebResources/`:

```
Error: WebResources\tsconfig.json is already here and scaffold won't write over it
       — move it aside, or scaffold somewhere else.
```

Exit 11, the file byte-identical afterwards, and no other template file created — the check runs
before the first write, so a refusal leaves nothing half-written. There is deliberately no `--force`
to overrule it.

## No-network claim, observed rather than asserted

`scaffold` claims it reaches nothing: no Dataverse, no authentication, no network, not even the
update check. That is observable in the output rather than only in the code.

Every command that runs the standard probe opens with it — the `drift` run in case A begins
`Checking your setup...` and then `✓ Prerequisites all good, let's go!`. **No `scaffold` run in cases
C through I printed either line.** The probe is where the PAC CLI check, the git-repo check, and the
NuGet update call live, so its absence is the claim demonstrated.

Case F is the sharper version: `scaffold plugins` returned exit 15 from a bare temp directory that
was not a git repo and had no `.flowline` — the argument was rejected without any of those
prerequisites being consulted.

## What these runs confirmed

- Orphan provenance resolves against real git history and renders author, date, and subject (A).
- The comparison filters rather than echoes — 2 candidates in, 1 orphan out (A).
- The resolved-profile line names the environment it resolved for (B).
- `scaffold` writes the template with no Dataverse call, no auth, and no prerequisite probe (C–I).
- Both `scaffold` modes work, and the upward resolution is announced before it surprises anyone (G, H).
- Nothing is written over: an existing project skips, a stray template file refuses (D, E).
- The `new` alias is a true alias, not a near-duplicate (I).
- `--scope formevents` is gone from both the help surface and the parser, and is rejected before any
  project or Dataverse work (J).
- Verbose mode carries tool versions after their check lines, per the verbose rules.

## Not covered

Everything below needs a **write** to a live environment. This round deliberately made none, so these
remain unexercised here.

- **`push` warns which components depend on a web resource** before deleting or removing it.
- **`push` refuses a web resource another solution owns** (breaking change).
- **`push --no-build` no longer pushes a stale plugin package**, and `push` no longer re-uploads an
  unchanged one — both need a real push to observe.
- **`push --no-publish` warns whenever nothing publishes.**
- **Verbose side-by-side snapshot trees** — needs a push to render.
- **`deploy` managed-unpack fix** and **the orphan report's managed-upgrade wording**.
- **Dataverse request timeout reporting** — not reproducible on demand; it needs a slow environment.
- **`[PreImage]` on `CreateMultiple` rejection** and the corrected "entity images not supported"
  message — both are attribute-validation paths covered by the unit suite, not reachable from the CLI
  without a plugin assembly built for the purpose.

**The update notice** was exercised but not observed firing: the local build (`0.16.1-alpha.0.68`) is
ahead of anything published, so a channel-matched comparison correctly prints nothing. The path ran —
`status --verbose --no-cache` forced a fresh check — but a positive result needs a published version
newer than the running one.

---

## Earlier round — deploy missing-component preflight gate

Retained from the 2026-08-09 run against TEST. Same release range; the results still stand.

- **Build:** Release, at commit `4097bdf`
- **Target:** TEST. PROD was never contacted.
- **Import:** never performed — every run used `--dry-run`, which stops before the import.

Runs used `--no-backup --skip-solution-check --skip-dtap-check` so the new gate was the only
pre-import work doing anything, and no environment backup was taken.

| # | Case | Expected | Observed | Exit |
|---|---|---|---|---|
| A | Target has everything the solution needs | Gate passes, deploy continues | `✓ No missing components.` — one verdict line, deploy proceeded to completion | 0 |
| B | Target missing components | Block before import, name them, write report | Blocked; named 2 components with type, owning solution, and what required each; wrote the report | 15 |
| C | `--skip-component-check` after a block | Gate does not run; its stale report is cleared | No gate line in the output; the target's report removed; a second target's report untouched | 0 |
| D | Malformed required-component list | Fail as "check couldn't run", distinct from a block | `Error: Missing-component check couldn't run against the target (…). Use --skip-component-check to deploy without it.` | 10 |

Case D was not planned — it happened when the platform rejected a first, badly-shaped injection.
It is the more valuable accident: it exercised the "no verdict" path against a live service and
confirmed it is reported and exit-coded separately from a real block.

### The negative control

Case B is the result worth trusting. Three fabricated dependencies were injected into the
solution source; **two** were reported. The third, `msdyn_iotdevice`, was silently resolved because
TEST genuinely has it.

That is the whole claim of the feature demonstrated in one run: the gate is filtering the
solution's required-component list against the live target, not echoing the file back. A gate that
reported all three would have been indistinguishable from one that reads the file and prints it.

### Observed output (case B)

```
Checking target for missing components...
Error: Target is missing 2 required components — deploy stopped before import.
  Flowline Probe Absent Entity (flowline_probe_absent_entity) (Entity) — in 'FlowlineProbeSolution (1.0.0.0)', required by Flowline Probe Absent Entity (flowline_probe_absent_entity)
  Flowline Probe Absent Two (flowline_probe_absent_two) (Entity) — in 'FlowlineProbeSolution (1.0.0.0)', required by Flowline Probe Absent Two (flowline_probe_absent_two)
Full list: …\artifacts\missing-components-automatevalue-test-crm4-dynamics-com.txt
Fix it: install the missing solution or application in the target, or remove the dependent component from the solution in DEV and run 'flowline sync'.
Last resort: --skip-component-check deploys without this check.
```

The filename carries the target, and the header names solution, target, and UTC time — both fixes
for the review finding that one report was being shared across every promotion stage.

### Payload size

The open question was whether a large solution exceeds the inline message limit, forcing teams to
disable the gate permanently. Measured against TEST by padding one real solution with an
incompressible entry, so every zip carried an identical required-component list (12,786 entries)
and only size varied:

| Payload | Duration | Result |
|---|---|---|
| 0.5 MB | 5.7 s | 0 missing |
| 1.5 MB | 6.0 s | 0 missing |
| 8.5 MB | 26.5 s | 0 missing |
| 32.5 MB | 111.7 s | 0 missing |
| 64.5 MB | 216.9 s | 0 missing |

**Nothing was rejected at any size.** The concern was the wrong shape — there is no observed ceiling
up to 64.5 MB, so nothing forces a team onto the skip flag. The real cost is duration: a 64 MB
solution adds over three and a half minutes to every deploy.

It also corrects an assumption the plan carried. Cost was expected to track the number of required
components; it tracks payload size. Every zip above held the same dependency list, and going from
0.5 MB to 8.5 MB still moved the call from ~6s to ~27s.

Two changes followed:

- The failure message no longer claims a large payload "may exceed the inline message limit" — that
  limit was never observed. It names the size and points at duration, which makes a client timeout
  the plausible cause instead.
- Above 8 MB the spinner label carries the size and says the wait runs to minutes, so a slow check
  does not read as a hang.

Still untested at size: whether the check and `pac solution import` diverge somewhere beyond
64.5 MB, and how the check behaves when dependency count and payload size are large independently —
they were never varied separately above 0.5 MB.

### Not covered by that round

- **False-positive rate.** Every component the gate reported was one that was genuinely absent. No
  case has been observed where it reports something the target actually has, and the gate blocks by
  design, so a false positive is a hard stop.
- **A real import.** All runs were `--dry-run`. The gate's interaction with an actual import — in
  particular whether a blocked import would have failed the way the gate predicts — is untested.
- **Ordering against the solution checker and backup specifically.** Both were skipped to isolate the
  gate. Their relative order is covered by the unit test that resolves the real DI container.

---

## Build configuration matters when testing the CLI

A `FlowlineException` from a pre-import gate first appeared as `Unhandled exception. …` with a full
stack trace, which read like broken error handling. It was not. `Program.cs` calls
`config.PropagateExceptions()` inside `#if DEBUG`, and propagation beats the `SetExceptionHandler`
that renders `Error: <message>` with a typed `ExitCode`. The same commit built `-c Release` produced
the correct output.

**Verify user-facing CLI output from a Release build.** A Debug build makes correct error handling
look broken. This is now recorded in `AGENTS.md` under Build and verification.

Every exit code in both result tables above was read from a Release build for this reason.

## Cleanup

`FlowlineDeployTest` was left exactly as found: clean working tree, no commits, nothing written to
any environment. Every folder used for the `scaffold` cases was a fresh temp directory.

One residue carried from the earlier round: the test project's cached artifact (`artifacts/*.zip` and
its manifest) records a commit that no longer exists. That is gitignored build output, and the next
deploy detects the mismatch and repacks — no action needed.
