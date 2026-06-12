---
title: Deploy — Workflow & View State Restoration
date: 2026-06-12
status: idea
origin: docs/Features/FR-state-restoration.md
idea-source: docs/ideation/2026-06-07-deploy-command-ideation.md (idea #8)
---

# Deploy — Workflow & View State Restoration

## Summary

Every solution import resets **classic workflows** and Business Process Flows to `statecode=Draft`.
After `flowline deploy`, automations that were Active in the target silently stop running. No
warning, no error. Teams only notice when a business process fails.

Modern Power Automate cloud flows (2024+) are mostly fine — update imports preserve state, first-time
imports attempt restoration. Classic workflows and BPFs are not covered.

---

## How It Works

Two-step wrapper around `pac solution import`:

**Before import:** query target for all `workflow` and `savedquery` records in the solution.
Snapshot their `statecode` and `statuscode`.

**After import:** for each record where current state differs from snapshot — import reset it —
apply `SetStateRequest` to restore it.

```
Snapshot:  "Send Welcome Email"  →  statecode=1 (Active), statuscode=2
Post-import:                     →  statecode=0 (Draft),  statuscode=1
→ SetStateRequest: restore to Active
→ Log: "Restored 'Send Welcome Email' to Active (reset to Draft by import)"
```

No-op when state already matches. Cloud flows that Microsoft now preserves show no diff — no
`SetStateRequest` issued.

---

## Scope

| Restored | Logical name | Notes |
|---|---|---|
| Classic workflows / BPFs | `workflow` | Primary problem — still reset to Draft every import |
| Power Automate cloud flows | `workflow` | Safety net for first-time imports or edge cases |
| Views | `savedquery` | Public views only (`isprivate=0, isdefault=0`) — precaution, mirrors Daxif |

---

## New Flag

`--no-restore` — skip state restoration entirely. Useful when you intentionally want all
automations to start inactive (e.g. fresh env setup). Restoration is **on by default**.

`--no-restore` is independent of `--no-delete` (orphan cleanup). The two flags don't interact.

---

## Implementation Notes

- Snapshot: single query before `pac solution import` — all `workflow` + `savedquery` in solution
- Restore: runs immediately after import exits with code 0
- Use `SetStateRequest` not `UpdateRequest` — statecode is a managed field
- For Active workflows: deactivate first if required before state transition
- Per-record failures: log and continue, don't abort the deploy
- Query uses `solutioncomponent` join to scope to the deploying solution only
