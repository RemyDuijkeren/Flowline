# Multi-`[Handles]` Step Registration — Requirements

**Date:** 2026-06-21
**Scope:** Standard — attribute change + reader refactor

---

## Outcome

Allow a single `IPlugin` class to register multiple Dataverse plugin steps by stacking `[Handles]`
attributes. Intended as a migration escape hatch for brownfield codebases (spkl, Daxif) where one
class historically covered multiple step registrations. Not the preferred long-term pattern —
Flowline nudges developers to split into named subclasses when migration is complete.

---

## What we're building

### Allow multiple `[Handles]` on one class

`HandlesAttribute` gains `AllowMultiple = true`. Each `[Handles]` instance produces one independent
step registration. With zero or one `[Handles]` the behavior is unchanged.

```csharp
[Step("account")]
[Handles(Message.Create, Stage.PostOperation)]
[Handles(Message.Update, Stage.PostOperation)]
public class AccountPlugin : IPlugin { ... }
```

Registers two steps: `Create PostOperation` and `Update PostOperation`, both on `account`, both
using the same plugin type.

### `[Step]` config is shared across all registrations

`Order`, `Config`, `RunAs`, `SecondaryTable`, and `DeleteJobOnSuccess` come from the single `[Step]`
and apply identically to every registration produced by that class. There is no mechanism to
set these differently per registration.

### `[Filter]`, `[PreImage]`, `[PostImage]` apply per-compatible registration

These attributes remain class-level and are shared. The scanner applies each only to registrations
where the message supports it:

- `[Filter]` — registered on Update and UpdateMultiple steps only; silently skipped for others.
- `[PreImage]` — registered on steps where pre-images are supported (Update, Delete, etc.);
  silently skipped for Create.
- `[PostImage]` — registered on steps where post-images are supported (Create PostOp, Update, etc.);
  silently skipped for incompatible steps (e.g. Delete PostOp).

Error only if the attribute is present but **no** registration in the class is compatible with it.

### Step naming — preserve existing names where possible

Step identity in Dataverse is matched by `name` in `PlanPluginSteps`. Changing the name format
treats the old registration as an orphan (delete) and the new name as a new step (create).

**Goal priority (best to worst):**
1. **No-op** — existing Dataverse step untouched
2. **Update** — existing step modified in place (name or property change)
3. **Delete + create** — step removed and recreated; avoid at all costs (briefly removes the step from Dataverse during push)

**Rule:** include stage in the step name only when the same message appears more than once in the
class's `[Handles]` list.

| Registrations on the class | Step name format | Stage included? | Migration impact |
|---|---|---|---|
| Create + Update (different messages) | `{type.FullName}: {message} of {table}` | No — existing format | Existing step: no-op. New step: create. ✓ |
| Update PreOp + Update PostOp (same message) | `{type.FullName}: {message} of {table} at {stageName}` | Yes — required to avoid collision | Existing step: name changes → must not delete+create |

**Fallback match for same-message case:** when `PlanPluginSteps` fails to find a step by name,
it must attempt a secondary match by `(plugintypeid + message + entity + stage)` among the
Dataverse snapshot steps for that plugin type. If found, **update** the step (rename + sync
properties) instead of deleting and recreating it. This turns the name-change into an update,
preserving step continuity.

### Migration warning

When >1 `[Handles]` is present, Flowline emits a warning alongside other step warnings:

> `AccountPlugin: multiple [Handles] detected — prefer splitting into named subclasses for
> long-term maintainability.`

Non-blocking. Not suppressible.

---

## Migration cases this enables

These patterns are common in spkl and Daxif codebases and currently require subclass boilerplate
in Flowline. Multi-`[Handles]` removes that requirement as a stepping-stone during migration.

| Pattern | Example | Works? |
|---|---|---|
| Same table, multiple messages | Create + Update PostOp on `account` | ✓ |
| Same table, different stages of same message | Update PreOp + Update PostOp on `account` | ✓ |
| Same table, multiple messages + stages | Create PostOp + Update PreOp + Update PostOp | ✓ |
| Shared filter across multiple Update registrations | `[Filter]` + Update PreOp + Update PostOp | ✓ |
| PreImage on mixed class (Create + Update) | `[PreImage]` skipped for Create, applied to Update | ✓ |

---

## Migration cases still requiring separate subclasses

Multi-`[Handles]` cannot express these — separate named subclasses remain the right answer.

| Pattern | Why not possible |
|---|---|
| Different tables per registration | `[Step]` carries one table; no per-`[Handles]` table override |
| Different `[Filter]` columns per registration | `[Filter]` is class-level; shared across all steps |
| Different image aliases per registration | `[PreImage]`/`[PostImage]` are class-level; shared |
| Different `Order` per registration | `Order` is on `[Step]`; applies to all registrations |
| Different `Config` or `RunAs` per registration | Same — properties of `[Step]`, not `[Handles]` |
| `SecondaryTable` varies across registrations | `SecondaryTable` is on `[Step]`; applies to all |

---

## Scope boundaries

**In scope:**
- `AllowMultiple = true` on `HandlesAttribute`
- Reader produces a list of `PluginStepMetadata` per class (return type change from single to list)
- Filter/image compatibility logic applied per registration
- Step name includes stage for uniqueness
- Migration warning when >1 `[Handles]` detected

**Out of scope:**
- Warning suppression mechanism
- Per-`[Handles]` table, Order, Config, RunAs, or SecondaryTable override
- Inheriting `[Step]` from base classes (separate concern, not part of this change)
- Changes to `[Step]` or any attribute other than `[Handles]`

---

## Documentation updates required

### `src/Flowline.Attributes/README.md` — `[Handles]` section (line ~290)

Extend the existing `[Handles]` section to cover multi-registration:

- Show stacked `[Handles]` syntax with a Create + Update example
- State when to use it: migration from spkl/Daxif where one class covered multiple registrations
- Note that Flowline emits a warning nudging to split into named subclasses
- **Warn explicitly:** splitting a multi-`[Handles]` class into subclasses later will rename the
  steps in Dataverse — this causes step recreation (delete + create) on the next push. Plan the
  split for a maintenance window or accept the brief removal.

### `wiki/04-Push-Plugins-and-Custom-APIs.md` — `[Handles]` section (line ~142)

Mirror the same additions as the Attributes README. Both documents cover `[Handles]` for the same
audience; they must stay in sync.

### `wiki/13-Migration-from-spkl.md` — new subsection under plugin migration

spkl supports multiple `[CrmPluginRegistration]` attributes on one class. The migration guide
currently shows `[Handles]` only for class renaming. Add a new subsection:

**"One class, multiple step registrations"**
- Show a spkl class with two `[CrmPluginRegistration]` attributes (e.g. Create + Update PostOp)
- Show the equivalent Flowline code using stacked `[Handles]`
- Note the migration warning Flowline emits and that the long-term goal is named subclasses
- Warn about step recreation when splitting (same warning as above)

### `wiki/14-Migration-from-Daxif.md` — new subsection under plugin migration

Daxif's fluent registration can register one class for multiple steps. Same treatment as the spkl
guide — add a "One class, multiple step registrations" subsection with before/after and the same
splitting warning.

### `wiki/16-Migration-from-PACX.md` — no changes needed

PACX registers steps via individual CLI calls, not class-level annotations. There is no
multi-registration-per-class concept to migrate from.

---

## Is this worth it?

### Complexity

The changes are localized to three places: one attribute, one method in the reader, one method in
the planner. Nothing spreads across the codebase. The reader loop-over-handles is the same logic
run N times; the filter/image per-compatible check is a post-loop guard; the planner fallback match
is an additive secondary lookup. No new concepts are introduced — the reader and planner already
do all of this for the single-step case.

**Verdict: complexity is bounded and worth the migration payoff.**

### Normal path impact

The normal path — class with no `[Handles]`, name follows convention — is completely unaffected.
`AllowMultiple = true` on an attribute has zero effect when zero instances are present. The class-
name parsing path, step naming, and planner matching are all unchanged for single-step classes.

**Verdict: zero risk to the normal path.**

### The "remove it later" confusion

Once migration is done, the warning nudges the developer to split into named subclasses. When they
do, the step type name changes (`AccountPlugin` → `AccountPostCreatePlugin`), which changes the
step name in Dataverse. That causes a delete + create for each split step — the exact worst-case
operation this design otherwise avoids.

This is an acceptable one-time cost because:
- The split is an **intentional, developer-initiated** action, not a surprise side-effect.
- The warning keeps it visible so the developer doesn't forget they have multi-`[Handles]` in place.
- The alternative — staying on multi-`[Handles]` indefinitely — is also fine; the warning is a
  nudge, not a deadline.

The confusion risk worth calling out explicitly: developers should understand that cleaning up
multi-`[Handles]` by splitting into subclasses **will** cause step recreation in Dataverse.
Document this in the migration guide alongside the warning text.

**Verdict: worth it — the confusion is predictable, one-time, and developer-controlled.**

---

## Implementation notes

Single call site (`PluginAssemblyReader.cs:158`). `Steps` on `PluginTypeMetadata` is already a
list — the call site passes `[step]` today and will pass the full list after this change. Estimated
effort: 1–2 hours.
