---
title: A set tuned to be over-inclusive for safety becomes unsafe when reused as an exclusion filter
date: 2026-07-21
category: docs/solutions/design-patterns/
module: Flowline.Core.Services (WebResourcesProjectResolver, SolutionFileLayout), Flowline.Core.Plugins (PluginProjectResolver)
problem_type: design_pattern
component: project_detection
tags: [discovery, set-semantics, exclusion, false-negative, data-loss, over-inclusive, filtering]
applies_when:
  - "A set is built deliberately over-inclusive (keep maybe-X to avoid wrongly dropping a real X), then that same set is reused elsewhere as the exclusion for a not-X category"
  - "One discovery pass classifies items into overlapping-by-construction buckets (plugin / WebResources / test) where 'maybe A' and 'is B' can name the same item"
  - "A resolver picks 'the one that's left' by elimination, so a wrong exclusion upstream silently changes which item is picked"
---

# A set tuned to be over-inclusive for safety becomes unsafe when reused as an exclusion filter

## Context

Flowline classifies the projects in a `.sln`/`.slnx` into roles. Plugin discovery uses a deliberately
**over-inclusive** cheap pre-filter: `PluginProjectResolver.DescribePreFilterSkip` keeps a project as a
"maybe-plugin" whenever it *can't cheaply rule it out* — e.g. the project declares no `<TargetFramework>`
of its own but a `Directory.Build.props` sits above it, so an SDK reference *could* arrive from there
(`src/Flowline.Core/Plugins/PluginProjectResolver.cs:124`). That over-inclusion is correct **for its own
purpose**: a false "definitely not a plugin" drop later deletes a live plugin registration, its steps, and
its Custom APIs, so the pre-filter is tuned to never drop a real plugin (`PluginProjectResolver.cs:96`).

`SolutionFileLayout` then reused that same over-inclusive set as the **exclusion** for WebResources
detection: WebResources = the solution `.csproj` that is *not* in the plugin set, not PCF, not a test
(`SolutionFileLayout.cs:73` builds the set; `:78-79` hands it to `WebResourcesProjectResolver.Resolve`).
The reuse is where it broke.

## Guidance

**A set tuned to be permissive for safety in context A is the wrong set to subtract in context B.** When
a discovery pass keeps "maybes" to avoid a costly false-negative in *its* job, those maybes include items
that legitimately belong to a *different* role — and subtracting the permissive set removes them from that
other role's candidates.

Two defenses, applied together:

1. **Let the other role re-claim its own by a positive signal, overriding the borrowed exclusion.** Don't
   subtract the permissive set blindly — keep an excluded item when it positively looks like it belongs
   here. Flowline keeps a plugin-set member in the WebResources candidates when it carries a *strong*
   WebResources signal (`Microsoft.Build.NoTargets` SDK, a suppressed compile target, a `dist/` folder, a
   `flowline:` annotation), because a real plugin carries none of those:
   `WebResourcesProjectResolver.cs:96` — `.Where(path => !pluginProjectPaths.Contains(path) || HasStrongWebResourceSignal(path))`.
2. **Require a positive signal from the winner, so a wrong elimination fails loud, not silent.** Detection
   was elimination-first ("the one that's left is it"). If the real item was wrongly excluded, a *different*
   item is "the one that's left". Requiring the winner to carry at least one positive signal turns that
   into a loud outcome instead of a confident wrong pick: `WebResourcesProjectResolver.cs:121` —
   `if (topScore < MediumWeight)` returns "no confident match" rather than the zero-signal survivor.

## Why This Matters

Reusing the plugin pre-filter set as the WebResources exclusion created a **silent data-loss** path. In a
repo with a shared `Directory.Build.props`, a genuine WebResources project whose own csproj omits
`<TargetFramework>` was swept into the plugin set, excluded from WebResources candidates, and a plain
library became the lone survivor — returned as "the WebResources project" with zero corroboration. Its
`dist/` never existed, so the deploy drift gate (which compares built web resources against the packed
solution) found nothing to warn about, passed, and a deploy reverted the user's un-synced web resources.

The bug is invisible at each step: the pre-filter is correct for plugins, the subtraction is a one-liner,
and elimination "obviously" returns the WebResources project. It only appears when you ask *whose*
definition of the set you're subtracting — a set's over-inclusiveness is a property of the job it was
built for, and it silently changes meaning the moment another consumer borrows it.

## When to Apply

- You have a discovery/classification pass that keeps "maybe-X" items to avoid a costly false-negative in
  its own decision (deletion safety, build avoidance, cache invalidation).
- Another consumer wants "not-X" and reaches for that same set as the thing to subtract.
- Roles overlap by construction — the same `.csproj` can be a "maybe-plugin" *and* the real WebResources
  project — so exclusion is not the clean complement it looks like.
- A resolver picks by elimination, where a wrong exclusion upstream silently swaps the answer rather than
  erroring.

If any of these hold: don't subtract the borrowed set blindly. Add a positive re-claim for the other role,
and make the picker demand a positive signal so a wrong elimination is loud. See
[[solutionfilelayout-project-detection-consolidation]] for the surrounding detection design, and
[[reverse-relationship-inverts-what-orphaned-means]] for the sibling trap where a *relationship* is read in
the wrong direction — both are "a set/edge tuned for one purpose reasoned about wrongly by another".

## Examples

**The trap (before):**

```csharp
// SolutionFileLayout: the WebResources exclusion set is the over-inclusive plugin pre-filter set
var pluginPaths = PluginProjects.Select(p => p.ProjectPath).ToHashSet(OrdinalIgnoreCase);

// WebResourcesProjectResolver: subtract it blindly, then take the one that's left
candidates = solutionCsproj
    .Where(p => !pluginPaths.Contains(p))   // a real WebResources project inheriting its
    .Where(p => !IsPcf(p)).Where(p => !IsTest(p));  // framework from Directory.Build.props is HERE
return candidates.Single();                 // ...so the plain library is "the one that's left"
```

**The fix (after):**

```csharp
candidates = solutionCsproj
    // re-claim: a strong WebResources signal overrides the borrowed exclusion
    .Where(p => !pluginPaths.Contains(p) || HasStrongWebResourceSignal(p))
    .Where(p => !IsPcf(p)).Where(p => !IsTest(p));

var top = TopByScore(candidates);
if (top.Count > 1) throw new FlowlineException(...);   // ambiguous: loud
if (topScore < MediumWeight) return null;              // zero-signal winner: "no confident match", loud-skip
return top[0];                                          // a real match always carries a signal
```

A real WebResources project always carries a signal, so it is either never in the plugin set or is rescued
back — and "no match" therefore genuinely means none, which a caller can skip with a loud warning instead
of silently reverting live data.
