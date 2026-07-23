# Verbose "Connecting via PAC auth profile ''" shows blank instead of the user email

- **Status**: fixed — 2026-07-23.
- **Severity**: low — cosmetic, verbose-mode output only, no functional impact (connection itself
  succeeded correctly; only the printed profile label was blank).
- **Found**: 2026-07-23, live, running `flowline push --dry-run --verbose` with the one remaining
  unnamed PAC auth profile active (same PAC-profile shape that caused
  `status-empty-profile-name-breaks-fallback.md`).

## Repro (pre-fix)

1. Have an unnamed PAC auth profile active.
2. Run any command that connects via PAC (`push`, `sync`, `deploy`, ...) with `--verbose`.
3. Expected: `Connecting via PAC auth profile 'someone@example.com' at ...`.
4. Actual: `Connecting via PAC auth profile '' at ...` — empty quotes.

## Root cause

`DataverseConnector.ConnectViaPacAsync` (`DataverseConnector.cs:54`, pre-fix) built the message with
`profile.Name ?? profile.User`. Same root cause as `status-empty-profile-name-breaks-fallback.md`:
PAC's `authprofiles_v2.json` gives an unnamed profile `Name: ""` (empty string), not null, so a bare
`??` chain never falls through to `User`.

## Fix applied

Extracted `DataverseConnector.ResolveProfileLabel(PacProfile)` (`DataverseConnector.cs`, next to the
existing `ResolveAuthority` static helper) using `string.IsNullOrWhiteSpace(profile.Name) ?
profile.User : profile.Name`, and used it at the verbose-log call site.

Regression tests: `DataverseConnectorTests.cs` —
`ResolveProfileLabel_NamedProfile_ReturnsName`, `ResolveProfileLabel_EmptyStringName_FallsBackToUser`
(the exact live shape), `ResolveProfileLabel_NullName_FallsBackToUser`. Full suite green after the fix
(1021 passing in Flowline.Core.Tests, 916 in Flowline.Tests).

## Live re-verification (post-fix)

Rebuilt (`dotnet pack src/Flowline/Flowline.csproj -c Release`), cleared the stale NuGet package cache
for the unchanged version string, reinstalled the global tool, and re-ran `flowline push --dry-run
--verbose`: the line now reads `Connecting via PAC auth profile 'remy@automatevalue.com' at
https://automatevalue-dev.crm4.dynamics.com...`.

## Root-cause sweep (same session)

Grepped `src/` for the same `Name ?? ...` fallback pattern and found two more instances of the exact
same bug, both fixed alongside this one (full test suite green, 917 + 1021 passing; CLI rebuilt and
reinstalled):

- `DataverseConnector.cs:233` (the AADSTS90072/50020 tenant-mismatch error message) — now reuses
  `ResolveProfileLabel`. Not live-verified (requires contriving a real tenant-mismatch MSAL failure);
  covered by the same `ResolveProfileLabel` unit tests since it's the identical helper.
- `SecretResolver.cs` (client-secret prompt/error for service-principal profiles, `Name ?? ApplicationId`
  instead of `Name ?? User`) — extracted its own local `ResolveProfileLabel` helper (different fallback
  field). New test: `SecretResolverTests.ResolveAsync_EmptyStringName_NonInteractive_FallsBackToApplicationId`.
  Not live-verified (requires a service-principal profile with an empty Name and no secret available);
  unit-tested only.

Confirmed clean (no third-plus instance): `ProfileResolutionService.cs` and `PacUtils.cs` already used
`string.IsNullOrEmpty`/`IsNullOrWhiteSpace` correctly at their `profile.Name` usages.
