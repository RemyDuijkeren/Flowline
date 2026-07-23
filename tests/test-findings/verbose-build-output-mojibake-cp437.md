# Verbose build output shows mojibake (`ΓåÆ` instead of `→`) for npm/rollup's own colored output

- **Status**: not fixed — root cause not conclusively isolated to Flowline; needs investigation with a
  human present at a real terminal (see below for why this session couldn't finish the diagnosis).
- **Severity**: low — cosmetic only, `--verbose` mode, only affects the pass-through of a third-party
  build tool's (rollup's) own colored console output.
- **Found**: 2026-07-23, live, `flowline push --scope webresources --dry-run --verbose` in the test
  workspace (ClientAssets WebResources project, rollup build step).

## Observation

Verbose output included lines like:
```
dotnet:   [36m
dotnet:   [1msrc/example.ts[22m ΓåÆ [1mdist[22m...[39m
dotnet:   [32mcreated [1mdist[22m in [1m1.8s[22m[39m
```
`ΓåÆ` is the classic mojibake signature of a UTF-8 arrow (`→`, U+2192, bytes `E2 86 92`) decoded as
CP437/CP1252. The raw ANSI SGR codes (`[36m`, `[1m`, `[22m`, `[39m`) are also visible as literal text
rather than being either rendered as color or stripped.

## What's been ruled out (and what hasn't)

- Flowline's own `Program.cs:27` already sets `Console.OutputEncoding = Encoding.UTF8` — so this isn't
  simply "Flowline never sets UTF-8."
- The Bash-tool shell this session ran the live test in reported active codepage 437
  (`chcp.com` → `Active code page: 437`) — but that's this session's own Git-Bash/MinTTY subprocess
  environment, not necessarily representative of a real user's terminal (PowerShell, Windows Terminal),
  so it's unclear whether this reproduces outside this specific test harness.
- Could not locate, via source grep, where the `dotnet:` line-prefix in the captured output actually
  comes from (no literal `"dotnet: "` or similar label-prefix pattern found under `src/`) — so it's
  unclear whether this prefix is Flowline's own subprocess-output labeling (in which case Flowline
  owns whatever encoding that streaming path uses to decode the child process's stdout) or an artifact
  of how this session's shell tool displayed the captured output. This is the open question a fix would
  need to resolve first.

## Suggested next step

Re-run the same repro (`flowline push --scope webresources --dry-run --verbose` with a WebResources
project that has a bundler build step) from a real interactive terminal (Windows Terminal / PowerShell,
UTF-8 codepage) to confirm whether this reproduces outside the Bash-tool harness before spending time
on a fix. If it does reproduce, find wherever the npm/rollup child process's stdout is captured and
re-printed, and confirm it decodes with UTF-8 (matching what npm itself writes) rather than the
process's inherited console codepage.
