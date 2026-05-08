# Feature Request: Deploy — Workflow & View State Restoration

## Problem

Every solution import — managed or unmanaged — resets **classic workflows** and Business
Process Flows to `statecode=Draft`. After a `flowline deploy`, automations that were Active
in the target environment stop running silently. There is no warning, no error, no
indication that anything changed. Teams only notice when a business process fails or someone
manually checks the environment.

This happens on every deploy, regardless of whether the workflow changed at all.

**Modern Power Automate cloud flows (solution-aware)** are a different story. Microsoft
improved import behaviour for these in 2024:

- **Update import (flow already exists in target):** the flow's current state is preserved.
  If it was off, it stays off. If it was on, it stays on. No reset.
- **First-time import (flow is new to the target):** import attempts to restore the exported
  state, provided all connection references in the flow have an active connection in the
  target environment.
- **During any import:** cloud flows are temporarily turned off and then back on as part of
  the import pipeline. This is a brief internal cycle — the final state after import is what
  matters, not the in-progress state.

**Classic workflows and BPFs are not covered by this fix** — they still reset to Draft on
every import. This is the primary remaining problem that state restoration must solve.

Views (`savedquery`) appear unaffected in typical imports but are included in scope as a
precaution, since Daxif restores them and edge cases may exist (e.g. a view that was
deactivated in the target and gets re-imported).

---

## User Story

**As a developer deploying a solution**, I want classic workflows and BPFs to remain in the
same active/inactive state they were in before the deploy, without having to manually
re-activate them after every import.

---

## How It Works

Two-step, wraps the existing `pac solution import` call:

**Before import:** query the target for all workflows and views in the solution and snapshot
their current `statecode` and `statuscode`.

**After import:** for each workflow/view where the current `statecode`/`statuscode` differs
from the snapshot — i.e. import reset it — apply a `SetStateRequest` to restore it.

```
Snapshot:  "Send Welcome Email"  →  statecode=1 (Active), statuscode=2
Post-import query:               →  statecode=0 (Draft),  statuscode=1
→ SetStateRequest: restore to Active
→ Log: "Restored 'Send Welcome Email' to Active (reset to Draft by import)"
```

Nothing is changed if the state already matches — the restore is a no-op for components
import did not touch. For cloud flows that Microsoft now preserves automatically, the
snapshot comparison will show no diff and no `SetStateRequest` is issued.

---

## Scope

| Restored | Logical name | Notes |
|---|---|---|
| Classic workflows / business process flows | `workflow` | Primary remaining problem — still reset to Draft on every import |
| Power Automate cloud flows (solution-aware) | `workflow` | Microsoft fixed update imports; included as a safety net for first-time imports or edge cases |
| Views | `savedquery` | Public views only (isprivate=0, isdefault=0) — included as precaution, mirrors Daxif scope |

State restoration runs **after** import completes, before Flowline exits.

---

## New flag

| Flag | Behaviour |
|---|---|
| `--no-restore` | Skip state restoration entirely. Useful if you intentionally want import's Draft reset to take effect (e.g. deploying to an environment where all automations should start inactive). |

State restoration is **on by default** — the default behaviour (silent Draft reset for
classic workflows) is never what you want in a normal deploy.

---

## Relationship to `--save`

`--save` controls whether orphan deletions are skipped. It does not affect state
restoration. The two flags are independent.

State restoration is not a destructive operation — it puts back what the import
unilaterally changed. It makes no sense to suppress it with `--save`.

---

## Implementation Notes

- Snapshot is taken before `pac solution import` is called — a single query for all
  `workflow` and `savedquery` records in the solution with their statecode/statuscode
- Restore runs immediately after import exits with code 0
- Use `SetStateRequest` (not `UpdateRequest`) — statecode is a managed field that requires
  the state-transition API
- Deactivating a workflow before changing its state may be required if it is currently
  Active and needs to be set to a specific statuscode — same pattern Daxif uses
- If restore of a single record fails (e.g. workflow was deleted by the import), log the
  error and continue — don't abort the deploy
- For cloud flows: the snapshot diff will typically show no change (Microsoft now preserves
  state on update imports), so `SetStateRequest` is issued only when the import actually
  reset the state — which covers first-time imports and any platform regressions

---

## Out of Scope

- Workflow owner reassignment across environments (separate feature, requires domain name
  mapping between source and target)
- Orphan cleanup (separate feature — see `FR-orphan-cleanup.md`)

---

## References

- [Import a solution (Power Apps docs)](https://learn.microsoft.com/en-us/power-apps/maker/data-platform/import-update-export-solutions)
  — Solution import mechanics, statecode behaviour for classic workflows
- [Import a flow (Power Automate docs, updated 2024-10-09)](https://learn.microsoft.com/en-us/power-automate/import-flow-solution)
  — Documents the 2024 fix: existing flows preserve state on update import; new flows
  restored if connection references have connections; brief disable/enable cycle during import
- [Community post — solution import and workflow states](https://community.dynamics.com/blogs/post/?postid=715a5ef2-a787-401f-978d-55328b68701d)
  — Real-world observations on workflow state behaviour after import
