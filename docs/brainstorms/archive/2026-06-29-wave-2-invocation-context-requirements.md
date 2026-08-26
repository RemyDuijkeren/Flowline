# Wave 2 — Invocation Context Logging

**Date:** 2026-06-29
**Status:** Ready for planning
**Relates to:** `docs/ideation/2026-06-25-cli-observability-ideation.html` — I6 (ActivitySource), Wave 2 cleanup

---

## Problem

When a Flowline command fails — locally or on a build server — the log file captures what happened but not the environment it happened in. There is no record of which tool versions were active, whether it ran on CI, which solutions were configured, or which environment was targeted. Reproducing the failure requires asking the user to re-run with `--verbose` and manually report their setup.

---

## Outcome

Every command invocation produces a structured invocation header in the log file, capturing the full environment snapshot at the moment of execution. The same properties are set as ActivitySource tags on the root Activity (I6), so they flow automatically to App Insights (I7) as searchable custom dimensions. After this ships, a log file alone should be sufficient to understand the environment a failure occurred in.

---

## In Scope

### Invocation Header Properties

Logged once per command invocation, after `CheckSetupAsync` and `ProjectConfig.Load` have run in `FlowlineCommand.ExecuteAsync`. All properties are set as both a structured `LogInformation` entry and as tags on the root `Activity`.

| Property | Source | Notes |
|---|---|---|
| `flowline.version` | `AssemblyFileVersionAttribute` | e.g. `0.8.0` |
| `dotnet.version` | `EnsureDotNetAsync` result | e.g. `8.0.300` |
| `pac.version` | `EnsurePacCliAsync` result | e.g. `1.34.6` |
| `pac.installType` | `EnsurePacCliAsync` result | e.g. `winget`, `scoop`, `dotnet-tool` |
| `git.version` | `EnsureGitAsync` result | e.g. `2.45.0` |
| `git.branch` | New git call in `EnsureGitAsync` | e.g. `main`, `feature/deploy-v2`; `(detached)` on detached HEAD |
| `os` | `RuntimeInformation.OSDescription` | e.g. `Windows 11 Pro 10.0.26200` |
| `os.arch` | `RuntimeInformation.OSArchitecture` | e.g. `X64`, `Arm64` |
| `ci` | Env var detection | `true`/`false` |
| `ci.platform` | Env var detection | `github`, `azuredevops`, `jenkins`, `unknown` |
| `verbose` | `RuntimeOptions.IsVerbose` | `true`/`false` |
| `force` | `RuntimeOptions.Force` | `true`/`false` |
| `project.root` | `RootFolder` after discovery | Absolute path to project root |
| `project.solutions` | `Config.Solutions` names | Comma-separated list, e.g. `CoreSolution,PortalSolution` |
| `env.configured` | `ProjectConfig` URL fields | Which tiers have a URL set, e.g. `prod,uat` |
| `env.hashes` | SHA-256 (truncated, 8 chars) per configured URL | Stable identifier for correlating runs against same environment |

### CI Detection Expansion

Add `GITHUB_ACTIONS` to the existing env var checks in `ConsoleHelper.IsInteractive()` (currently only `CI`, `TF_BUILD`, `JENKINS_URL`). The `ci.platform` property maps:
- `GITHUB_ACTIONS=true` → `github`
- `TF_BUILD` set → `azuredevops`
- `JENKINS_URL` set → `jenkins`
- `CI=true` with none of the above → `unknown`

### Wave 1 ILogger Cleanup

Audit and remove explicit `LogInformation` calls in `PluginService`, `WebResourceService`, and `SolutionDiffService` that duplicate output already captured by `LoggingRenderHook`. Keep only calls for internal events with no corresponding console write (e.g. internal counts or durations not printed to the terminal).

---

## Out of Scope

- **IP address and country** — App Insights captures IP automatically from the OTLP HTTP connection when I7 ships; explicit capture adds PII risk with no additional value in local log files.
- **PAC auth profile name** — `pac auth list` takes 4–6 seconds; too slow for per-command startup.
- **Full environment URLs** — potentially sensitive (tenant identifiers). Hashed URL provides correlation without exposure.
- **Which environment a command targets** — commands choose their target environment at runtime, not at startup. Logging all configured tiers is the correct level of detail here.
- **Hardware metrics, locale, timezone** — not actionable for debugging Flowline failures.
- **Hashed machine/user identity** — deferred to I7 (App Insights); the telemetry initializer is the correct registration point, not the invocation header.

---

## Behaviour

### Sequencing in `ExecuteAsync`

```
1. Log command + redacted args          ← already exists (line 22)
2. CheckSetupAsync                      ← already exists; populates tool versions on RuntimeOptions
3. ProjectConfig.Load                   ← already exists (line 30)
4. [NEW] Log invocation header          ← single LogInformation + Activity tags, inserted here
5. ExecuteFlowlineAsync                 ← command body
6. Log completion + exit code           ← already exists (line 33)
```

The header log fires after both `CheckSetupAsync` and `ProjectConfig.Load` so all properties are available in one entry. This keeps the log readable: one header block, then command output, then completion.

### Commands Without a Project

For commands where `RequiresProject = false` and no `.flowline` is found, `project.solutions` and `env.*` properties log as empty. `project.root` logs the fallback CWD. No errors or warnings.

### Property Format

Structured log properties (not interpolated strings) — Serilog serialises them as key/value pairs in the file. No sensitive values. Empty/null properties are omitted from the log entry rather than written as empty strings.

---

## Assumptions

- Git branch is obtained by extending `EnsureGitAsync` with a `git rev-parse --abbrev-ref HEAD` call. Detached HEAD emits `(detached)`.
- Environment URL hashing uses SHA-256 truncated to 8 hex characters — stable, collision-resistant at this scale, not reversible.
- Activity tags and the `LogInformation` entry carry the same properties — no divergence between what goes to the log file and what goes to App Insights.
- The root `Activity` is started in `FlowlineCommand.ExecuteAsync` as part of I6; the invocation header tags are set on that activity immediately after it is created.

---

## Success Criteria

- Opening any log file from a failed command shows a single structured header with all 16 properties (or fewer if the project has no `.flowline`).
- Two log files from the same CI pipeline run share the same `Activity.TraceId` (I6 correlation).
- A log file from a CI run shows `ci=true` and `ci.platform=github` (or the appropriate platform).
- App Insights (I7, when shipped) can be queried by `pac.version`, `os.arch`, `ci`, `ci.platform`, and `env.hashes` as custom dimensions.
- No measurable startup latency increase for commands that already call `CheckSetupAsync` (versions are already retrieved there).
