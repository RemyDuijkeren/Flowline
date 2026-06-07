Review all user-facing CLI messages in recently changed `.cs` files against the Flowline tone-of-voice rules and AI agent readability requirements.

**Step 1 — Find changed files**
Run `git diff --name-only HEAD` and `git diff --name-only --cached` to get all changed `.cs` files.
If no changed files, check `git diff --name-only HEAD~1` instead.

**Step 2 — Find all CLI messages**
In those files, find every call to:
- `console.Ok(...)`, `console.Done(...)`, `console.Info(...)`, `console.Skip(...)`, `console.Verbose(...)`, `console.Warning(...)`, `console.Error(...)`
- `AnsiConsole.MarkupLine`, `AnsiConsole.Markup`
- `ctx.Status(...)`, spinner label strings
- `AnsiConsole.WriteLine`
- `.AddColumn(...)` table content
- Any string literal containing `[green]`, `[red]`, `[dim]`, `[yellow]`, `[bold]`
- `throw new FlowlineException(...)` — the message string argument
- `.WithDescription(...)` calls on commands (help text shown in `flowline --help`)

**Step 3 — Check each message against @docs/tone-of-voice.md**

For each message, check:
- **Spinner labels**: active verb + ellipsis + bold for named thing? No preamble?
- **Spinner sub-items**: present tense, ≤ 30 chars, no punctuation?
- **Ok**: `console.Ok(...)` used for successful sub-steps? No word "successfully"? No redundant metadata?
- **Skip**: `console.Skip(...)` used? Phrased as "already there — skipping" or similar?
- **Error**: `console.Error(...)` used? States what happened + what to do next? No passive voice?
- **Finish line**: `console.Done(...)` used? One per command, last line only, emoji included, references next command?
- **Verbose**: `console.Verbose(...)` used? After the clean line, not before?
- **Vocabulary**: none of the banned words (see vocabulary cheat sheet in the guide)?
- **Raw markup bypass**: raw `MarkupLine($"[green]...[/]")`, `[red]`, `[dim]`, `[yellow]` calls where a helper exists? Flag these — use the helper instead unless mixing styles within the line.

**Step 3b — Check for AI agent readability**

Agents parse CLI output to decide what to do next. Check:

- **Actionable error messages**: `FlowlineException` messages thrown with exit codes 4, 12, 14, or 17 must include the corrective action in the message itself — not just in the exit code table.
  - Code 4 (NotAuthenticated): must include the command to run, e.g. `run: pac auth create --environment <url>`
  - Code 12 (DirtyWorkingDirectory): must say what to do, e.g. `Commit or stash changes first`
  - Code 14 (VersionConflict): must include `Add --force to overwrite`
  - Code 17 (ForceRequired): must include `Use --force to proceed`
- **No agent-opaque failures**: error messages must not say only "failed" or "error" without context. An agent receiving the message must know which resource failed and what action it should take (retry, fix config, add a flag, etc.).
- **Help text (`.WithDescription`)**: must follow the what+trigger+state-change pattern — what the command does, when to run it, and what changes after. A one-line "Push plugins" description fails; "Build and register plugin assembly and web resources directly to DEV — skips pack/import. Run after plugin or web resource changes." passes.

**Step 4 — Output**
For each violation, output exactly:

```
file.cs:42 — [category] — violation description — suggested fix
```

Categories: `spinner`, `ok`, `skip`, `error`, `finish-line`, `verbose`, `vocabulary`, `raw-markup`, `agent-actionable`, `agent-opaque`, `help-text`

If clean, output: `All messages pass tone and agent-readability check.`

Do not restate passing lines. Only report violations.
