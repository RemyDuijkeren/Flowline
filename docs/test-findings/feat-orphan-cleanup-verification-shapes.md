# Residual Review Findings — feat/orphan-cleanup-verification-shapes

Source: `ce-code-review` run against this branch (base `master` @ `64b836d`, head `87de48d`), 7-reviewer roster (correctness, testing, maintainability, project-standards, reliability, adversarial, learnings-researcher). Applied fixes are already committed on this branch (see commit `87de48d`); this file records what was **not** applied and why.

## Deferred — needs human judgment or live-org verification

- **[P2, advisory] Role's raw id-match may not survive cross-environment promotion.** (`src/Flowline.Core/Services/OrphanCleanupService.cs:424`) Role is the only `SupportedManualTypes` member verified by a raw Solution.xml `id` rather than a live-resolved schemaName/uniquename. Dataverse is documented to reconcile security roles by name on import when a role of that name already exists in the target — the same non-portability class already confirmed for WebResource/Entity/OptionSet in this codebase. If confirmed, a role synced from DEV could carry a different live GUID in UAT/PROD, causing the orphan diff to misclassify a still-assigned role as removable. **Cannot be confirmed without a real multi-environment test** (deploy the same solution into two orgs that each already have a same-named role). If confirmed, resolve Role live by name via `ResolveNamedComponentIdsAsync`'s existing `role`/`roleid`/`name` entry instead of the plain id match, mirroring how Entity/WebResource/OptionSet already handle this. — *adversarial reviewer, anchor 50*

- **[P1, manual] `OrphanCleanupService.cs` crossed 1000 lines (977 → 1143 across this branch).** Structural split recommended before the next entity-detected type is added: extract the resolution family (`IdentifyEntityDetectedTypesAsync`, `ResolveEntityDetectedManualEntriesAsync`, `ResolveOptionSetMetadataIdsAsync`, `ResolveNamedComponentIdsAsync`, `ResolveEntityMetadataIdsAsync`) into a separate class, and the componenttype/label lookup tables (`ManualTypeLabels`, `NameResolvableTypes`, `CustomApiIdAttributes`, `ExecutionOrder`) into a data-only catalog class. Deferred here as a reactive-review-driven refactor carries more regression risk than value without a live org to verify against; better done as its own deliberate unit. — *maintainability reviewer, anchor 100*

## Out of scope for this review

- **Unrelated uncommitted change in `src/Flowline/Program.cs`.** Re-enables `SolutionCheckService` and `BackupService` (previously deliberately disabled in commit `5860898`). Predates this branch entirely and was never touched by any of its units. Two independent reviewers (correctness, plus learnings-researcher cross-referencing git history) flagged it as either an intentional bundled change needing its own review, or a stray local uncomment that shouldn't ship silently. **Not part of this branch's diff** — flagged to the user directly, not fixed here.

## P3 / low-priority — noted, not actioned

- No test for `ScanConnectionReferenceLogicalNames` parsing malformed XML (fails safe: over-reports, doesn't suppress).
- No test locking in the (now corrected) per-table failure isolation's warning text beyond the one new test added.
- `BuildLocalIdentifierHarvest`'s five source sets aren't each individually proven to trigger the "possible local match" note — only the `NamedComponents` source path is tested.
- Case-insensitive matching in the "possible local match" signal isn't exercised by a differently-cased name.
- Two minor residual-risk notes from maintainability (double `TryGetValue` calls in `ResolveEntityDetectedManualEntriesAsync`'s `detail` computation — since fixed by the code-review patch which removed the second lookup entirely; a 7-positional-parameter helper signature, matching the existing `GetEntityNamesAsync` style already in this file).

## Applied (for reference — already committed, not residual)

- Entity-detection query failure isolation (correctness + reliability, cross-corroborated).
- Null-identity-attribute suppression bypass fixed to skip rather than default to "orphaned" (adversarial + testing, cross-corroborated).
- OptionSet metadata resolution now distinguishes genuine "not found" faults from real failures (reliability).
- Wiki (`Flowline.wiki/07-Deploy.md`) MANUAL-components list updated to include Bot/ConnectionReference (project-standards) — committed separately in the wiki's own repository, not this one.
