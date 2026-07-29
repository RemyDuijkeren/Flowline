# Non-TTY output is hard-wrapped at 80 columns, splitting log paths and URLs mid-token

- **Status**: not fixed — needs a decision on how Flowline should pick its render width (and whether to
  stop wrapping at all when not attached to a terminal). See "Why not fixed inline".
- **Severity**: medium for agent/CI consumers, low for humans — nothing is wrong, but the one thing a
  caller most often needs to extract programmatically (the log file path) is the thing that reliably
  gets broken in half.
- **Found**: 2026-07-29, live, every command, running Flowline from an AI coding agent (stdout captured,
  no TTY).

## Repro

Run any command with stdout redirected (any agent harness, `flowline push > out.txt`, or a CI log):

```
flowline sln add .\nope\nope.cdsproj
```

Observed (line breaks are literally in the captured stream):

```
Error: No project at '.\nope\nope.cdsproj' — check the path.
Log:
C:\Users\RemyvanDuijkeren\AppData\Local\Flowline\logs\2026-07-29T070034Z-sln.log
```

and, at other path lengths, mid-token splits like `…2026-07-29T072941Z-push.lo` / `g` on the next line.
`flowline --help` breaks its own example URLs the same way:

```
    flowline clone ContosoCustomizations --prod
https://contoso.crm4.dynamics.com
```

`COLUMNS=200` has no effect (verified). There is no `--width`, `--no-wrap`, or plain-output flag.

## Root cause

Spectre.Console derives its render width from the console buffer and falls back to 80 columns when
stdout is redirected, then word-wraps every renderable to that width. Flowline never overrides the
profile width, so every message — including `Log: <path>` and the `--help` examples — is wrapped at 80
regardless of the consumer. Nothing in Flowline treats "this is a path/URL, keep it whole" differently
from prose.

## Impact on the agent path specifically

- The printed log path is Flowline's own documented audit trail (`docs/test-goal.md` calls it the way
  to verify a run without `--verbose`). An agent that wants to open it has to reassemble the path from
  two or three lines first, and cannot distinguish a wrap from a genuine newline.
- Same for environment URLs echoed in `Checking dev https://…` lines and for solution/step names in
  drift and orphan reports, which are the other things worth parsing.
- It compounds with the absence of any machine-readable output mode (no `--json`/porcelain anywhere in
  the command surface): prose parsing is the only option, and the prose is reflowed.

## Suggested fix direction

Cheapest correct change: when `!AnsiConsole.Profile.Capabilities.Interactive` (or stdout is
redirected), set the profile width to something large (or `int.MaxValue`) so nothing is wrapped, and
let the consuming terminal/pager wrap if it wants — this is what most CLIs do. Tables/trees would then
render at their natural width. A narrower alternative is to keep wrapping prose but emit
paths/URLs through a renderable that opts out of wrapping.

Worth pairing with a `--json` (or at minimum `--porcelain`) mode for the drift/orphan report and the
push plan, which is the other half of what makes this surface agent-drivable.

## Why not fixed inline

Changing the render width changes the layout of every table, tree, and panel Flowline prints, in every
command, for every non-interactive consumer including CI logs — that is a product-wide presentation
decision with no obviously right value, not a mechanical repair. The bug-fix policy in
`docs/test-goal.md` routes those to a finding.
