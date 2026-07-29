# Verbose build output shows mojibake (`ΓåÆ` instead of `→`) in rollup's own output — NOT a Flowline bug

- **Status**: **closed 2026-07-29 — not a Flowline defect.** Reproduces identically with plain
  `dotnet build`, with Flowline nowhere in the process chain.
- **Severity**: n/a (cosmetic, and not ours).
- **Originally found**: 2026-07-23, live, `flowline push --scope webresources --dry-run --verbose`.

## What the earlier run couldn't decide

The original report saw lines like

```
dotnet:   src/example.ts ΓåÆ dist...
```

in `--verbose` output and could not tell whether Flowline decodes the child process's stdout with the
wrong codepage, or whether it was an artifact of that session's Git-Bash/MinTTY shell (which reported
active code page 437). It also could not find where the `dotnet: ` line prefix came from.

## Resolution

Both open questions are now answered.

**1. The `dotnet:` prefix is Flowline's** — `SubprocessCapture.Apply` prefixes every captured child
line with `FormatPrefix(cmd)` (`src/Flowline/Diagnostics/SubprocessCapture.cs:36` and `:58`). So the
line shape is Flowline's; the *content* is the child's.

**2. The mojibake is not Flowline's.** Re-run 2026-07-29 in PowerShell 7 with
`[Console]::OutputEncoding = utf-8` and `chcp` reporting **65001** — i.e. the opposite of the earlier
session's cp437 shell:

- Through Flowline: `dotnet:   src/example.ts ΓåÆ dist...` — still mojibake, so the earlier session's
  shell was not the cause.
- Running the same build directly, no Flowline at all:

  ```
  dotnet build ClientAssets\Cr07982.ClientAssets.csproj -c Release
    src/example.ts ΓåÆ dist...
  ```

  Identical mojibake.

`ΓåÆ` is UTF-8 `→` (`E2 86 92`) decoded as CP437. Since it appears with no Flowline in the chain, the
mis-decode happens between node/rollup and MSBuild's `Exec` task, which is outside Flowline's control.

Also confirmed changed since the original report: the literal ANSI SGR codes (`[36m`, `[1m`, …) noted
in the original observation did **not** reappear — rollup detects the non-TTY and emits no color.

## Action

None for Flowline. Anyone chasing the arrow further should take it up with the npm/rollup ↔ MSBuild
`Exec` encoding behavior, not with `SubprocessCapture`.
