# Missing-component gate misses a step's plugin-type reference

- **Status:** open, not fixed inline
- **Severity:** medium — the gate reports clean and the import fails minutes later on exactly the
  class of problem the gate exists to catch early
- **Found:** 2026-08-24, round "does a step bound to an unregistered package assembly fail the
  import?" in [`../end-to-end-test-log.md`](../end-to-end-test-log.md)
- **Component:** `src/Flowline.Core/Deploy/MissingComponentCheckService.cs`

## Repro

1. A target holding a plug-in package whose content carries an assembly with no `pluginassembly`
   record — the state documented in
   [`../solutions/integration-issues/dataverse-plugin-package-assembly-not-registered-on-update.md`](../solutions/integration-issues/dataverse-plugin-package-assembly-not-registered-on-update.md).
2. A solution zip carrying a step whose `PluginTypeExportKey` names a plugin type of that assembly.
3. `flowline deploy <url> --path <zip>`.

Observed: `✓ No missing components.`, then the solution checker, then the backup, then the import
fails on `SDK Message Processing Steps import: FAILURE: A record for PluginType with hash value
<key> for PluginTypeExportKey is not found`. Exit 13.

Reproducible in one command from `C:\Code\FlowlineDeployTest\_fixture\probe-step.zip` while TEST
stays in its current state.

## Root cause

The gate asks the platform: it hands the whole zip to `RetrieveMissingComponents` and reports what
comes back. That API returned an empty list for a zip the same environment then refused to import.
So this is a blind spot in the platform's own dependency answer, not a filter Flowline applies on
top of it.

Unverified: whether `RetrieveMissingComponents` ignores type-92 components wholesale, or only fails
to follow the export-key hash to a plugin type. Worth knowing before designing anything, and
answerable with a second fixture rather than by reading code.

## Why it wasn't fixed inline

Nothing here is a code slip. Closing it means Flowline doing dependency resolution the platform API
declined to do — reading `SdkMessageProcessingSteps/*.xml` out of the zip, extracting each
`PluginTypeExportKey`, and querying `plugintype` in the target for each one. That is a new
capability with its own failure modes (a check that wrongly blocks a valid deploy is worse than the
one that lets this through), and it needs the question above answered first.

## Suggested direction

Two options, in order of cost:

1. **Extend the existing gate** with an export-key pass over the zip's step files. Small, targeted,
   and it turns a 10-minute failed import into an immediate refusal naming the assembly. Fails open
   on anything it cannot read, like the package-assembly check does.
2. **Repair rather than warn.** Under discussion for 0.19.0 — see the same solution doc. Two things
   these rounds settled about where a repair has to sit: the post-import check never runs when the
   import fails, so a repair placed after the import cannot help this case; and creating the
   `pluginassembly` record before the import is *sufficient* — the import's own content write then
   populates plugin types and the step resolves (round 2026-08-24b, case F). A repair is one
   `Create`, not a content upload.

Either way, the operator-facing fact is now established and worth stating in the message verbatim:
the target needs the `pluginassembly` record created under that package before the import, and that
record alone is enough — the same deploy then creates the plugin types and the step.
