# Stale-`.nupkg` guard blocked `push` after any version bump, on the path that created the ambiguity

- **Status**: **fixed** 2026-07-29. Regression tests added, full suite green, live re-verified against
  the exact repro.
- **Severity**: medium — hit the normal `commit → push` loop repeatedly, and the printed remedy named a
  flag the user hadn't passed.
- **Found**: 2026-07-29, live, twice in one session, `flowline push --scope plugins` (nupkg mode).

## Repro

In a nupkg-mode plugin project versioned by MinVer (the scaffolded shape), where `push` builds the
project itself:

1. `flowline push` — succeeds, leaves `Backend/bin/Release/Cr07982.Backend.0.0.0-alpha.0.3.nupkg`.
2. `git commit` anything (MinVer's height increments → next version is `…alpha.0.4`).
3. `flowline push` again.

Observed before the fix:

```
Building Backend (Release)...
✓ Build Backend done in 7s (Release)
Error: Found 2 .nupkg files under the build output —
Cr07982.Backend.0.0.0-alpha.0.3.nupkg, Cr07982.Backend.0.0.0-alpha.0.4.nupkg.
Run a clean build (delete bin/Release or drop --no-build) so only the current
version's package remains.
```

Exit 15, recoverable only by deleting the file by hand — and the next commit brought it straight back.

## Root cause

Two problems in one message.

1. **The guard fired on the path it was meant to exempt.** Flowline ran the build in that same
   invocation; `dotnet pack` embeds the version in the filename and never removes an earlier version's
   package, so the ambiguity was produced by Flowline's own build step, not by a stale artifact the
   user left behind.
2. **The remedy was inapplicable.** "drop `--no-build`" is advice for someone who passed `--no-build`;
   no such flag was used, so the only actionable half was "delete bin/Release".

## Why `-t:Rebuild` / `dotnet clean` is not the fix

This was checked before choosing an approach, because Flowline already has `-t:Rebuild` plumbing
(`DotNetUtils.BuildArguments`) used to self-heal the *opposite* symptom (`PushCommand.cs` — nupkg older
than the freshly built DLL, i.e. the version didn't change so Pack skipped and overwrote nothing).

Measured on a real project with a stale `…alpha.0.1.nupkg` planted beside the current `…alpha.0.4`:

| Command | Result |
| --- | --- |
| `dotnet build -t:Rebuild -c Release` | **both** packages still present |
| `dotnet clean -c Release` | removes the **current** package, **leaves the stale one** |

MSBuild's Clean only deletes what the *current* build recorded as its output; a package built from a
different version was never in that list. So Rebuild cannot fix this, and Clean alone makes it worse.

Also measured, and what makes the chosen fix safe: deleting every `.nupkg` and running a plain
`dotnet build` **regenerates** the package — so clearing first cannot strand `PluginPackageMode.Auto`
on the classic `.dll` path.

## Fix

`PushCommand.ClearStalePackages` deletes every `.nupkg` under the plugin project's build output
immediately before Flowline invokes the build, so the package Pack writes is the only one there. Called
only when Flowline runs the build itself and only when the mode can consume a package
(`PluginPackageMode != Dll`) — with `--no-build` the packages on disk are all there is, and deleting
them would destroy the very thing being pushed. A locked or read-only file is logged and skipped rather
than failing the push; the ambiguity guard still catches anything that survives.

The guard itself stays for the `--no-build` path, where there is genuinely nothing to disambiguate
against and guessing would risk pushing stale code to Dataverse. Its message now names only the step
that applies:

> Found 2 .nupkg files under the build output — … Can't tell which one matches your source. Delete the
> ones you don't want, or drop --no-build so Flowline can repack.

Tests in `tests/Flowline.Tests/PushCommandTests.cs`: clears every package including nested ones while
leaving the build output itself alone; clearing then resolving no longer hits the ambiguity guard;
removals are reported and carry no Spectre markup (`console.Verbose` escapes what it is given); a
missing build output root is a no-op.

## Live verification

Planted the exact repro (`…alpha.0.1.nupkg` beside `…alpha.0.4.nupkg`) and ran the command that failed
twice earlier that day:

- `push --scope plugins --dry-run` → stale package cleared, build repacked, push proceeded. Only
  `Cr07982.Backend.0.0.0-alpha.0.4.nupkg` left on disk.
- `--verbose` → `Removed stale package Backend/bin/Release/Cr07982.Backend.0.0.0-alpha.0.1.nupkg`,
  plain text.
- `push --scope plugins --no-build` with both packages present → still exit 15, with the reworded
  message.
