# `init` prefix-format rejection costs two Dataverse connects before surfacing

- **Status:** not fixed (logged for judgment)
- **Severity:** low — UX/latency only; no wrong result, no data risk. Fail-closed.

## Repro

Run `flowline init` with a syntactically invalid `--publisher-prefix` against a resolvable DEV:

```
flowline init ValidName01 --dev <dev-url> --publisher-prefix mscrmx
flowline init ValidName01 --dev <dev-url> --publisher-prefix a          # too short
flowline init ValidName01 --dev <dev-url> --publisher-prefix abcdefghi  # too long
flowline init ValidName01 --dev <dev-url> --publisher-prefix 1ab        # bad start
```

Each prints, before the static-string error:

```
✓ Dev: AutomateValue Dev (...)                 <- resolver connect (env type lookup)
· Resolved PAC auth profile (...)
Connecting to Dataverse...
✓ Connected to Dataverse                       <- second connect
Error: Publisher prefix must ...
```

Two Dataverse round-trips for a purely syntactic error the CLI could reject offline. Compare the
solution `<name>` (rejected pre-connect at `InitCommand.cs:49`) and `--display-name` (rejected after
the first connect only, before the second) — both fail faster than the prefix.

## Root cause

`SolutionNameValidator.EnsurePublisherPrefix` runs at `SolutionCreateFlow.RunAsync` (`flow:71`), which
is **after** `ConnectAsync` (`flow:63`), which is itself after the resolver's env-type connect in
`InitCommand.ExecuteFlowlineAsync` (`InitCommand.cs:51`). The placement is deliberate: an
interactively *picked* prefix only exists post-connect (the picker reads existing publishers over the
connection), so validation has to sit there for that path. A flag-supplied prefix inherits the same
late placement for free.

## Suggested fix direction (not attempted inline)

Validate a **flag-supplied** prefix up front, mirroring the name check, and leave the flow's check for
the interactively-picked prefix:

```csharp
// InitCommand.ExecuteFlowlineAsync, right after EnsureSolutionUniqueName(settings.Name):
if (!string.IsNullOrWhiteSpace(settings.PublisherPrefix))
    SolutionNameValidator.EnsurePublisherPrefix(settings.PublisherPrefix);
```

Clone's create-new path (`PickOrCreateAsync`) has no prefix flag, so it's unaffected. The flow keeps
its own `EnsurePublisherPrefix` for the picked-prefix case, so the guarantee doesn't move — only a
redundant early check is added. Not done inline because it introduces intentional duplication of the
prefix rule across two layers, which is a design call worth confirming rather than assuming.
