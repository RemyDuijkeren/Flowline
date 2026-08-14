---
title: "Cached Advice Goes Stale the Moment Someone Acts On It — Re-derive the Verdict, Don't Serve It"
date: 2026-08-14
category: docs/solutions/design-patterns/
module: UpdateNoticeChecker
problem_type: design_pattern
component: tooling
severity: high
applies_when:
  - "Caching a computed recommendation whose whole purpose is to make the user change the state it was derived from"
  - "Adding a TTL to anything that tells the user to do something — update, migrate, re-auth, re-provision"
  - "Reviewing a cache read that returns a stored value directly instead of re-running the comparison that produced it"
  - "Caching a failed check, where the cache entry drives a back-off rather than a displayed result"
symptoms:
  - "A notice keeps telling the user to do something they already did, until the TTL expires"
  - "A message reads back a nonsense comparison against itself, e.g. \"2.0.0 is out — you're on 2.0.0\""
  - "The bug only reproduces after the user follows the tool's own advice, so it never shows up in normal testing"
  - "An unrelated interrupt (Ctrl+C, a cancelled token) silently suppresses a recurring notice for a whole TTL window"
tags:
  - caching
  - ttl
  - cli-output
  - update-check
  - staleness
  - cancellation
  - design-decision
related_components:
  - FlowlineValidator.TryGetCachedUpdateVersion
  - UpdateVersionComparer
  - NuGetVersionClient
---

# Cached Advice Goes Stale the Moment Someone Acts On It

## Context

Flowline's startup update check asks NuGet once a day whether a newer release exists, caches the answer, and prints a one-line notice on interactive runs. The cache exists so 364 of every 365 command invocations cost nothing.

The obvious implementation stores the newer version string and serves it back while the entry is fresh. That is correct for an ordinary cache — the stored value is still the right answer until it expires.

It is wrong here, and wrong on the most common path through the feature. The notice's entire job is to make the user run `dotnet tool update`. The moment they do, the cached entry describes a world that no longer exists — but it is still inside its TTL, so it keeps being served. The user updates, runs another command, and is told the version they are now running is available. For up to a day, the tool reports a successful update as a failed one.

Caught in review here, before shipping. It is easy to miss because it only reproduces when someone follows the tool's advice, which is exactly the path manual testing skips.

## Guidance

**When a cached value is a recommendation to change state, re-derive it on read instead of returning it.**

The cache should hold the *input* to the decision (what NuGet published), not the *decision* (what to tell the user). Keep the network result cached; re-run the cheap comparison every time.

In `src/Flowline/Services/UpdateNoticeChecker.cs`:

```csharp
// Wrong — serves a verdict that the user's own action may have invalidated.
if (validator.TryGetCachedUpdateVersion(noCache, out var cached))
    return cached;

// Right — the cache supplies the candidate; the comparison happens now.
if (validator.TryGetCachedUpdateVersion(noCache, out var cached))
    return cached == null ? null : UpdateVersionComparer.GetNewerVersion(FlowlineVersion.Display, [cached]);
```

The re-derivation is free — a semver parse and compare against the running assembly's version — and it reuses the comparator that is already the tested source of truth for what "newer" means. No cache schema change, no extra I/O.

**The mirror case: distinguish "checked, nothing to report" from "couldn't check."**

The same cache entry backs a failure back-off. Without one, an offline machine pays the full network timeout on every command forever, so recording the failed attempt is right. But the cancellation path is not a failure:

```csharp
// A Ctrl+C is not evidence NuGet is unreachable, so it must not buy a day of silence.
if (!cancellationToken.IsCancellationRequested)
    validator.SaveUpdateCheck(null);
```

`NuGetVersionClient.GetVersionsAsync` deliberately returns null for every failure mode so callers never have to catch. That uniformity is good for the caller's error handling and bad for the caller's *caching* decision — the caller has to recover the distinction from the token, because the return value threw it away.

## Why This Matters

The failure is invisible in the normal test matrix. Every test asserting "a fresh cache returns the stored value" passes, and that assertion is what makes the bug ship — it pins the wrong behavior as correct.

It is also worst for the best-behaved users. Someone who ignores the notice never sees the defect. Someone who acts on it immediately gets told their action did nothing, which is precisely the outcome that erodes trust in the tool's output.

The generalization: an ordinary cache assumes the cached value's truth is independent of the consumer. A recommendation breaks that assumption by design — it exists to change the world it describes. Any advisory cache is self-invalidating on success, so its read path needs a freshness check the TTL cannot provide.

## When to Apply

Reach for this whenever the cached thing is imperative rather than descriptive:

- "A newer version is available" → the user updates
- "This environment needs re-authentication" → the user re-auths
- "N orphaned components found" → the user cleans them up
- "Your solution is out of date with PROD" → the user syncs

Descriptive caches — an environment's URL, a solution's component list, a tool's version — do not have this property. Nothing about displaying them changes them.

## Examples

The regression test states the property directly rather than asserting on a stored value:

```csharp
[Fact]
public async Task CheckAsync_CachedVerdictAlreadyInstalled_ReportsNothing()
{
    // The user took the advice and updated, but the cached entry is still inside its TTL. Serving it
    // verbatim would say "X is out — you're on X" for the rest of the day.
    var validator = MakeValidator();
    validator.SaveUpdateCheck(FlowlineVersion.Display);
    var handler = new FakeHandler((_, _) => throw new InvalidOperationException("cache hit should not fetch"));

    var result = await UpdateNoticeChecker.CheckAsync(
        MakeConsole(interactive: true), validator, MakeClient(handler), noCache: false, CancellationToken.None);

    result.Should().BeNull();
    handler.CallCount.Should().Be(0);
}
```

Two things worth copying: it seeds the cache with the *running* version to simulate the post-update state, and it makes the fake handler throw so a cache miss fails loudly instead of quietly passing for the wrong reason.

A rejected alternative, for the record: invalidating the whole cache entry when the recorded Flowline version changes. It looks equivalent and is not — `FlowlineValidator` records `AssemblyFileVersion`, which MinVer stamps identically across every prerelease of the same release, so prerelease-to-prerelease upgrades would slip through the check while appearing to be covered.
