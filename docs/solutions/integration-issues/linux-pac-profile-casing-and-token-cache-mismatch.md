---
title: "PAC CLI data directory casing and Linux secret store mismatch break Dataverse auth"
date: 2026-09-19
category: integration-issues
module: Flowline.Core.Services.DataverseConnector
problem_type: integration_issue
component: tooling
severity: critical
symptoms:
  - "Every Dataverse-touching command fails on Linux with \"PAC auth profile file not found\", naming a path that plainly exists"
  - "The MSAL cache helper silently creates an empty second PowerAppsCLI-cased folder beside PAC's real PowerAppsCli folder"
  - "After the path is corrected, connecting still fails with \"Session expired\", and re-running pac auth create never clears it"
  - "An error path shells out to `pac --version` and prints PAC's entire help text inside the exception message"
root_cause: wrong_api
resolution_type: code_fix
framework_version: "pac 2.12.2"
related_components:
  - DataverseConnector.GetPacCliDataDirectory
  - DataverseConnector.BuildLinuxKeyringStorageProperties
  - DataverseConnector.CreateMsalCacheHelperAsync
tags:
  - linux
  - macos
  - pac-cli
  - msal
  - keyring
  - cross-platform
  - auth-profile
  - dataverse-connector
---

# PAC CLI data directory casing and Linux secret store mismatch break Dataverse auth

## Problem

Flowline reuses PAC CLI's own cached authentication instead of asking users to sign in again, by reading PAC's on-disk auth-profile file and its MSAL token cache directly. The code that builds those paths and picks a token store assumed Windows path casing and encrypted-file storage. Every Dataverse-touching command failed on Linux as a result, in two layers: fixing the path casing only exposed the second defect underneath it (wrong secret store).

## Symptoms

- `flowline sync` (and any Dataverse-connecting command) failed with `Error: PAC auth profile file not found: /home/<user>/.local/share/Microsoft/PowerAppsCLI/authprofiles_v2.json` on a machine where PAC's real file plainly existed, one directory-casing away.
- A second, empty directory appeared beside PAC's real data folder, created by the wrongly-cased path Flowline itself constructed.
- After the casing was fixed, connecting still failed with `Session expired for <user>. Run 'pac auth create'...` even though `pac` itself worked fine and had a live cached session.
- A separate error path, when it fired, embedded PAC's entire `--help` output inside the exception message, burying the real error.

## What Didn't Work

- **Caching hypothesis for `flowline status`.** Initial suspicion was that `flowline status` cached the install type and was serving stale state. Real bug, but unrelated to auth — fixed separately, landed on master as `657dc4d` ("fix(status): probe the toolchain fresh instead of reading a week-old cache"). Ruled out as the cause of the auth failures.
- **Catching `MsalCacheHelper.CreateAsync`'s exception as the headless-detector.** First attempt wrapped `CreateAsync` in a try/catch to detect "no Secret Service reachable" (e.g. SSH session, no D-Bus bus). Measurement — running with `DBUS_SESSION_BUS_ADDRESS` unset — showed `CreateAsync` succeeds regardless; it never throws for a missing keyring. That branch was dead code. `src/Flowline.Core/Services/DataverseConnector.cs:150` confirms the actual detector is a call to `keyringHelper.VerifyPersistence()` after creation, not the constructor.
- **"Token cache persistence is broken on this box."** An intermediate hypothesis, disproved by writing a throwaway payload to the keyring under a test schema, running `VerifyPersistence()`, and reading the payload back unchanged. The observed 0-byte `tokencache_msalv3_keychain.dat` on disk is MSAL's expected sentinel file for the keyring-backed store, not evidence of a broken store — `src/Flowline.Core/Services/DataverseConnector.cs:125` names that file `PacLinuxCacheFileName`, used only as the cross-process lock file when the keyring is the real token store per the comment at `DataverseConnector.cs:120-122`.

## Solution

**Defect 1 — directory casing.** `GetPacCliDataDirectory()` built `Microsoft/PowerAppsCLI`; PAC's own binaries only ever write `Microsoft/PowerAppsCli` (lowercase `i`), confirmed by grepping PAC's shipped tool store. Fixed at `src/Flowline.Core/Services/DataverseConnector.cs:708-712`:

```csharp
// "PowerAppsCli" is PAC's own spelling, and the case matters: Windows resolves any casing, Linux
// and macOS do not.
internal static string GetPacCliDataDirectory()
{
    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    return Path.Combine(localAppData, "Microsoft", "PowerAppsCli");
}
```

Both callers of the wrong path — profile-file resolution (`LoadPacAuthProfiles`, `DataverseConnector.cs:721`) and the MSAL cache directory (`CreateMsalCacheHelperAsync`, `DataverseConnector.cs:129` and `:168`) — route through this one method, so the single fix covers both.

**Defect 2 — secret store.** `CreateMsalCacheHelperAsync()` called `WithLinuxUnprotectedFile()` unconditionally for any non-Windows OS, i.e. a plaintext file cache PAC never writes to. Fixed by adding a Linux-specific path that opens PAC's actual libsecret keyring entry, matched field-for-field, before falling back to the old plaintext-file behavior only when the keyring genuinely cannot be used:

```csharp
// src/Flowline.Core/Services/DataverseConnector.cs:128-136
internal static StorageCreationProperties BuildLinuxKeyringStorageProperties() =>
    new StorageCreationPropertiesBuilder(PacLinuxCacheFileName, GetPacCliDataDirectory())
        .WithLinuxKeyring(
            schemaName: PacLinuxKeyringSchema,
            collection: "default",
            secretLabel: PacLinuxKeyringLabel,
            attribute1: new KeyValuePair<string, string>("CacheKind", "MSAL_Token_Cache"),
            attribute2: new KeyValuePair<string, string>("Version", "1"))
        .Build();

// src/Flowline.Core/Services/DataverseConnector.cs:140-159
if (OperatingSystem.IsLinux())
{
    try
    {
        var keyringHelper = await MsalCacheHelper.CreateAsync(BuildLinuxKeyringStorageProperties());
        keyringHelper.VerifyPersistence();   // CreateAsync alone does not detect a missing Secret Service
        return keyringHelper;
    }
    catch (MsalCachePersistenceException)
    {
        // Fall through to the plaintext file below (headless: CI, Docker, WSL, no D-Bus session).
    }
}
```

The constants at `DataverseConnector.cs:123-125` are PAC's exact keyring identity:

```
schema      com.microsoft.powerapps.cli
attribute   CacheKind = MSAL_Token_Cache
attribute   Version = 1
label       MSAL token cache for Power Platform CLI
cache file  tokencache_msalv3_keychain.dat
```

**Defect 3 — version probe.** `GetPacCliVersion()` used to shell out `pac --version`, which PAC 2.12 parses as an unknown argument and answers with its full help text, landing inside error messages. Fixed to read the version the startup toolchain check already resolved instead of probing again — `src/Flowline.Core/Services/DataverseConnector.cs:773`:

```csharp
string GetPacCliVersion() => runtimeOptions?.ToolVersions?.PacVersion ?? "unknown";
```

## Why This Works

Both defects share one root cause: code written and tested against Windows behavior that Windows itself masks. Windows resolves file paths case-insensitively, so a wrong-cased `PowerAppsCLI` silently matched PAC's real `PowerAppsCli` folder and nobody saw defect 1 until running on Linux. Windows also reads PAC's cache through the OS broker/DPAPI, never touching `WithLinuxUnprotectedFile()`, so defect 2's branch was Linux/macOS-only code that had never actually been exercised against a real PAC-written store — it always constructed a store, just never the one PAC itself was writing to.

The keyring fix works because MSAL's secret lookup is exact-match on schema plus every attribute (`DataverseConnector.cs:117-122`): supplying PAC's real identity is what makes Flowline read the same secret `pac auth create` wrote, rather than silently returning nothing (which is indistinguishable from "no token" and surfaces as a misleading expired-session error). The `VerifyPersistence()` gate is necessary because `MsalCacheHelper.CreateAsync` does not fail when no Secret Service is reachable; only a verified round-trip proves the store actually works, so gating on `CreateAsync` alone (the first attempt) would never catch a headless environment and would instead fail later, deeper in the call stack, in a way harder to diagnose.

## Prevention

Regression tests exist specifically because Windows cannot catch these defects itself:

- `tests/Flowline.Core.Tests/DataverseConnectorTests.cs:710` (`GetPacCliDataDirectory_UsesPacsOwnCasingForTheFolder`) asserts the exact folder name `PowerAppsCli` and its parent `Microsoft`. The test comment at `DataverseConnectorTests.cs:706-708` states why: Windows resolves either casing, so nothing on the primary (Windows) dev machine would catch a case regression — this test is the only thing that would.
- `tests/Flowline.Core.Tests/DataverseConnectorTests.cs:676-689` (`BuildLinuxKeyringStorageProperties_MatchesPacsOwnKeyringEntry`) and `:691-704` (`BuildLinuxKeyringStorageProperties_MatchesPacsAttributesAndCacheFileName`) reflect into the built `StorageCreationProperties` and assert the schema name, collection, label, both attributes, and cache file name match PAC's real keyring entry field-for-field. The comment at `DataverseConnectorTests.cs:671-675` explains the reflection: MSAL keeps these fields internal, so the test reaches in deliberately — if MSAL ever renames them the test breaks loudly, which is the point, since the fix's correctness depends on that exact shape.

Still open: macOS. `CreateMsalCacheHelperAsync` (`DataverseConnector.cs:161-171`) deliberately leaves macOS on the old `WithLinuxUnprotectedFile()`-less plaintext-file path — per the comment at `DataverseConnector.cs:163-165`, PAC's Keychain service name and account name could not be verified from a Linux machine, and guessing would reproduce the identical bug somewhere nobody testing on Linux or Windows could reproduce it. `MacKeyChainServiceName` / `MacKeyChainAccountName` on the builder are null by default (verified by reflecting over the built `StorageCreationProperties`), so nothing currently targets Keychain at all on macOS. To finish this, a future engineer on a real Mac needs to capture, from a Keychain entry PAC itself wrote (via `pac auth create` then inspecting Keychain Access or `security find-generic-password`): the exact service name, the account name, and confirmation of which token-cache file name PAC pairs with that entry (parallel to `PacLinuxCacheFileName` on Linux). Apply the same pattern as the Linux fix — read PAC's real values, add a macOS branch in `CreateMsalCacheHelperAsync` mirroring the `OperatingSystem.IsLinux()` block, and add a reflection-based test parallel to the two Linux keyring tests above, since the same masking risk — nothing on Windows or Linux CI catches a wrong macOS Keychain identity — applies identically.

## Related

- [`docs/solutions/architecture-patterns/xrmcontext-v4-auth-integration.md`](../architecture-patterns/xrmcontext-v4-auth-integration.md) reaches the same PAC token cache from the XrmContext generation side. Its line 181 defers Linux and Mac support as uncertain, which this fix partly settles for Linux; review the two together rather than letting them drift.
- [`docs/solutions/conventions/internal-vs-public-documentation-split.md`](../conventions/internal-vs-public-documentation-split.md) is why the concrete cache paths above live here rather than in the public wiki.
- `docs/auth.md` carries the user-facing version of the per-platform table and was corrected in the same work.

Landed on master as `1ff0ee3` (casing, version probe) and `45fbd53` (keyring). Session history found no prior sessions covering this problem.
