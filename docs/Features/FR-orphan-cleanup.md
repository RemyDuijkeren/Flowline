# Feature Request: Deploy — Orphan Cleanup

## Problem

Unmanaged solution import is additive. When deploying v2.0 of a solution to a target that
has v1.0, Dataverse adds new components and updates existing ones — but **never removes
anything that was deleted from the solution**. Plugin steps keep firing, web resources stay
published, workflows keep running, forms and views clutter the UI. The environment silently
drifts from the intended state after every deploy.

Managed solution import does not have this problem: Dataverse tracks component ownership
and removes anything no longer part of the managed layer. This is one of the primary
practical reasons teams choose managed over unmanaged solutions.

### Hard failure: plugin class removed from the DLL

Orphan cleanup is not just a hygiene concern — it is a **hard prerequisite** in one common
scenario: when a plugin class is removed from the assembly.

Dataverse validates plugin type registrations against the actual DLL content on every
assembly upload. If a `plugintype` record is still registered for a class that no longer
exists in the new DLL, Dataverse **rejects the upload** with an error such as:

> `The plug-in type 'MyNamespace.OldHandler' does not exist in the assembly.`

This means `flowline push` (or any assembly update) will fail outright until the orphaned
plugin types and their steps are deleted first. Cleanup in this case is not optional — the
deploy is blocked until the orphans are removed.

---

## Opportunity

Managed solution orphan cleanup is not magic — it is a diff between the previous solution
state and the new one, followed by a delete of the difference. Flowline can replicate this
for unmanaged solutions.

Even for component types where auto-delete is unsafe (tables, columns, forms), **an
explicit, actionable report of what was removed from the solution is itself a significant
differentiator**. It turns "silently broken environment" into "explicit change log with a
clear todo list". Developers deploying unmanaged today get nothing — they have to mentally
track what changed across environments.

This is the core argument for unmanaged + Git-native ALM: you get the traceability and
workflow benefits of Git, and Flowline gives you the deployment hygiene that managed
solutions provide automatically.

---

## User Stories

**As a developer deploying a solution**, I want to know which components were removed in
this version so I don't leave orphaned plugin steps and dead web resources in the target.

**As a release manager deploying to production**, I want Flowline to automatically clean up
operational orphans (plugin steps, web resources, workflows) and show me what structural
orphans (tables, columns, forms) I need to review and delete manually.

**As a consultant managing multiple unmanaged solutions in one environment**, I want
Flowline to check whether an orphan is shared with another solution before deleting it, so
I don't break unrelated customisations.

---

## How It Works

**Before `pac solution import`:**

1. Query target `solutioncomponent` for the solution → current component set (S_old)
2. Parse `customizations.xml` from the solution ZIP → incoming component set (S_new)
3. Orphans = S_old − S_new
4. Classify each orphan (see below)
5. For auto-delete candidates: check cross-solution membership (see below)
6. Print orphan report
7. Unless `--save`: execute deletions for auto-delete components not shared with other solutions

---

## Component Classification

### Auto-delete

Deleted automatically unless `--save` is set, provided the cross-solution check passes.
These are operational records with no data of their own, scoped to a publisher.

| Component | Logical name | Notes |
|---|---|---|
| Plugin assembly | `pluginassembly` | |
| Plugin type | `plugintype` | |
| Plugin step | `sdkmessageprocessingstep` | |
| Step image | `sdkmessageprocessingstepimage` | |
| Custom API | `customapi` | |
| Custom API request parameter | `customapirequestparameter` | Cascades from custom API delete |
| Custom API response property | `customapiresponseproperty` | Cascades from custom API delete |
| Web resource | `webresource` | |
| Workflow / classic process | `workflow` | Deactivate first, then delete |

Deletion order: images → steps → types → assemblies; custom APIs; web resources; workflows.

### Report-only (never auto-deleted)

Listed in the orphan report with a "manual action required" label. Structural or
data-bearing components where auto-deletion risks data loss or breaking other
customisations.

| Component | Why not auto-deleted |
|---|---|
| Table (entity) | Has rows — data loss risk |
| Column (attribute) | Has values in rows — data loss risk |
| Relationship | Referential integrity |
| Form | May be referenced by app modules or other solutions |
| View | May be referenced by app modules, charts, or other solutions |
| Chart | May be referenced by dashboards |
| Dashboard | May be pinned to home pages |
| Security role | Users depend on it |
| Global option set | Columns reference it |
| Site map | App navigation |
| App module | Top-level app container |
| Connection reference | Power Automate flows depend on it |
| Environment variable | Referenced by flows or plugins |

---

## Cross-solution Safety Check

Before auto-deleting any component, query `solutioncomponent` in the target for the
component's `objectid` across all solutions excluding the current one. If found in another
active solution:

- Downgrade to report-only
- Label as: `SKIP — also in solution 'OtherSolutionName'`

This fixes a known gap in Daxif, which deletes orphans without this check and can
silently break other solutions sharing a component.

---

## Ownership Stamps

Flowline pushes plugin components and web resources directly (via `flowline push`). This
gives Flowline full control over the `description` field at write time, which can be used
to stamp these components with a solution ownership marker.

**Stamp format:** `[flowline:solution=MySolution]`

Applied on every `flowline push` create or update to:

| Component | Field stamped |
|---|---|
| `pluginassembly` | `description` — appended after the SHA256 hash |
| `plugintype` | `description` |
| `sdkmessageprocessingstep` | `description` — alongside the existing `[flowline:ClassName]` stamp |
| `sdkmessageprocessingstepimage` | `description` |
| `customapi` | `description` |
| `customapirequestparameter` | `description` |
| `customapiresponseproperty` | `description` |
| `webresource` | `description` — appended after any existing developer-written content |

**How Flowline writes the stamp:**
- Parse the existing `description` value before writing
- Remove any existing `[flowline:solution=...]` token (to handle renames or re-pushes)
- Append the updated token at the end: `Existing description. [flowline:solution=MySolution]`
- Never overwrite the rest of the description — only manage the stamp token

**How orphan cleanup uses it:**

When a component is classified as an auto-delete candidate, check its `description` for the
ownership stamp:

```
Orphan: "AccountPlugin: PostCreate" (sdkmessageprocessingstep)
  description: "[flowline:AccountPlugin][flowline:solution=MySolution]"
  → stamp present → confirmed Flowline-managed → proceed with auto-delete

Orphan: "SomeStep: DoThing" (sdkmessageprocessingstep)
  description: "" (no stamp)
  → no stamp → not confirmed Flowline-managed → downgrade to MANUAL
  → label: "not Flowline-managed — verify before deleting"
```

**Web resources: filename as additional ownership indicator**

Flowline scopes web resources to publisher and solution via the filename convention
`{prefix}_{solution}/{relativePath}` (e.g. `pub_mysolution/js/legacy.js`). This makes the
filename itself a strong and reliable ownership indicator — a web resource whose name starts
with `{prefix}_{solution}/` was created by Flowline for that solution. The description
stamp is belt-and-suspenders on top of this.

For orphan cleanup, a web resource that matches the filename convention can be treated as
Flowline-managed even if the description stamp is absent (e.g. it was pushed before stamps
were introduced). A web resource that does NOT match the convention should be treated as
not Flowline-managed regardless of whether a stamp is present.

**What is NOT stamped:**

Workflows and classic flows are created in Power Automate / the solution designer and
imported via `pac solution import`. Flowline does not create these, so it cannot reliably
stamp them without risk of overwriting developer-maintained description content. The
`solutioncomponent` diff and cross-solution check are sufficient for these.

---

## Extended Checks (future improvements)

The report-only classification can be enriched to help developers make informed manual
decisions:

**For forms, views, charts, dashboards:**
- Is the component referenced in any app module (`appmodulecomponent`)?
- Is it present in another solution's `solutioncomponent`?
- Is the other solution a managed Microsoft/first-party solution?

**For tables and columns:**
- Does the table have rows? (`$top=1` query)
- Is the column referenced by any active workflows or plugins?

**For security roles:**
- Is it assigned to any active users or teams?

Each check upgrades the report entry from a bare "MANUAL" label to a contextual note:
- `"View 'Active Accounts' is used in app module 'Sales Hub' — review before deleting"`
- `"Table 'legacy_log' has 0 rows — likely safe to delete"`
- `"Role 'Field Service User' is assigned to 3 users — reassign before deleting"`

---

## Orphan Report Format

```
┌──────────────────────────────────────────────────────────────────────────────────────────────┐
│  Orphan Report — MySolution v2.0 → Staging                                                    │
├────────────────────────────────────────────┬──────────────────┬──────────────────────────────┤
│  Component                                 │  Type            │  Action                      │
├────────────────────────────────────────────┼──────────────────┼──────────────────────────────┤
│  AccountPlugin: PostOp Create account      │  Plugin step     │  DELETE                      │
│  pub_mysolution/js/legacy.js               │  Web resource    │  DELETE                      │
│  Send Legacy Notification                  │  Workflow        │  DELETE                      │
│  SomeStep: DoThing                         │  Plugin step     │  MANUAL — not Flowline-managed│
│  Account Summary form                      │  Form            │  MANUAL                      │
│  Active Accounts (custom view)             │  View            │  MANUAL                      │
│  legacy_data (table)                       │  Table           │  MANUAL                      │
│  pub_common/js/shared.js                   │  Web resource    │  SKIP — also in 'CommonSolution' │
└────────────────────────────────────────────┴──────────────────┴──────────────────────────────┘

  3 components will be deleted. 4 require manual action. 1 skipped (shared).
  Run with --save to skip deletions and review the report first.
```

---

## New Flag

| Flag | Behaviour |
|---|---|
| `--save` | Report orphans but skip all deletions. Safe for a first deploy to an unknown environment, or when you want to review before committing to cleanup. |

---

## Relationship to State Restoration

Orphan cleanup and state restoration are independent features that both wrap
`pac solution import`. They can be implemented and shipped separately.

`--save` suppresses orphan deletions only — it does not affect state restoration.
See `FR-state-restoration.md`.

---

## Positioning

This directly addresses the main practical objection to unmanaged solutions in enterprise
Power Platform projects:

> "We use managed because we need the environment to stay clean after each deploy."

With orphan cleanup, Flowline provides the same guarantee for the components that matter
most (plugin steps, web resources, workflows) and an explicit, reviewable report for
everything else. Combined with Flowline's Git-native workflow, unmanaged becomes a credible
choice for teams that need both branch-based development and deployment hygiene.

---

## Out of Scope

- Patch solution support (intentionally not supported in Flowline)
- Workflow owner reassignment (separate feature — complex domain name mapping across envs)
- Data migration for deleted tables (out of scope for ALM tooling)
- Managed solution deployment (Flowline is explicitly unmanaged-first)
- State restoration (separate feature — see `FR-state-restoration.md`)
