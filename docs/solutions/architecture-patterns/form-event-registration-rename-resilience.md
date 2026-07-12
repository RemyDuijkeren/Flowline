---
title: Form-event rename resilience — advisory-only signals behind a hard-fail contract
date: 2026-07-12
category: docs/solutions/architecture-patterns
module: form-event-registration
problem_type: architecture_pattern
component: tooling
severity: medium
applies_when:
  - "a flowline:onload/onsave annotation's form-name token no longer matches any live Dataverse form on that entity at push time"
  - designing a resilience layer for a registration/lookup mechanism that binds source annotations to renamable external records by name
symptoms:
  - "flowline push fails with a bare 'form not found' error after a maker renames a form in the Maker Portal, with no guidance on what happened or how to fix it"
related_components:
  - FormEventReader
  - FormEventPlanner
  - FormEventService
  - FormEventRenameAdvisor
  - FormEventIdentityCache
  - FormEventDeterministicId
  - PushCommand
  - FlowlineStoragePaths
  - FormKeyComparer
tags:
  - form-events
  - dataverse
  - rename-resilience
  - form-lookup
  - identity-cache
  - annotation-binding
---

# Form-event rename resilience — advisory-only signals behind a hard-fail contract

## Context

Flowline pushes JS form-event handlers to Dataverse forms using source annotations —
`// flowline:onload <entity> "<Form Name>" FunctionName` — that bind to a `systemform` by a literal
`(EntityLogicalName, FormName)` string match (`src/Flowline.Core/Services/FormEventReader.cs:116-166`,
keyed via the case-insensitive `FormKeyComparer` at `FormEventReader.cs:330-342`). The Dataverse-side
handler registration itself is wired to the form by `formid` (a GUID), not by name, so a rename in the
Maker Portal never breaks the actual handler binding — but it does break Flowline's ability to *find*
that form again on the next push, since the annotation still says the old name.

Before this change, that lookup miss produced a bare message with no diagnostic value:

```
form '{form}' not found for entity '{entity}' (Main or Quick Create form).
```

(`FormEventReader.cs:215`, inside `BuildFormNotFoundMessage`.) The developer had no signal that a
rename was the likely cause, and no path forward except manually diffing Maker Portal history against
source.

The fix adds a three-signal advisory resolver that fires only on lookup-miss and appends a suggestion
to the existing failure text — it deliberately never changes the failure outcome itself. This is worth
documenting as a pattern because the interesting design work isn't the advisor logic itself, it's the
constraints that shaped it: preserving a hard failure as a product requirement, deriving "proof" from a
value the tool itself produced earlier, and a live platform-verification step that killed an initially
promising idea before it reached code.

This work is captured on the local branch `feat/form-event-rename-resilience` (not yet pushed or opened
as a PR); the codebase paths below are the current state of that branch, including the follow-up refactor
commit `020bdcf` (`refactor(form-events): use DataverseForm instead of a raw tuple across the advisor
boundary`). That SHA is local-only as of this writing — no PR exists to cite instead, and the hash itself
may change if the branch is later rebased or squash-merged; treat "commit 020bdcf" below as shorthand for
"the current state of this local branch," not a stable, permanently-resolvable reference.

## Guidance

**1. A hard-fail contract can still get better UX — treat the diagnostic and the outcome as separable.**
The plan's Requirements state this explicitly: a name-lookup failure always fails the push; no signal,
alone or combined, lets registration proceed or succeed silently
(`docs/plans/2026-07-12-001-feat-form-event-rename-resilience-plan.md`, R5). The annotation's form name
stays mandatory and the annotation grammar is untouched (R7) — a deliberate decision to keep
source-of-truth in the annotation, not an oversight. The entire feature lives in one function,
`FormEventRenameAdvisor.Suggest` (`src/Flowline.Core/Services/FormEventRenameAdvisor.cs:15-50`), called
only from the miss branch of `FormEventReader`'s resolution loop:

```csharp
// FormEventReader.cs:161-165
return globalMatches.Count switch
{
    0 => (Pair: pair, Error: BuildFormNotFoundMessage(entity, form, resolvedAnnotations, solutionForms, cache)),
    _ => (Pair: pair, Error: $"form '{form}' for entity '{entity}' exists in Dataverse but is not a component of solution '{solutionName}'.")
};
```

`BuildFormNotFoundMessage` (`FormEventReader.cs:209-229`) always builds the same `baseMessage`, calls
`FormEventRenameAdvisor.Suggest`, and appends its result only if non-null (`FormEventReader.cs:228`:
`return suggestion is null ? baseMessage : baseMessage + suggestion;`). When no signal fires, the
message is byte-for-byte identical to the pre-feature text — the negative case is a no-op by
construction, not an extra branch to keep in sync.

**2. Derive "proof" from a value your own tool produced, not a heuristic.** The strongest of the three
signals — self-tag — doesn't compare names at all. It recomputes the exact deterministic handler ID
Flowline would have produced *when the annotation was originally registered* (using the annotation's
still-unrenamed `requestedName`, not any candidate's current name), then scans every live form on the
entity for a `<Handler>` whose stored ID matches:

```csharp
// FormEventRenameAdvisor.cs:57-84 (FindSelfTagMatch)
foreach (var resolved in sharingAnnotations)
{
    var evt = resolved.Annotation.Event;
    var (requestedFunctionName, autoNamespace, isExplicit) = FormEventPlanner.DeriveHandlerResolutionInputs(resolved, evt);

    var (finalFunctionName, found, _) = FormEventFunctionResolver.Resolve(
        resolved.Content, requestedFunctionName, autoNamespace, isExplicit);
    if (!found)
        continue;

    var expectedId = FormEventDeterministicId.ForHandler(entity, requestedName, evt, finalFunctionName!, resolved.LibraryName);

    foreach (var candidate in candidatesForEntity)
    {
        XDocument xdoc;
        try { xdoc = XDocument.Parse(candidate.FormXml); }
        catch (Exception) { continue; }

        if (FormXmlEventSerializer.GetHandlers(xdoc, evt).Any(h => h.HandlerUniqueId == expectedId))
            return candidate.Name;
    }
}
```

Because that ID is a hash Flowline itself derives from `(entity, name, event, functionName, libraryName)`,
a match is direct evidence — not a guess — that this exact candidate *was* the annotation's target under
a prior name; nothing else could have produced that exact ID. `DeriveHandlerResolutionInputs` is shared
(not reimplemented) from `FormEventPlanner.cs:252-259`, so the advisor's resolution logic can't drift
from the planner's — a small but important reuse decision, since divergent resolution rules between the
two would make self-tag evidence unreliable.

Note the resilience choice at `FormEventRenameAdvisor.cs:78-80`: this loop parses FormXml for *every*
live candidate on the entity, including ones the feature never validated FormXml for elsewhere. A
malformed/empty FormXml on any one candidate is swallowed and that candidate is skipped, rather than
crashing the whole advisory pass — an already-failing push must never get *worse* by the advisor blowing
up.

**3. A per-environment cache widens the evidence window past what's derivable in a single pass.**
`FormEventIdentityCache` (`src/Flowline.Core/Services/FormEventIdentityCache.cs`) is a plain JSON-array
file of `(Entity, Name, FormId, LastSeenUtc)` records, one file per Dataverse organization
(`GetFormEventCachePath`, below). It's written on *every* successful resolution during
`FormEventReader.LoadSnapshotAsync` — including forms with no current annotation, which matters because
that's exactly the data a rename-detection later needs from a form that was renamed away from:

```csharp
// FormEventReader.cs:116-131 (abbreviated)
foreach (var group in solutionForms.GroupBy(f => (f.EntityLogicalName, f.Name), FormKeyComparer.Instance))
{
    var matches = group.ToList();
    if (matches.Count == 1)
    {
        resolvedForms[group.Key] = new DataverseForm(...);
        cacheResolutions.Add((matches[0].EntityLogicalName, matches[0].Name, matches[0].Id));
    }
    ...
}
cache?.SetMany(cacheResolutions);
```

Because `LoadSnapshotAsync` runs unconditionally regardless of `--dry-run` (R2), the cache gets populated
by dry runs too, at no extra implementation cost — this is a case where satisfying a requirement fell out
of *where* the write was placed rather than needing its own flag/branch.

At lookup-miss time, the cache signal checks whether a previously cached `formId` for `(entity,
requestedName)` still exists live, just possibly under a different name now:

```csharp
// FormEventRenameAdvisor.cs:34-42
var cachedFormId = cache?.TryGet(entity, requestedName);
if (cachedFormId is { } formId)
{
    var cacheMatches = solutionForms.Where(f => f.Id == formId).ToList();
    if (cacheMatches.Count == 1)
        return BuildSuggestion(entity, cacheMatches[0].Name, sharingAnnotations, Confidence.Probable);
}
```

**4. Architecture boundaries can force an API's shape — accept the explicit-path constructor rather than
fighting the boundary.** `Flowline.Core` has no project reference to `Flowline` (the CLI project) — the
reference direction runs the other way. That means `FormEventIdentityCache` cannot call
`FlowlineStoragePaths` itself to find its own file. Its constructor therefore just takes a raw
`path: string` (`FormEventIdentityCache.cs:11`), with the caller responsible for resolving it. Resolution
happens in `Flowline.Utils.FlowlineStoragePaths.GetFormEventCachePath`:

```csharp
// src/Flowline/Utils/FlowlineStoragePaths.cs:31-35
public static string GetFormEventCachePath(string environmentUrl)
{
    var sanitized = SanitizeForFileName(environmentUrl);
    return Path.Combine(GetStorageRoot(), "form-events", $"{sanitized}.json");
}
```

`SanitizeForFileName` (`FlowlineStoragePaths.cs:37-55`) lowercases and trims a trailing slash before
sanitizing — so a `--dev` flag URL and a CI script's URL that differ only in trailing slash or casing
resolve to the same cache file rather than silently fragmenting the cache into two files. This CLI-side
path is then threaded down through `FormEventService` into `FormEventReader.LoadSnapshotAsync`'s
`formEventCachePath` parameter, which is `null`-tolerant: no path means no cache is constructed at all,
rather than writing to a disposable temp file nothing would ever read back.

**5. Trace exactly what a merge-on-write does before assuming a "rename-discovery" push could clobber the
very history it needs.** A natural worry: if a push resolves `(entity, "New Name")` to some `formId`, does
`SetMany` wipe out the previous `(entity, "Old Name") -> formId` entry at the exact moment a *future*
lookup-miss on "Old Name" would need it? Reading `SetMany` answers this directly — it removes only the
entry matching the *incoming* `(entity, name)` key before appending the new one, per resolution, not a
blanket rewrite by `formId`:

```csharp
// FormEventIdentityCache.cs:58-64
foreach (var (entity, name, formId) in resolutions)
{
    entries.RemoveAll(e =>
        string.Equals(e.Entity, entity, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    entries.Add(new Entry(entity, name, formId, DateTime.UtcNow));
}
```

Since the removal predicate is keyed on `(Entity, Name)`, not `FormId`, writing a fresh `(entity, "New
Name", formId)` entry never touches an existing `(entity, "Old Name", formId)` entry that shares the same
`formId` — both rows coexist in the file. That's exactly what the cache-signal case in `Suggest` depends
on: a stale annotation still says "Old Name", the lookup misses on that name, `TryGet(entity, "Old
Name")` still finds the old entry's `formId`, and that `formId` is then checked against the live
`solutionForms` list to find its current name. The historical entry surviving isn't an accident of timing
— it's guaranteed by the key the removal predicate uses.

**6. State accepted tradeoffs as tradeoffs, not silently absorbed risk.** Three things were deliberately
left as-is rather than hardened, each with a stated reason:

- *Swallow-all exception handling.* Both `TryGet` and `SetMany` wrap all I/O in `try/catch (Exception)`
  and degrade to a no-op miss or a skipped write (`FormEventIdentityCache.cs:27-32`, `:69-73`) — "a failed
  cache write shouldn't fail a push that already resolved forms successfully; worst case, the next push
  just doesn't find these entries and re-resolves by name." This is a narrow, deliberate exception to this
  project's standing convention against swallowing exceptions (no try/catch around service calls unless
  there is explicit recovery logic) — the recovery logic here is "degrade to cache-miss," which is
  explicit and narrow *(auto memory [claude])*.
- *Unlocked concurrent-write race.* Two simultaneous `flowline push` runs against the same environment
  can race on `SetMany`'s read-modify-write and clobber each other's writes. Recorded explicitly in
  `docs/residual-review-findings/feat-form-event-rename-resilience.md` under "Accepted, not a residual":
  a lost write just means a future push re-resolves by name — the cache is advisory-only bookkeeping, not
  a correctness-bearing store, so file locking was judged disproportionate.
- *Self-tag's first-sharing-annotation-wins precision gap.* `FindSelfTagMatch` (`FormEventRenameAdvisor.cs:52-88`)
  returns on the *first* sharing annotation that produces a match, without checking whether another
  sharing annotation on the same `(entity, form)` pair (e.g. `onSave` vs `onLoad`) has its own genuine
  self-tag evidence pointing at a *different* candidate. `BuildSuggestion` then prints a rewrite line for
  every sharing annotation using that one winning candidate. This is recorded as an open residual finding
  (P2, confidence 75) in `docs/residual-review-findings/feat-form-event-rename-resilience.md` — flagged
  as "a behavior change to the matching algorithm (favor recall vs. precision), not a mechanical fix," so
  it needs a design call rather than auto-apply, and was left unfixed by user decision.

  **Note on that residual-findings file's own staleness:** its *other* listed finding — that
  `FormEventRenameAdvisor.Suggest`/`FormEventReader.BuildFormNotFoundMessage` took a raw 5-field
  positional tuple instead of the existing `DataverseForm` model — has since been fixed. Commit `020bdcf`
  retyped both call sites to `IReadOnlyList<DataverseForm>`; current `FormEventReader.cs` builds
  `List<DataverseForm>` explicitly rather than passing the tuple through, and `DataverseForm` itself is
  defined at `src/Flowline.Core/Models/FormEventModels.cs:40`. The findings doc's text for that entry is
  stale relative to the current tree — worth flagging rather than repeating as a live gap if that file is
  read later.

**7. Two options were named and explicitly deferred, not silently dropped.** Per the plan (R8): fuzzy
name-similarity matching and an explicit `formid:` GUID-pin annotation token are documented as future
options, not implemented — listed under "Deferred to Follow-Up Work" in the plan's Scope Boundaries,
alongside proactive rename surfacing in `drift`/`status` and a manual `--relink` command. None of the four
appear anywhere in the current `FormEventRenameAdvisor.cs`/`FormEventReader.cs` source.

**8. Verify a platform-behavior assumption live before designing around it — logs and docs alone can
mislead.** During design, `systemform.isdefault` was floated as a candidate discriminator signal
(the idea: use it to identify "the" canonical main form on an entity for a sole-survivor-style check).
Querying the real "AutomateValue Dev" Dataverse test environment via `pac env fetch -xf` showed 1,535 live
Main/Quick Create forms with zero rows having `isdefault = true`, and Microsoft's own documentation
confirms `isdefault` is a Maker Portal dashboard-only concept, not a durable per-form flag. The idea was
dropped before any implementation, which is why it isn't visible anywhere in the diff. This is a concrete
instance of an existing project practice: query the live test environment when unsure of Dataverse
attribute/query behavior, don't just infer from docs or logs *(auto memory [claude])* — applied at design
time, before code was written, avoiding an implementation detour into a dead end.

## Why This Matters

The pattern generalizes beyond this one feature: when a hard-fail contract is a deliberate product
decision (not a limitation to route around), the design space narrows to *diagnostics only* — and that
constraint, rather than being limiting, is what makes the three-signal design tractable and safe to ship
without touching the annotation grammar at all (R7 held throughout). Self-tag's approach — deriving proof
from a value your own tool already produces (the deterministic handler ID), rather than inventing a new
heuristic — is reusable wherever a tool needs retroactive evidence of "this thing used to be that thing."
The cache's read-then-remove-only-the-matching-key-then-add shape is the standard way to make a
last-write-wins cache safe for exactly this kind of history-preservation requirement, and is worth
recognizing as a pattern rather than re-deriving next time a similar rename-cache is needed. Finally, the
`isdefault` dead end is a reminder that Dataverse platform behavior claims — especially ones sourced from
docs or partial logs — should be spot-checked against a live environment before they shape an
implementation, not just before they ship in a doc.

## When to Apply

- Designing a diagnostic/advisory layer on top of an intentionally-hard-failing operation, where the
  product requirement is "fail, but explain better" rather than "recover automatically."
- Needing retroactive proof that some current entity used to be some prior entity, when the tool itself
  previously derived a deterministic identifier for that entity — recompute-and-match beats fuzzy
  similarity for precision, at the cost of narrower recall (only catches cases where the tool's own prior
  computation is recoverable).
- Building a small local cache that must tolerate "advisory data lost" without ever escalating to
  "operation failed" — the swallow-all-exceptions + best-effort-write shape from `FormEventIdentityCache`
  is the right level of engineering effort for that risk profile; don't add locking or transactional
  writes when the cost of a lost write is "the next attempt just re-derives it."
- Any time a lower-level project/assembly needs data whose natural home (a path convention, an
  environment-scoped store) lives in a higher-level project it cannot reference — take the value as an
  explicit constructor parameter from the caller rather than inverting the dependency.
- Before committing to a platform-attribute-based signal (Dataverse or otherwise) sourced from docs or
  logs alone — spot-check it against a real environment first, especially if the attribute's semantics
  sound "too convenient" for the use case (e.g. a UI-scoped default flag being repurposed as a durable
  per-record signal).

## Examples

- `FormEventRenameAdvisor.Suggest` (`src/Flowline.Core/Services/FormEventRenameAdvisor.cs:15-50`) — the
  full three-signal, strongest-first dispatch, returning `null` (no candidate) or a
  `Confidence`-tiered suggestion string; never a resolved `DataverseForm`.
- `FormEventReader.BuildFormNotFoundMessage` (`FormEventReader.cs:209-229`) — the call site proving the
  contract: base message always built first, advisor result only ever appended, never substituted.
- `FormEventIdentityCache.SetMany` (`FormEventIdentityCache.cs:40-74`) — the remove-matching-key-then-add
  merge that preserves historical `(entity, oldName) -> formId` rows across concurrent/overlapping writes.
- `FlowlineStoragePaths.GetFormEventCachePath` / `SanitizeForFileName` (`src/Flowline/Utils/FlowlineStoragePaths.cs:31-55`)
  — canonicalize-before-sanitize, so trailing-slash/casing differences in an environment URL don't
  fragment the per-organization cache file.
- `docs/plans/2026-07-12-001-feat-form-event-rename-resilience-plan.md` — Requirements R1-R8 and Key
  Technical Decisions KTD1-6, including the explicit rationale for the tuple-vs-model choice later revised
  in commit `020bdcf`.
- `docs/residual-review-findings/feat-form-event-rename-resilience.md` — the two open/accepted findings;
  note its first entry (raw-tuple API) is stale against current source as of `020bdcf` and should not be
  treated as a live gap when reading that file going forward.

## Related

- [`docs/solutions/conventions/match-platform-vocabulary-for-new-domain-concepts.md`](../conventions/match-platform-vocabulary-for-new-domain-concepts.md)
  — same feature area and files (`FormEventReader.cs`, `FormEventPlanner.cs`), defines the "Form Event
  Handler"/"Event annotation" vocabulary this doc's mechanism operates on; a naming-convention lesson, not
  about rename resilience.
- [`docs/solutions/design-patterns/webresource-dependency-registration-patterns.md`](../design-patterns/webresource-dependency-registration-patterns.md)
  — a conceptually adjacent annotation-driven Dataverse sync feature with its own "reuse existing identity,
  generate fresh only when new" GUID-reuse pattern for dependency library GUIDs; different mechanism and
  files, same "name-keyed identity resolution against Dataverse" theme.
- `docs/plans/2026-07-10-002-feat-form-event-registration-plan.md` — the original plan for the base
  form-event-registration feature this work adds resilience to.
