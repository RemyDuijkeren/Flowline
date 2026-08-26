---
title: "feat: Add xrmcontext rewrite (XrmContext v4) as --generator xrmcontext"
type: feat
date: 2026-06-18
origin: docs/plans/2026-06-17-001-feat-generate-xrmcontext-generator-plan.md
---

# feat: Add XrmContext v4 as `--generator xrmcontext`

## Summary

Add `--generator xrmcontext` to `flowline generate`, backed by the `XrmContext` v4 dotnet
tool (NuGet: [XrmContext](https://www.nuget.org/packages/XrmContext), v4.0.0-beta.25 published
March 2026). Flowline validates tool availability, generates a temp `appsettings.json` from
`.flowline` config, and invokes `dotnet tool run xrmcontext`. Custom API generation is enabled
by default. The old F# exe (`Delegate.XrmContext` v3) uses `--generator xrmcontext3`.

---

## Problem Frame

The existing plan (`docs/plans/2026-06-17-001-feat-generate-xrmcontext-generator-plan.md`)
wraps the old `Delegate.XrmContext` F# exe (`xrmcontext3`) as a bridge. The v4 rewrite is now
available on NuGet (beta) and is the better long-term option:

- C# .NET 8 dotnet tool — no exe extraction, no connection string surgery
- Custom APIs natively — the primary gap in v3
- Cross-platform — no Windows-only constraint
- Simpler orchestration — no `XrmContextToolProvider`, no `BuildXrmContextConnectionString`;
  Flowline writes a temp `appsettings.json` and invokes `dotnet tool run xrmcontext`

A Flowline-native generator is not needed. `xrmcontext3` remains as a bridge for projects
that cannot yet migrate to v4.

---

## Generator Naming

| Flag value      | Backing tool                  | Status          |
|-----------------|-------------------------------|-----------------|
| `pac`           | `pac modelbuilder build`       | Default         |
| `xrmcontext`    | `XrmContext` v4 dotnet tool   | This document   |
| `xrmcontext3`   | `Delegate.XrmContext` v3 exe  | Bridge, no new investment |

The `GeneratorType` enum gains a member `XrmContext` (serializes as `"XrmContext"`); the
existing `XrmContext3` member (serializes as `"XrmContext3"`) remains for the F# exe.

---

## Requirements

**Generator selection**

- R1. `flowline generate --generator xrmcontext` selects the XrmContext v4 dotnet tool.
- R2. `flowline generate --generator xrmcontext3` selects the legacy F# exe.
- R3. Generator choice persists to `.flowline` under `generate.generator` on every run.

**Tool availability**

- R4. Before invoking, Flowline resolves the best available invocation for `XrmContext` v4,
  in priority order:
  1. `dnx XrmContext --prerelease` — if `dnx` is available (.NET 10 one-shot runner), prefer
     this; no install required, same pattern as the PAC `dnx microsoft.powerapps.cli.tool`
     path. `--prerelease` is a `dnx` option (controls NuGet resolution, not forwarded to
     XrmContext); required while v4 is in beta. Drop it once v4 hits stable.
     Invocation shape: `dnx <packageId> [commandArguments...] [dnx-options]`.
  2. `dotnet tool run xrmcontext` — if XrmContext is installed as a global or local dotnet tool.
  3. Fail with `FlowlineException(ExitCode.BuildFailed)` instructing the user to run
     `dotnet tool install -g XrmContext`. No auto-install.
- R5. Availability check mirrors the PAC PATH check pattern — validate and fail fast.

**Configuration**

- R6. Flowline generates a temp `appsettings.json` from `.flowline` config before each run
  and deletes it on completion (success or failure). Users never manage this file directly.
- R7. The `DATAVERSE_URL` key in the generated config is populated from the active PAC profile
  environment URL. Auth uses `DefaultAzureCredential` (Azure SDK) — for service principal PAC
  profiles, Flowline injects `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`, and `AZURE_TENANT_ID`
  as environment variables when spawning the tool process. For user/UNIVERSAL profiles, auth
  falls through to `DefaultAzureCredential`'s remaining chain (shared MSAL cache, Azure CLI,
  interactive browser). *(see Open Questions — user profile auth)*
- R8. The generated config sets `XrmContext.Solutions` from the solution name,
  `XrmContext.Entities` from `extraTables` (if any), and `XrmContext.NamespaceSetting` from
  the resolved namespace (same derivation chain as PAC and xrmcontext3).
- R9. `GenerateCustomApis: true` is set in the generated config by default — no PAC fallback
  needed.
- R10. `XrmContext.OutputDirectory` is set to the temp output path (`Plugins/Models~`); the
  existing temp-swap renames it to `Plugins/Models/` on success.

**Invocation**

- R11. Flowline writes the generated `appsettings.json` to a temp working directory and sets
  that directory as the working directory for the `dotnet tool run xrmcontext` invocation.
  The tool reads `appsettings.json` from its working directory — no `--appsettings` flag
  exists. *(verified 2026-06-18)*
- R12. Invocation uses the same CliWrap + `WithToolExecutionLog` + verbose-logging pattern as
  the PAC path and xrmcontext3.
- R13. Non-zero exit bubbles as a `FlowlineException` — no silent catch.

---

## Key Technical Decisions

**Temp appsettings.json, not CLI flags.** Generating a complete temp `appsettings.json` from
`.flowline` gives Flowline full control of the config surface without exposing appsettings
management to the user. Discarded after the run — no drift from `.flowline`.

**Auth via `DefaultAzureCredential`, not connection string.** For service principal PAC
profiles, Flowline extracts AppId/Secret and injects `AZURE_CLIENT_ID` / `AZURE_CLIENT_SECRET`
/ `AZURE_TENANT_ID` as env vars on the spawned process — no secret in CLI args or process
listings. For user profiles, `DefaultAzureCredential` tries `SharedTokenCacheCredential`
(may overlap with PAC's MSAL cache), then Azure CLI, then interactive browser. Behavior
differs from xrmcontext3's explicit connection string.

**Working directory, not `--appsettings`.** Tool reads `appsettings.json` from working
directory via `Microsoft.Extensions.Configuration`. Flowline sets the CliWrap working
directory to the temp config folder. *(verified 2026-06-18)*

**Custom APIs on by default.** The v4 rewrite generates Custom API types natively. Enabled
unconditionally — no PAC fallback, no opt-out flag until a use case emerges.

**Validate like PAC, not like xrmcontext3.** xrmcontext3 downloads and caches an exe. v4 is
a dotnet tool — validate presence, fail with instructions; do not manage installation.

**Implement against beta.** v4.0.0-beta.25 is on NuGet (March 2026). Flowline is pre-v1.0 —
beta dependency is acceptable. Track for stable release and update install instructions when
it ships.

---

## Scope Boundaries

**In scope**
- `GeneratorType.XrmContext` enum member (new, serializes as `"XrmContext"`)
- Tool availability resolver — `dnx XrmContext` (priority 1) or `dotnet tool run xrmcontext` (priority 2), fail with instructions if neither
- `XrmContextRewriteAppsettingsBuilder` — builds temp `appsettings.json` from `.flowline`
- `XrmContextRewriteRunner` — CliWrap invocation with working directory set to temp config dir
- `GenerateCommand` branch for `XrmContext`
- Wiki: `Command-Reference.md` — document `--generator xrmcontext` and its auth behavior

**Deferred**
- Nullable types opt-in (`NullableTypes: true`) — expose when user demand exists
- Intersection interfaces, alternate key helpers — v4 supports them; expose when requested
- .NET 10 DNX runner — covered by R4 (priority 1); moved to in-scope

**Out of scope**
- `XrmContextToolProvider` (exe extraction) — only needed for xrmcontext3
- `BuildXrmContextConnectionString` — only needed for xrmcontext3
- Custom API opt-out flag — enabled by default

---

## Risks & Dependencies

- **Beta dependency**: v4.0.0-beta.25 — breaking changes possible before stable. Low risk:
  Flowline is pre-v1.0 and the beta has been stable since March 2026. Pin to a minimum
  version; update when stable ships.
- **User profile auth**: `DefaultAzureCredential` behavior for personal PAC accounts is
  unvalidated against real environments. `SharedTokenCacheCredential` may overlap with PAC's
  MSAL cache, making auth transparent; or it may fall through to interactive browser.
  Validate during implementation. Service principal profiles are clean.
- **Tenant ID extraction**: PAC profiles may not store TenantId explicitly. Verify
  `DataverseConnector.FindBestProfile()` exposes it; if not, resolve from the environment URL.
- **`.flowline` migration**: Pre-existing `.flowline` files with `generator: XrmContext3`
  (written by xrmcontext3) are unaffected. No `generator: XrmContext` values exist in the
  wild (feature is new).

---

## Open Questions

**Resolved:**
- **`--appsettings` CLI support**: No flag — tool reads from working directory. Flowline sets
  CliWrap working directory. *(verified 2026-06-18)*
- **Config shape**: `DATAVERSE_URL` is a top-level key. Auth is `DefaultAzureCredential`.
  *(verified 2026-06-18)*
- **NuGet availability**: v4.0.0-beta.25 on NuGet since March 2026 — can implement now.
  *(verified 2026-06-18)*

**Deferred to implementation:**
- **User/UNIVERSAL profile auth**: Does `SharedTokenCacheCredential` read the PAC MSAL cache
  transparently? Validate against a real user-profile environment. If not transparent, surface
  a clear error directing users to run `az login`.
- **Tenant ID source**: Verify `DataverseConnector.FindBestProfile()` exposes TenantId for
  service principal profiles; if absent, derive from org URL via discovery service.

---

## Sources

- NuGet: [XrmContext v4.0.0-beta.25](https://www.nuget.org/packages/XrmContext) — March 2026,
  5.6K downloads; [Delegate.XrmContext v3.0.1](https://www.nuget.org/packages/Delegate.XrmContext)
  — Sep 2022, 486K downloads
- Rewrite source: `E:/Code/delegateas/XrmContext/` (`rewrite` branch) — CLI parsing in
  `src/DataverseProxyGenerator.Tool/CommandLineParser.cs`, config in
  `src/DataverseProxyGenerator.Tool/Configuration/SimpleXrmContextConfigBuilder.cs`
- `docs/plans/2026-06-17-001-feat-generate-xrmcontext-generator-plan.md` — xrmcontext3 plan;
  `GeneratorType` changes must be coordinated
- `docs/brainstorms/2026-06-18-flowline-generator-strategy-requirements.md` — generator strategy
- `src/Flowline/Commands/GenerateCommand.cs` — PAC invocation, temp-swap, save behavior
- `src/Flowline.Core/Services/DataverseConnector.cs` — `FindBestProfile`, PAC auth patterns
- `src/Flowline/Utils/PacUtils.cs` — `GetBestPacCommandAsync` dnx resolution pattern to mirror
- `dnx XrmContext --prerelease --help` *(verified 2026-06-18)*: `--prerelease` is a `dnx` option
  (NuGet resolver, not forwarded to the tool); invocation shape is
  `dotnet dnx <packageId> [commandArguments...] [options]`
