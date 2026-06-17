---
title: "feat: Add xrmcontext rewrite (DataverseProxyGenerator) as --generator xrmcontext"
type: feat
date: 2026-06-18
origin: docs/plans/2026-06-17-001-feat-generate-xrmcontext-generator-plan.md
---

# feat: Add xrmcontext rewrite (DataverseProxyGenerator) as `--generator xrmcontext`

## Summary

Add `--generator xrmcontext` to `flowline generate`, backed by the `DataverseProxyGenerator`
dotnet tool (NuGet ID: `XrmContext`, rewrite branch of [delegateas/XrmContext](https://github.com/delegateas/XrmContext/tree/rewrite)).
Flowline validates tool availability, generates a temp `appsettings.json` from `.flowline` config,
and invokes `dotnet tool run xrmcontext`. Custom API generation is enabled by default, replacing
the PAC fallback. The old F# exe (`Delegate.XrmContext`) is renamed to `--generator xrmcontext-fs`.

---

## Problem Frame

The existing plan (`docs/plans/2026-06-17-001-feat-generate-xrmcontext-generator-plan.md`)
wraps the old `Delegate.XrmContext` F# exe (Alt A) as a bridge, with a Flowline-native generator
deferred long-term. The xrmcontext rewrite changes the calculus:

- It is a modern C# .NET 8 dotnet tool — no exe extraction, no connection string surgery
- It covers Custom APIs natively — the primary gap in the old version
- It is cross-platform — no Windows-only constraint
- It is effectively release-ready (checked 2026-06-18): CI wired, no TODOs, one `v*` tag away
- The orchestration layer is simpler than Alt A — no `XrmContextToolProvider`, no
  `BuildXrmContextConnectionString`; Flowline writes a temp `appsettings.json` and invokes
  `dotnet tool run xrmcontext`

This makes a Flowline-native generator unnecessary long-term. Alt A (`xrmcontext-fs`) remains
as a bridge for projects that need a generator before the rewrite ships to NuGet.

---

## Generator Naming

| Flag value           | Backing tool                  | Status        |
|----------------------|-------------------------------|---------------|
| `pac`                | `pac modelbuilder build`       | Current default |
| `xrmcontext`         | DataverseProxyGenerator rewrite | This document |
| `xrmcontext-fs`      | Delegate.XrmContext F# exe    | Renamed from `xrmcontext` in original plan |

The old plan used `--generator xrmcontext` for the F# exe. That value is reassigned to the
rewrite; the F# exe gets `xrmcontext-fs`. The `GeneratorType` enum gains a third member:
`XrmContextRewrite` (serializes as `"xrmcontext"`); the existing `XrmContext` member serializes
as `"xrmcontext-fs"`.

---

## Requirements

**Generator selection**

- R1. `flowline generate --generator xrmcontext` selects the DataverseProxyGenerator rewrite.
- R2. `flowline generate --generator xrmcontext-fs` selects the legacy F# exe (renamed from
  the original `xrmcontext` value in the existing plan).
- R3. Generator choice persists to `.flowline` under `generate.generator` on every run, same
  as the existing save behavior.

**Tool availability**

- R4. Before invoking, Flowline checks that `xrmcontext` is available as a dotnet tool (global
  or local manifest). If not found, throw `FlowlineException(ExitCode.BuildFailed)` with a
  message instructing the user to run `dotnet tool install -g xrmcontext`. No auto-install.
- R5. Availability check mirrors the PAC PATH check pattern — validate and fail fast; do not
  attempt to install on the user's behalf.

**Configuration**

- R6. Flowline generates a temp `appsettings.json` from `.flowline` config before each run
  and deletes it on completion (success or failure). Users never manage this file directly.
- R7. The `DATAVERSE_URL` key in the generated config is populated from the active PAC profile
  environment URL. Auth is handled by `DefaultAzureCredential` (Azure SDK) — for service
  principal PAC profiles, Flowline injects `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`, and
  `AZURE_TENANT_ID` as environment variables when spawning the tool process. For user/UNIVERSAL
  profiles, auth falls through to Azure CLI (`az login`) — if not present, the tool fails with
  a credential error. *(see Open Questions — user profile auth)*
- R8. The generated config sets `XrmContext.Solutions` from the solution name, `XrmContext.Entities`
  from `extraTables` (if any), and `XrmContext.NamespaceSetting` from the resolved namespace
  (same derivation chain as PAC and xrmcontext-fs).
- R9. `GenerateCustomApis: true` is set in the generated config by default. Custom API
  generation runs as part of the xrmcontext invocation — no PAC fallback needed.
- R10. `XrmContext.OutputDirectory` is set to the temp output path (`Plugins/Models~`); the
  existing temp-swap renames it to `Plugins/Models/` on success.

**Invocation**

- R11. Flowline writes the generated `appsettings.json` to a temp working directory and sets
  that directory as the working directory for the `dotnet tool run xrmcontext` invocation.
  The tool has no `--appsettings` flag — it reads `appsettings.json` from its working
  directory via standard `Microsoft.Extensions.Configuration` discovery.
- R12. Invocation uses the same CliWrap + `WithToolExecutionLog` + verbose-logging pattern as
  the PAC path and xrmcontext-fs.
- R13. Non-zero exit bubbles as a `FlowlineException` — no silent catch.

---

## Key Technical Decisions

**Temp appsettings.json, not CLI flags.** The rewrite is config-file-driven; CLI flags are
overrides, not the primary interface. Generating a complete temp `appsettings.json` from
`.flowline` ensures Flowline controls the full config surface without exposing appsettings
management to the user. The file is discarded after the run — no persistent config file to
drift from `.flowline`.

**Auth via `DefaultAzureCredential`, not PAC connection string.** The rewrite uses the Azure
SDK `DefaultAzureCredential` chain. For service principal PAC profiles, Flowline extracts
AppId/Secret from the profile and injects `AZURE_CLIENT_ID` / `AZURE_CLIENT_SECRET` /
`AZURE_TENANT_ID` as environment variables on the spawned process — no connection string in
CLI args, no secret visible in process listings. For user/UNIVERSAL profiles, Flowline cannot
bridge the PAC MSAL cache to `DefaultAzureCredential`; the tool falls through to Azure CLI
credentials. This is a known limitation: user profile support requires the developer to also
have `az login` active for the same tenant.

**Working directory, not `--appsettings`.** The tool reads `appsettings.json` from its
working directory via standard `Microsoft.Extensions.Configuration` — no path override flag
exists. Flowline writes the temp config to a scratch directory and passes that as the working
directory to CliWrap. The temp directory is cleaned up after the run, same as the temp output
folder swap.

**Custom APIs on by default.** The rewrite generates Custom API types natively. Enabling them
by default closes the capability gap vs the PAC path without requiring an extra flag. The
existing PAC-based custom API discovery (`if (generator == Pac)`) remains gated on PAC only.

**`xrmcontext` reassigned; `xrmcontext-fs` for the legacy tool.** The rewrite is the
canonical `xrmcontext`; the F# exe gets the `fs` qualifier. Existing `.flowline` files with
`generator: xrmcontext` will need migration (one-time rename). The plan for xrmcontext-fs
should document this.

**No new `ICodeGenerator` abstraction.** Three generators (`pac`, `xrmcontext`, `xrmcontext-fs`)
still do not warrant an interface. The `if/else` branch in `GenerateCommand` gains one more
arm. Extract when a fourth materializes.

**Validate like PAC, not like xrmcontext-fs.** The xrmcontext-fs plan downloads and caches
an exe. The rewrite is a dotnet tool — the standard install mechanism. Flowline validates
presence and exits with instructions; it does not manage installation.

---

## Scope Boundaries

**In scope**
- `GeneratorType.XrmContextRewrite` enum member, serializing as `"xrmcontext"`
- Rename `GeneratorType.XrmContext` → `GeneratorType.XrmContextFs`, serializing as `"xrmcontext-fs"`
- Tool availability validator
- `XrmContextRewriteAppsettingsBuilder` — builds temp `appsettings.json` from `.flowline` config
- `XrmContextRewriteRunner` — CliWrap invocation
- `GenerateCommand` branch for `XrmContextRewrite`
- Wiki: `Command-Reference.md` — document new flag value and rename

**Deferred**
- Nullable types opt-in (`NullableTypes: true`) — add to `.flowline` config when user demand exists
- Intersection interfaces, alternate key helpers — rewrite supports them; expose when requested
- Migration from `generator: xrmcontext` (old) → `generator: xrmcontext-fs` — handle in the
  xrmcontext-fs plan update, not here
- .NET 10 DNX runner — when Flowline targets .NET 10, tool invocation may not require global
  install; adapt the availability check at that point

**Out of scope**
- `XrmContextToolProvider` (exe extraction) — only needed for xrmcontext-fs
- `BuildXrmContextConnectionString` — only needed for xrmcontext-fs
- Custom API opt-out flag — enabled by default; add opt-out only if a use case emerges

---

## Risks & Dependencies

- **Rewrite not yet released**: As of 2026-06-18, one `v*` tag push away from NuGet. CI is
  wired, no blockers found. Risk: tag may not drop before Flowline needs to ship the generator
  feature. Mitigation: implement xrmcontext-fs (Alt A) first as a bridge; migrate to this plan
  when the tag drops. Dev testing: build locally from `E:/Code/delegateas/XrmContext` and
  install via `dotnet tool install --add-source <local-build-path> -g xrmcontext`.
- **User profile auth gap**: `DefaultAzureCredential` does not read the PAC MSAL cache.
  Developers using interactive PAC auth (`pac auth create` with personal account) need `az
  login` active for the same tenant. Service principal profiles are unaffected — Flowline
  injects the credentials as env vars. Mitigation: clear error message directing user-profile
  users to `az login`; document limitation in wiki.
- **Tenant ID extraction**: TenantId must be available from the PAC profile to set
  `AZURE_TENANT_ID`. If not stored explicitly, fallback to resolving from the environment URL.
  Verify during implementation.
- **`.flowline` migration**: Existing projects with `generator: xrmcontext` (set by the
  xrmcontext-fs plan) will silently switch to the rewrite after this ships. Decide in
  implementation whether to migrate automatically on read or warn + require explicit re-save.

---

## Open Questions

**Resolved:**
- **`--appsettings` CLI support**: No flag exists. Tool reads from working directory via
  `Microsoft.Extensions.Configuration`. Flowline sets working directory on the CliWrap
  invocation. *(verified 2026-06-18)*
- **Config shape**: `DATAVERSE_URL` is a top-level key (not a `DataverseConnection` section).
  Auth is `DefaultAzureCredential`. *(verified 2026-06-18)*

**Deferred to implementation:**
- **User/UNIVERSAL profile auth**: `DefaultAzureCredential` does not read the PAC MSAL cache.
  Options: (a) throw `FlowlineException(NotAuthenticated)` with a message directing users to
  run `az login` for user profiles, (b) investigate whether the PAC token cache path can be
  injected as a `SharedTokenCacheCredential` hint. Decision: validate during implementation
  by testing user profile behavior against a real environment.
- **Service principal tenant ID source**: PAC profiles store AppId and Secret but may or may
  not store TenantId explicitly. Verify `DataverseConnector.FindBestProfile()` exposes TenantId
  for service principal profiles; if not, derive from the environment URL (tenant can be
  resolved from the org URL via the discovery service).

---

## Sources

- `docs/plans/2026-06-17-001-feat-generate-xrmcontext-generator-plan.md` — Alt A plan; flag
  rename and `GeneratorType` changes must be coordinated with this plan
- `docs/brainstorms/2026-06-17-generate-xrmcontext-support-requirements.md` — original
  xrmcontext requirements (Alt A origin)
- `E:/Code/delegateas/XrmContext/` (rewrite branch) — source for capability and config surface
  verification
- `src/Flowline/Commands/GenerateCommand.cs` — PAC invocation, temp-swap, save behavior
- `src/Flowline.Core/Services/DataverseConnector.cs` — `FindBestProfile`, PAC auth patterns
