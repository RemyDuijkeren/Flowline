# Flowline CLI — Tone of Voice

**The one-liner:** Talk like a smart colleague who knows their stuff and doesn't waste your time.

Flowline is the fun, lightweight alternative to Dataverse Pipelines. Every message should feel
like it comes from a tool that respects the developer's time and intelligence — not an enterprise
wizard that over-explains itself.

---

## Personality pillars

| Pillar | What it means | What to avoid |
|---|---|---|
| **Confident** | Says what it did, not what it tried to do | "Attempting to...", "Trying to..." |
| **Casual** | Contractions, short sentences, no jargon | "Initialization of solution artifacts", "Proceeding with..." |
| **Economical** | One thought, one line | Restating what the user already typed |
| **Honest** | Skipped steps are dim, not silent | Pretending everything is green when nothing happened |
| **Grounded** | Dry humour is fine, emoji are earned | Exclamation marks on every line, forced enthusiasm |

---

## Message categories & rules

### Spinner labels *(text next to the spinner)*
Active verb, ellipsis, bold for the named thing. Shown while waiting — carries the "working on it" meaning so no preamble line is needed.
```
Checking your setup...
Checking prod 'MyOrg'...
Cloning CrO7982 from Dataverse...
Building...
```

### Spinner sub-items *(printed while spinner runs)*
Present tense, no punctuation, ≤ 30 chars. These flash by — keep them tight.
```
Git's good
You're in a Git repo
.NET's good
PAC CLI's good
```

### Success after an operation
Green, bold for the named thing. Drop redundant metadata (don't repeat what was already validated).
```
[green]All good, let's go![/]
[green]Prod: ByteValue (https://...)[/]
[green]Dev: ByteValue-dev (https://...)[/]
[green]Solution: CrO7982 (managed: false)[/]
[green]Extensions project ready[/]
[green]Build done[/]
```

### "Already there, skipping" *(idempotent steps)*
Dim — not a success, not an error. No "Skip creation" — just state what's true.
```
[dim]SolutionPackage already cloned — skipping[/]
[dim]Solution file already there — skipping[/]
[dim]Extensions project already there — skipping[/]
```

### Errors
Red, direct, tell them what to do next. No passive voice. No blame.
```
[red]Prod environment not found. Check the URL or your PAC login.[/]
[red]Solution 'Foo' isn't in that environment.[/]
[red]Build failed — check the output above.[/]
```

### Finish line
One per command, always last, earns its emoji. References the next command(s) so they know what's next.
```
:rocket: Cloned! Use 'push' and 'sync' to keep it in flow.
```

---

## Rhythm & message order

A command output reads like a short story with three acts. Each act has a clear opener and closer.

```
ACT 1 — Preflight      (spinner → one verdict line)
ACT 2 — Resolve inputs (spinner → one verdict line, repeated per input)
ACT 3 — Do the work    (steps with spinners → dim skips or green completions)
         ─────────────────────────────────────────────────────────────────
         Finish line   (one line, one emoji, what's next)
```

### Order rules

1. **One spinner per logical unit of waiting.** Don't split one async call into multiple spinners.
   Don't merge unrelated waits into one.

2. **Spinner resolves to exactly one line.** After a spinner completes, print one outcome line
   (green success or red error). The spinner *was* the "working on it" — the outcome line is the
   verdict.

3. **Skips are dim, not green.** A skipped step didn't succeed — it was already done.

4. **Errors stop the act immediately.** When an error fires, nothing else prints below it.

5. **The finish line is always last and always alone.** It earns its weight by position. No blank
   line needed before it.

6. **No preamble.** Never announce what you're about to do with a plain line and then do it. The
   spinner label *is* the announcement.

### Example shape of `flowline clone`

```
                                          ← ACT 1: preflight (single spinner)
All good, let's go!

                                          ← ACT 2: resolve inputs
Prod: ByteValue (https://...)
Solution: CrO7982 (managed: false)

                                          ← ACT 3: work steps
SolutionPackage already cloned — skipping    [dim]
Solution file already there — skipping       [dim]
Extensions project ready                     [green]
WebResources project ready                   [green]
Mapping file written                         [green]
Build done                                   [green]

:rocket: Cloned! Use 'push' and 'sync' to keep it in flow.
```

---

## Verbose mode (`--verbose`)

Verbose is for **the developer debugging the tool, not the user running the command.** Normal
output should feel complete without it.

### What verbose adds

| | Normal | Verbose |
|---|---|---|
| What happened | ✓ | ✓ |
| Why it matters | ✓ | ✓ |
| Versions found | ✗ | ✓ |
| Exact commands run | ✗ | ✓ |
| File paths for internal moves/renames | ✗ | ✓ |
| PAC async operation ticks | ✗ | ✓ |

### Verbose rules

1. **Always `[dim]`** — verbose lines are footnotes, not headlines. Skip messages are also
   `[dim]`, and that's intentional. The two are distinguished by **position**, not colour:
   skips appear at the step's natural place in the flow; verbose lines always follow directly
   after the clean line they annotate. No second dim style is needed.

2. **Always after the clean line, never before.** The verdict comes first; verbose detail follows.

3. **Never duplicate the clean line.** Verbose adds detail, it doesn't restate what's already
   printed in green.

4. **Commands use the full invocation string** so the user can copy-paste and run it themselves:
   ```
   [dim]Executing: pac solution clone --name CrO7982 --environment https://...[/]
   ```

5. **Versions follow the check line:**
   ```
   Git's good
   [dim]Git version: 2.44.0[/]
   ```

6. **Paths for internal file operations** (renames, moves):
   ```
   [dim]Renaming CrO7982.cdsproj → SolutionPackage.cdsproj[/]
   ```

7. **Verbose never changes the structure.** Acts, spinners, and the finish line are identical —
   verbose only inserts `[dim]` lines in between.

---

## Vocabulary cheat sheet

| Instead of… | Say… |
|---|---|
| "Validation successful" | just show the result in green |
| "Skip creation" | "already there — skipping" |
| "Initializing" | "Setting up" |
| "Proceeding" | (just do it, no announcement) |
| "Please provide" | "use --flag \<value\>" |
| "Currently" | (drop it) |
| "Successfully" | (drop it — if it failed you'd say so) |
| "Attempting to" | (just do it) |
| "In order to" | "to" |
