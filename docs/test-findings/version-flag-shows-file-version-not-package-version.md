# `flowline --version` printed the 4-part file version, so two different builds looked identical

- **Status**: **fixed** 2026-07-29 (uncommitted in the source working tree — awaits commit
  authorization). Tests added, full suite green, live re-verified.
- **Severity**: low functionally, but it directly defeats the "rebuild, reinstall, re-test" loop this
  project's own test workflow depends on.
- **Found**: 2026-07-29, live, while upgrading the globally installed tool from `0.13.1-alpha.0.2` to a
  freshly packed `0.13.1-alpha.0.7`.

## Repro

```
dotnet tool list -g            # flowline 0.13.1-alpha.0.2
flowline --version             # 0.13.1.0
dotnet tool uninstall -g flowline
dotnet tool install -g flowline --add-source <nupkg-dir> --version 0.13.1-alpha.0.7
flowline --version             # 0.13.1.0   ← unchanged
```

The two builds differ by five prerelease increments and several bug fixes, and `--version` reports the
same string for both. The welcome screen (`ConsoleHelper.WelcomeScreen`) printed the same value.

## Root cause

`Program.cs` fed Spectre's `SetApplicationVersion` from `AssemblyFileVersionAttribute`:

```csharp
config.SetApplicationVersion(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "1.0.0");
```

MinVer stamps `AssemblyFileVersion` as a 4-part number that ignores the prerelease label, so every
prerelease of `0.13.1` is `0.13.1.0`. The full identity is on `AssemblyInformationalVersion`
(`0.13.1-alpha.0.7+028286d…`), which was unused for display.

## Fix

New `src/Flowline/Utils/FlowlineVersion.cs` resolves the display version from
`AssemblyInformationalVersionAttribute`, trims the `+<sha>` build-metadata suffix (so it matches what
`dotnet tool list -g` shows and what `dotnet tool install --version` accepts), and falls back to
`AssemblyFileVersion` when informational is absent. `Program.cs` and `ConsoleHelper.WelcomeScreen` both
use it.

Left alone deliberately: the three `AssemblyFileVersion` reads that are **not** display —
`FlowlineActivitySource` (telemetry), `FlowlineValidator` (update check against the published NuGet
version), and `FlowlineCommand`'s `RuntimeOptions.ToolVersions` (structured log field). Changing those
would alter telemetry/update-check semantics, which is not what this bug is about.

### Stated residual — the log file still carries the ambiguous version

Every run's log opens with an invocation header that keeps the 4-part number:

```
[INF] Invocation: 0.13.1.0 dotnet=10.0.302 pac=2.9.3+ga17df1d …
```

That line is the natural "which build produced this run?" audit trail, so it has the same ambiguity the
console fix removes. It was **not** changed here on purpose: the approved plan for that field
(`docs/plans/2026-06-29-001-feat-wave2-invocation-context-plan.md`) specifies it as the assembly file
version and lists "`ToolVersions.FlowlineVersion` matches the assembly file version" as an acceptance
criterion, so switching it is a spec change for the owner to make, not a repair. If it is wanted, the
one-line change is `FlowlineCommand.cs:121` → `FlowlineVersion.Display`, and that plan's acceptance
criterion needs updating with it.

Tests: `tests/Flowline.Tests/FlowlineVersionTests.cs` — asserts `Display` equals the informational
version with build metadata stripped, contains no `+`, and is non-empty.

## Live verification

After repack + reinstall, `flowline --version` prints `0.13.1-alpha.0.7`.
