---
title: Plugintype ID Specification Not Needed in Flowline
date: 2026-06-23
category: docs/solutions/tooling-decisions/
module: Plugin Registration
problem_type: tooling_decision
component: tooling
severity: low
applies_when:
  - Evaluating feature parity with spkl for plugin registration
  - Migrating from spkl and wondering if stored plugintype GUIDs need to carry over
tags:
  - plugin-registration
  - plugintype
  - spkl
  - guid
  - step-matching
  - migration
---

# Plugintype ID Specification Not Needed in Flowline

## Context

spkl (SparkleXrm) allows specifying an `Id` on step registrations in its `CrmPluginRegistrationAttribute`. When evaluating whether to add a similar explicit ID option to Flowline CLI, the question arose: what problem does this solve, and does Flowline have the same problem?

## Guidance

Do not add an explicit plugintype or step ID option to Flowline. Flowline's design already handles the core problem that spkl's feature was solving, structurally rather than via stored config.

## Why This Matters

spkl's `Id` field solved a specific idempotency problem: without a stored GUID anchor, spkl could not reliably find existing step registrations on re-deploy and would create duplicates. The ID compensated for fragile name-based matching.

Flowline solves this differently and more robustly:

- **Plugin types** matched by `FullName` — unambiguous discovery, no stored ID needed
- **Steps** matched solely by the content tuple `(plugintypeid, sdkmessageid, sdkmessagefilterid, stage, mode)` — handles renames without a pinned GUID; the generated display name is written but never read for identity (see CONCEPTS.md: `Step identity`)

The remaining edge cases that IDs would address are niche:

- **Cross-environment GUID consistency** — plugin type GUIDs vary across environments in Flowline's direct-deploy model, but modern solution-based ALM preserves GUIDs through export/import anyway
- **Takeover of externally-registered types** — only matters when a class was registered under a different FullName by another tool, which is uncommon

If cross-environment GUID consistency ever becomes a real requirement, the appropriate solution is **deterministic seeded GUIDs** (v5 UUID derived from `FullName`) rather than user-specified IDs — stable GUIDs across environments with zero configuration surface.

## When to Apply

- When evaluating whether to port a spkl/XrmToolBox feature to Flowline — check whether Flowline's discovery-based model already handles it
- When a user migrating from spkl asks whether their stored plugintype GUIDs need to carry over — they do not; Flowline matches by `FullName`

## Examples

**spkl (config-file centric — requires stored GUIDs for idempotency):**

```json
{
  "steps": [
    {
      "id": "a1b2c3d4-e5f6-...",
      "class": "MyPlugin.AccountPlugin",
      "message": "Create",
      "entity": "account"
    }
  ]
}
```

**Flowline (discovery-based — no IDs needed):**

```csharp
[Step(Stage.PreOperation, "account", "Create")]
public class AccountPlugin : IPlugin { ... }
// PluginPlanner discovers existing types by FullName,
// matches steps solely by content tuple.
```

## Related

- CONCEPTS.md: `Step identity`
