---
title: XrmContext v4 Auth Integration — appsettings.json Temp Dir and DefaultAzureCredential via PAC MSAL Cache
date: 2026-06-19
category: architecture-patterns
module: generate-command
problem_type: architecture_pattern
component: tooling
severity: medium
applies_when:
  - "Integrating a dotnet tool that uses DefaultAzureCredential and reads config from appsettings.json in its CWD"
  - "Auth must work for both UNIVERSAL (interactive/PAC-cached) and SP (client secret) PAC profiles without storing secrets"
  - "Passing inherited environment variables to a subprocess via CliWrap without replacing the full env"
  - "Windows: SharedTokenCacheCredential must find the PAC MSAL token cache for zero-config interactive auth"
tags:
  - xrmcontext
  - defaultazurecredential
  - shared-token-cache
  - appsettings-json
  - temp-dir
  - pac-profile
  - service-principal
  - cliwrap
related_components:
  - authentication
  - development_workflow
---

# XrmContext v4 Auth Integration — appsettings.json Temp Dir and DefaultAzureCredential via PAC MSAL Cache

## Context

XrmContext v4 dropped explicit credential args (`/method:OAuth`, `/mfaAppId`, `/mfaClientSecret`) in favour of `DefaultAzureCredential` from the Azure.Identity SDK. This creates two integration challenges when embedding it in Flowline:

1. **Config delivery**: v4 reads all settings (including `DATAVERSE_URL` and generation options) from `appsettings.json` in its current working directory — not from CLI args.
2. **Auth bridging**: Flowline uses PAC profiles (UNIVERSAL for interactive users, SP for CI). There are no explicit credential flags to pass to v4. Auth must route through `DefaultAzureCredential`'s credential chain automatically.

**Approaches ruled out before settling on this pattern (session history):**

- **`AccessToken=` injection** — fails; documented in project memory as not working (auto memory [claude])
- **`LoginPrompt=Auto`** — fails (auto memory [claude])
- **EBG V2** (alternative early-bound generator) — evaluated and rejected: only fixes casing (no typed enums, no service context), has a known assembly-loading bug when PAC is installed as a dotnet tool (`.NET Framework 4.8` vs `.NET 8` conflict), and 50+ config options with no clear subset
- **Passing credentials as CLI args** — not applicable to v4; that is the XrmContext3 approach

The solution leverages `DefaultAzureCredential`'s `SharedTokenCacheCredential` step, which picks up PAC's MSAL token cache on Windows without explicit plumbing.

## Guidance

### Pattern 1: appsettings.json injection via temp dir

XrmContext v4 picks up `appsettings.json` from its CWD. Create a unique temp dir per run, write the config there, set it as the subprocess working directory, and delete it in `finally`.

```csharp
// XrmContextGenerator.cs — RunAsync
var tempAppsettingsDir = Path.Combine(Path.GetTempPath(), $"flowline-xrmcontext-{Guid.NewGuid()}");

try
{
    Directory.CreateDirectory(tempAppsettingsDir);

    var json = BuildAppSettingsJson(
        context.DevUrl,
        context.TempOutputPath,
        context.ModelNamespace,
        context.SolutionName,
        context.ExtraTables,
        context.ServiceContextName ?? "XrmContext");

    await File.WriteAllTextAsync(
        Path.Combine(tempAppsettingsDir, "appsettings.json"),
        json, cancellationToken);

    // ...launch subprocess with .WithWorkingDirectory(tempAppsettingsDir)
}
finally
{
    if (Directory.Exists(tempAppsettingsDir))
        Directory.Delete(tempAppsettingsDir, recursive: true);
}
```

Generated `appsettings.json`:

```json
{
  "DATAVERSE_URL": "https://org.crm.dynamics.com",
  "XrmContext": {
    "OutputDirectory": "C:\\temp\\models~",
    "NamespaceSetting": "MySolution.Models",
    "ServiceContextName": "XrmContext",
    "Solutions": ["MySolution"],
    "GenerateCustomApis": true,
    "DeprecatedPrefix": "ZZ_"
  }
}
```

`Entities` is appended only when `extraTables` is non-empty.

### Pattern 2: Pre-create the output directory

XrmContext v4 (and v3) does **not** create the output directory itself — it fails silently or with a cryptic error if the directory is absent. Always pre-create before launching:

```csharp
Directory.CreateDirectory(tempOutputPath);
```

This requirement was independently discovered for both XrmContext3 (auto memory [claude]) and v4.

### Pattern 3: Auth routing via BuildEnvVars

`DefaultAzureCredential` walks a credential chain. Control which step wins by conditionally injecting environment variables.

**Critical CliWrap behaviour:** `.WithEnvironmentVariables(emptyDict)` **adds** to the inherited environment — it does NOT replace it. Passing an empty dict is not "no auth" — it means "inherit everything from parent process."

```csharp
// XrmContextGenerator.cs
internal static IReadOnlyDictionary<string, string?> BuildEnvVars(PacProfile? profile)
{
    var envVars = new Dictionary<string, string?>();

    if (profile?.IsServicePrincipal == true)
    {
        if (profile.ApplicationId is not null)
            envVars["AZURE_CLIENT_ID"] = profile.ApplicationId;

        if (profile.TenantId is not null)
            envVars["AZURE_TENANT_ID"] = profile.TenantId;

        var secret = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET");
        if (secret is not null)
            envVars["AZURE_CLIENT_SECRET"] = secret;
    }

    return envVars; // empty for non-SP — subprocess inherits full parent env
}
```

**For UNIVERSAL (interactive) profiles:** empty dict → subprocess inherits parent env → `DefaultAzureCredential` finds PAC's MSAL cache via `SharedTokenCacheCredential` on Windows.

**For SP profiles:** `AZURE_CLIENT_ID` and `AZURE_TENANT_ID` injected from the PAC profile. `AZURE_CLIENT_SECRET` read from parent process env — never stored in `.flowline` or PAC profiles. Guard before launching:

```csharp
if (profile?.IsServicePrincipal == true &&
    Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET") is null)
{
    throw new FlowlineException(ExitCode.ConfigInvalid,
        "AZURE_CLIENT_SECRET is required when authenticating with a Service Principal. Set the environment variable before running.");
}
```

### DefaultAzureCredential chain

For UNIVERSAL profiles on Windows:

```
EnvironmentCredential         → skipped (no AZURE_* vars injected)
WorkloadIdentityCredential    → skipped
ManagedIdentityCredential     → skipped
SharedTokenCacheCredential    → WINS — reads PAC MSAL cache from %LOCALAPPDATA%
VisualStudioCredential        → fallback
AzureCliCredential            → fallback
InteractiveBrowserCredential  → last resort
```

For SP profiles: `EnvironmentCredential` wins (all three `AZURE_*` vars injected).

### Pattern 4: Profile resolution

```csharp
var profile = dataverseConnector.FindBestProfile(context.DevUrl);
```

`FindBestProfile` returns a URL-specific profile first; falls back to UNIVERSAL. A null profile (no match) returns an empty dict — same behaviour as UNIVERSAL.

## Why This Matters

- **No secret leakage**: `AZURE_CLIENT_SECRET` is never written to disk or config. It flows from parent environment into the subprocess only at runtime.
- **PAC integration is zero-config for UNIVERSAL profiles**: Developers already authenticated via `pac auth create` get XrmContext v4 working without any additional setup — `SharedTokenCacheCredential` reuses the existing token silently.
- **Temp dir isolation**: Each run gets a unique GUID dir. Concurrent runs cannot collide, and config from one project cannot bleed into another.
- **Cleanup is guaranteed**: `try/finally` removes the temp dir even when the subprocess fails or throws.
- **Windows-only caveat**: `SharedTokenCacheCredential` finding the PAC MSAL cache is Windows-specific. On Linux/Mac the cache path differs and may not be supported — `AzureCliCredential` or `InteractiveBrowserCredential` would be the fallbacks.

## When to Apply

- Integrating any dotnet tool or external process that uses `DefaultAzureCredential` and reads config from `appsettings.json` in its CWD.
- Auth must work for both interactive-user (cached tokens) and SP (env-var secret) scenarios without storing secrets on disk.
- The orchestrating process already has PAC authenticated (`pac auth create` has been run at least once).
- Running on Windows (SharedTokenCacheCredential path). On Linux/Mac, verify which credential chain step finds the PAC cache, or require explicit env vars.

## Examples

### UNIVERSAL profile — developer machine, zero config

Developer runs `pac auth create -u https://org.crm.dynamics.com` once. Subsequent `flowline generate` calls:

1. `FindBestProfile` returns UNIVERSAL profile (`IsServicePrincipal == false`)
2. `BuildEnvVars` returns empty dict
3. Subprocess inherits parent env (no `AZURE_*` vars injected)
4. `DefaultAzureCredential` → `SharedTokenCacheCredential` → finds PAC MSAL cache → authenticated

No flags, no prompts.

### SP profile — CI pipeline

CI sets `AZURE_CLIENT_SECRET=<secret>` in the environment. `.flowline` config stores `generator: xrmcontext`. PAC profile contains `ApplicationId` and `TenantId`. `BuildEnvVars` injects all three `AZURE_*` vars. `EnvironmentCredential` wins.

### XrmContext3 vs XrmContext v4 auth comparison

| | XrmContext3 | XrmContext v4 |
|---|---|---|
| Auth mechanism | Explicit CLI args (`/method:OAuth`, `/mfaAppId`) | `DefaultAzureCredential` via env vars |
| Config delivery | CLI args | `appsettings.json` in subprocess CWD |
| UNIVERSAL profile | `/method:OAuth` + PAC AppId + browser popup | `SharedTokenCacheCredential` — silent |
| SP profile | `/method:ClientSecret` + explicit args | `EnvironmentCredential` via `AZURE_*` vars |
| Output dir | Must pre-create | Must pre-create |

## Related

- `src/Flowline/Generators/XrmContextGenerator.cs` — implementation
- `src/Flowline/Generators/xrmcontext-v4-auth-flow.md` — sequence diagram + flowchart
- `docs/brainstorm/2026-06-17-generate-xrmcontext-support-requirements.md` — v3 brainstorm (v4 supersedes)
- `project_xrmcontext_auth.md` (auto memory) — v3 ADAL auth decisions, partially stale with v4 in place
