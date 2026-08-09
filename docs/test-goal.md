# Live test — deploy missing-component preflight gate

Manual end-to-end exercise of the gate added on `feat/deploy-import-preflight`, run against a
real Dataverse environment rather than only through the unit suite.

- **Date:** 2026-08-09
- **Build:** `src/Flowline/Flowline.csproj`, Release, at commit `4097bdf`
- **Test project:** a Flowline project outside this repo (`FlowlineDeployTest`)
- **Target:** the TEST environment. PROD was never contacted.
- **Import:** never performed — every run used `--dry-run`, which stops before the import.

Runs used `--no-backup --skip-solution-check --skip-dtap-check` so the new gate was the only
pre-import work doing anything, and no environment backup was taken.

## Results

| # | Case | Expected | Observed | Exit |
|---|---|---|---|---|
| A | Target has everything the solution needs | Gate passes, deploy continues | `✓ No missing components.` — one verdict line, deploy proceeded to completion | 0 |
| B | Target missing components | Block before import, name them, write report | Blocked; named 2 components with type, owning solution, and what required each; wrote the report | 15 |
| C | `--skip-component-check` after a block | Gate does not run; its stale report is cleared | No gate line in the output; the target's report removed; a second target's report untouched | 0 |
| D | Malformed required-component list | Fail as "check couldn't run", distinct from a block | `Error: Missing-component check couldn't run against the target (…). Use --skip-component-check to deploy without it.` | 10 |

Case D was not planned — it happened when the platform rejected a first, badly-shaped injection.
It is the more valuable accident: it exercised the "no verdict" path against a live service and
confirmed it is reported and exit-coded separately from a real block.

## The negative control

Case B is the result worth trusting. Three fabricated dependencies were injected into the
solution source; **two** were reported. The third, `msdyn_iotdevice`, was silently resolved because
TEST genuinely has it.

That is the whole claim of the feature demonstrated in one run: the gate is filtering the
solution's required-component list against the live target, not echoing the file back. A gate that
reported all three would have been indistinguishable from one that reads the file and prints it.

## Observed output (case B)

```
Checking target for missing components...
Error: Target is missing 2 required components — deploy stopped before import.
  Flowline Probe Absent Entity (flowline_probe_absent_entity) (Entity) — in 'FlowlineProbeSolution (1.0.0.0)', required by Flowline Probe Absent Entity (flowline_probe_absent_entity)
  Flowline Probe Absent Two (flowline_probe_absent_two) (Entity) — in 'FlowlineProbeSolution (1.0.0.0)', required by Flowline Probe Absent Two (flowline_probe_absent_two)
Full list: …\artifacts\missing-components-automatevalue-test-crm4-dynamics-com.txt
Fix it: install the missing solution or application in the target, or remove the dependent component from the solution in DEV and run 'flowline sync'.
Last resort: --skip-component-check deploys without this check.
```

Report file:

```
# FlowlineDeployTest -> https://automatevalue-test.crm4.dynamics.com (2026-08-09 03:06:09Z)
1. Flowline Probe Absent Entity (flowline_probe_absent_entity) (Entity) — in 'FlowlineProbeSolution (1.0.0.0)', required by …
2. Flowline Probe Absent Two (flowline_probe_absent_two) (Entity) — in 'FlowlineProbeSolution (1.0.0.0)', required by …
```

The filename carries the target, and the header names solution, target, and UTC time — both fixes
for the review finding that one report was being shared across every promotion stage.

## What the runs confirmed

- The gate runs on the real pre-import path and blocks before anything is written.
- It filters against the live target (the negative control above).
- Identity renders as display name plus schema name with a type label and the owning solution.
  No GUID appeared in any output.
- A block and an unrunnable check are separately worded and separately exit-coded (15 vs 10).
- The report is scoped per target, and clearing one target's report leaves another's alone.
- Skipping the gate clears that target's stale report.
- Ordering held: the gate ran ahead of the other pre-import work in every run.

## Not covered

- **The inline payload ceiling.** The check sends the packed solution in one message; the import it
  guards uses chunked upload. The test solution is ~14 KB, nowhere near any limit. The discriminating
  test is a >50 MB artifact — compare `pac solution import` success against the check.
- **False-positive rate.** Every component the gate reported was one that was genuinely absent. No
  case has been observed where it reports something the target actually has, and the gate blocks by
  design, so a false positive is a hard stop.
- **A real import.** All runs were `--dry-run`. The gate's interaction with an actual import — in
  particular whether a blocked import would have failed the way the gate predicts — is untested.
- **Ordering against the solution checker and backup specifically.** Both were skipped to isolate the
  gate. Their relative order is covered by the unit test that resolves the real DI container.

## Unrelated bug found

`deploy --path <zip>` is broken, independently of this change. `ReadArtifactSolutionManifest`
(`src/Flowline/Commands/DeployCommand.cs`) looks for an `Other/Solution.xml` entry, but a packed
solution zip carries `solution.xml` at the root — `Other/Solution.xml` is the *unpacked source*
layout. Every real packed artifact fails the manifest read:

```
Error: No Other/Solution.xml entry found in artifact '…\FlowlineDeployTest_unmanaged.zip'
       — is this a valid packed solution zip?
```

Confirmed present on the base commit and untouched by this branch. Not fixed here — it is outside
this change's scope.

## Cleanup

The test project was restored to the commit it started at, with a clean working tree and the
injected dependencies removed.

One residue: its cached artifact (`artifacts/*.zip` and its manifest) was rebuilt during testing and
now records a commit that no longer exists. That is gitignored build output, and the next deploy
detects the mismatch and repacks — no action needed.
