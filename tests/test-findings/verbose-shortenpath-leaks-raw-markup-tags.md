# `console.Verbose` leaks raw `[bold]...[/]` Spectre markup when fed `ConsolePath.ShortenPath`'s output

- **Status**: fixed — 2026-07-23.
- **Severity**: low — cosmetic only, verbose-mode output, no functional impact.
- **Found**: 2026-07-23, live, running `flowline deploy prod --dry-run --verbose` and `flowline drift
  prod --verbose` against `Cr07982`/DEV auth resolution.

## Repro (pre-fix)

1. Run any command that resolves a Dataverse connection with `--verbose` (`deploy`, `drift`, `clone`,
   `sync`, etc.) on a machine where the PAC auth profiles file path is long enough for
   `ConsolePath.ShortenPath` to abbreviate it.
2. Expected: `Loaded 4 PAC auth profile(s) from C:/.../PowerAppsCLI/authprofiles_v2.json` with
   `authprofiles_v2.json` rendered bold.
3. Actual: `Loaded 4 PAC auth profile(s) from C:/.../PowerAppsCLI/[bold]authprofiles_v2.json[/]` — the
   literal markup tags printed instead of being rendered.

## Root cause

`DataverseConnector.cs:496` builds its verbose message with
`ConsolePath.ShortenPath(authProfilesPath)`, whose default (`markup: true`) return value embeds raw
Spectre markup (`[bold]...[/]`) intended for a markup-aware sink. But `Console.Verbose(string message)`
(`FlowlineConsoleExtensions.cs:22`) wraps its argument in `VerboseRenderable`, whose constructor does
`new Markup($"[dim]{Markup.Escape(message)}[/]")` — escaping the **entire** incoming string, including
any markup tags already embedded in it. The embedded `[bold]`/`[/]` get escaped into literal text
instead of nesting as rendered markup.

Same failure class as an earlier fixed bug in `ConsolePath.FormatRelativePath` feeding a
`FlowlineException` message (documented in `sync`'s dirty-check finding
`sync-dirty-check-ignores-other-folder-changes.md`) — a `ConsolePath` helper's markup-formatted output
passed into a sink that escapes its own input.

## Fix applied

`ConsolePath.ShortenPath` gained the same `markup: bool = true` escape hatch `FormatRelativePath`
already has (see `ConsolePath.cs`'s doc comment on that parameter) — `markup: false` returns plain,
unescaped text with no `[bold]`/`[/]` wrapping. `DataverseConnector.cs:496` now passes
`markup: false`, since `console.Verbose` already escapes the whole message itself.

Regression tests: `tests/Flowline.Core.Tests/ConsolePathTests.cs` (new file) —
`ShortenPath_MarkupFalse_ContainsNoMarkupTags`, `ShortenPath_MarkupTrue_WrapsLastSegmentInBoldMarkup`
(existing behavior unchanged), `ShortenPath_ShortPath_ReturnsUnchangedRegardlessOfMarkup`, and an
integration-style `ConsoleVerbose_WithShortenPathMarkupFalse_RendersNoLiteralMarkupTags` reproducing
the exact live failure via `TestConsole`. Full suite green after the fix (`dotnet test Flowline.slnx`,
1928 passing).

## Live re-verification (post-fix)

Re-ran `flowline drift prod --verbose` against the rebuilt/reinstalled CLI: the line now reads
`Loaded 4 PAC auth profile(s) from C:/.../PowerAppsCLI/authprofiles_v2.json` with no literal markup
tags.

`ConsolePath.ShortenPath` has exactly one call site in the whole codebase (confirmed via repo-wide
grep), so this fix is fully scoped — no other caller to check.
