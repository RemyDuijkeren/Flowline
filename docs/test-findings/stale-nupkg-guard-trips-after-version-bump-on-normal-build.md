# Stale-`.nupkg` guard blocks `push` after any version bump, and its remedy names a flag the user didn't pass

- **Status**: not fixed — the correct behavior (clean before packing vs. select the just-built package
  vs. keep failing) is a design call; see "Why not fixed inline".
- **Severity**: medium — hits the normal `commit → push` loop repeatedly, and the printed remedy is
  wrong for the case that actually triggers it.
- **Found**: 2026-07-29, live, twice in one session, `flowline push --scope plugins` (nupkg mode).

## Repro

In a nupkg-mode plugin project versioned by MinVer (the scaffolded shape — `Cr07982.Plugins.csproj`
references `MinVer`), where `push` builds the project itself:

1. `flowline push` — succeeds, leaves `Backend/bin/Release/Cr07982.Backend.0.0.0-alpha.0.3.nupkg`.
2. `git commit` anything in the repo (MinVer's height increments → next version is `…alpha.0.4`).
3. `flowline push` again.

Observed:

```
Building Backend (Release)...
✓ Build Backend done in 7s (Release)
Error: Found 2 .nupkg files under the build output —
Cr07982.Backend.0.0.0-alpha.0.3.nupkg, Cr07982.Backend.0.0.0-alpha.0.4.nupkg.
Run a clean build (delete bin/Release or drop --no-build) so only the current
version's package remains.
```

Exit 15. Recovering needs a manual `rm bin/Release/*.nupkg`; the next commit reintroduces it.

## Two separate problems

1. **The guard fires on the path it was meant to exempt.** Flowline itself ran the build in this same
   invocation (no `--no-build`), and MSBuild does not remove the previous version's package, so the
   ambiguity is produced by Flowline's own build step — not by a stale artifact the user left behind.
2. **The remedy text is inapplicable.** "drop `--no-build`" is advice for a user who passed
   `--no-build`; here no such flag was used, so the only actionable half is "delete bin/Release", which
   reads as the fallback rather than the fix. Per `docs/tone-of-voice.md` an error should name the step
   that actually applies.

## Suggested fix direction

When Flowline ran the build in this invocation, it knows the pack step just ran: either delete
pre-existing `*.nupkg` under the build output before building, or select the package written during
this run (and only fall back to the ambiguity error on the `--no-build` path, where it genuinely cannot
tell). Either way, keep the error for the `--no-build` case, and reword it so it names `--no-build`
only when `--no-build` was actually passed.

## Why not fixed inline

"Delete build output the user didn't ask us to delete" and "pick one of several packages by write
time" are both behavior changes with real failure modes (a wrongly-chosen package would be pushed to
Dataverse silently), and choosing between them — plus whether the guard should remain at all on the
build path — is an owner decision. The bug-fix policy in `docs/test-goal.md` routes that to a finding.
