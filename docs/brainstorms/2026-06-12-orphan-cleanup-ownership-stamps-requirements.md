---
title: Orphan Cleanup — Ownership Stamps
date: 2026-06-12
status: idea
origin: docs/Features/FR-orphan-cleanup.md (ownership stamps section)
related: docs/plans/2026-06-07-004-feat-deploy-orphan-cleanup-plan.md
---

# Orphan Cleanup — Ownership Stamps

## Summary

Orphan cleanup is implemented. This doc covers the **ownership stamp** enhancement: writing a
`[flowline:solution=MySolutionName]` token into the `description` field of each Flowline-managed
component on push. The stamp lets orphan cleanup distinguish Flowline-managed components from
manually-registered ones and auto-delete only the former.

Without stamps, orphan cleanup today relies solely on the cross-solution `solutioncomponent` check.
That is sufficient but conservative — a component not found in other solutions still isn't
confirmed as Flowline-managed, so it downgrades to MANUAL. Stamps make the ownership check cheap
and reliable.

---

## Stamp Format

```
[flowline:solution=MySolution]
```

Written into the `description` field. Always at the end, after any existing content.

Write pattern:
1. Read current `description`
2. Strip any existing `[flowline:solution=...]` token (handles renames or re-pushes)
3. Append the updated token: `Existing description. [flowline:solution=MySolution]`

---

## Which Components Get Stamped

Applied on every `flowline push` create or update:

| Component | Field |
|---|---|
| `pluginassembly` | `description` — after the SHA256 hash |
| `plugintype` | `description` |
| `sdkmessageprocessingstep` | `description` — alongside the existing `[flowline:ClassName]` stamp |
| `sdkmessageprocessingstepimage` | `description` |
| `customapi` | `description` |
| `customapirequestparameter` | `description` |
| `customapiresponseproperty` | `description` |
| `webresource` | `description` — after any developer-written content |

**Not stamped:** Workflows/classic flows — created in Power Automate, not by Flowline. The
`solutioncomponent` diff and cross-solution check are sufficient for those.

---

## How Orphan Cleanup Uses Stamps

When a component is classified as auto-delete candidate, check its `description` for the stamp:

```
Orphan: "AccountPlugin: PostCreate" (sdkmessageprocessingstep)
  description: "[flowline:AccountPlugin][flowline:solution=MySolution]"
  → stamp present → confirmed Flowline-managed → auto-delete

Orphan: "SomeStep: DoThing" (sdkmessageprocessingstep)
  description: "" (no stamp)
  → no stamp → downgrade to MANUAL
  → label: "not Flowline-managed — verify before deleting"
```

**Web resources:** The filename convention `{prefix}_{solution}/{path}` is the primary ownership
indicator. Stamps are belt-and-suspenders. A web resource matching the filename convention can be
treated as Flowline-managed even without a stamp (e.g. pushed before stamps were introduced).

---

## Rollout Consideration

Components pushed before stamps were introduced will have no stamp. On first push after implementing
this, Flowline will write stamps on all creates/updates. Components that haven't changed since the
last push won't get stamps until they are touched. Orphan cleanup should tolerate stampless records
gracefully (downgrade to MANUAL rather than error) during the transition period.
