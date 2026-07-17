---
title: Environment Existence/Type Check Independent of Active PAC Profile - Plan
type: refactor
date: 2026-07-17
topic: environment-check-drop-active-profile
artifact_contract: ce-unified-plan/v1
artifact_readiness: requirements-only
product_contract_source: ce-brainstorm
execution: code
---

# Environment Existence/Type Check Independent of Active PAC Profile - Plan

## Goal Capsule

- **Objective:** remove `pac admin list`'s active-PAC-profile dependency from Flowline's environment existence/Type checks, so `clone`, `push`, `deploy`, `drift`, and `generate` resolve environments correctly regardless of which PAC auth profile is globally active.
- **Product authority:** resolved in this brainstorm dialogue, grounded in live empirical verification against real Dataverse and Power Platform APIs (not docs inference).
- **Open blockers:** none — technical feasibility confirmed live.

## Product Contract

### Summary

Flowline's environment existence and Production/Sandbox Type check moves off `pac admin list` — which is scoped to whichever PAC auth profile happens to be globally active — onto per-command profile resolution. It resolves the target profile the same way the connect step already does (`FindBestProfile`, URL-matched against local PAC auth profiles), then retrieves existence and Type via a direct token read against that resolved profile's cached credentials, with no `pac.exe` subprocess involved.

### Requirements

**Profile & existence resolution**
- R1. Environment existence and Type checks resolve the target profile via the same URL-matching logic the connect step already uses (`FindBestProfile`), instead of requiring a specific PAC auth profile to be globally active.
- R2. Existence and Type data are retrieved via a direct token acquisition against the resolved profile's cached PAC CLI credentials — no `pac.exe` subprocess, no dependency on which profile is globally active — mirroring the existing Dataverse connection mechanism.
- R4. Profile resolution for the existence/Type check and for the subsequent Dataverse connect share a single resolved profile per command invocation, rather than resolving independently.

**Type/Production guard**
- R3. The Production/Sandbox Type guard (`role == Prod ⇔ Type == Production`) continues to gate `clone`, `deploy`, and any other role-aware command exactly as today, sourced from the new resolution mechanism instead of `pac admin list`.
- R6. Ambiguous-profile and no-matching-profile outcomes (multiple local profiles matching a URL, or none) continue to use the existing `ProfileResolutionService` behavior (active-for-kind tiebreak, then prompt/error) — unchanged by this fix.

**Command coverage**
- R5. This fix applies uniformly to every command that currently performs an existence/Type check via the shared helper: `clone`, `push`, `deploy`, `drift`, `generate`.

### Key Decisions

- **Existence/Type check moves off `pac.exe` entirely, onto direct MSAL-cache token reads.** Confirmed live that `pac.exe` has no way to scope a single invocation to a non-active profile — `pac solution list --environment <url>` uses the globally active identity regardless of the URL given, returning `403 Forbidden` when that identity doesn't belong to the target tenant. Any fix routed through `pac.exe` inherits the same active-profile coupling this work removes. A direct token read is the only mechanism confirmed profile-agnostic, and it's already proven safe in production for the Dataverse connection itself.
- **Production/Sandbox Type is sourced from the Power Platform BAP admin API (`api.bap.microsoft.com`), not the Dataverse organization service.** Confirmed live: the Dataverse `organization` table, `pac org who`, and `pac env list` all lack a genuine Production/Sandbox field (`pac env list`'s `EnvironmentIdentifier.Type` is a different, unrelated classification). Only the BAP admin-scoped environments API carries `environmentSku` (Production/Sandbox/Developer/Teams/Default) — the same source `pac admin list` already uses internally, called directly instead of through a `pac.exe` subprocess bound to the active profile.
- **No new permission requirement.** The BAP admin API access needed is the same access `pac admin list` already required. A user able to push or deploy solutions to an environment already has this level of access to that specific environment — Microsoft's documented inclusion rules state a Dataverse System Administrator role in an environment qualifies for that environment to appear in this API's results.

### Scope Boundaries

- No change to how `.flowline` selects Prod/Dev/UAT/Test URLs — still project config, untouched by this fix.
- No change to `ProfileResolutionService`'s ambiguous-match or no-match handling — reused as-is.
- Standalone-mode active-profile defaulting (push/generate without `.flowline`) is a separate, already-scoped plan (`docs/plans/2026-07-17-001-feat-standalone-active-profile-default-plan.md`), not part of this fix.

### Dependencies / Assumptions

- Assumes the resolved profile's cached PAC CLI token can silently acquire a BAP-scoped token via `AcquireTokenSilent` with no new interactive consent — confirmed live for a `UNIVERSAL`/user-kind profile. Behavior for service-principal profiles (`IsServicePrincipal`) is unverified; confirm live during planning before relying on it, since service-principal profiles use a different token acquisition path (`AcquireTokenForClient`) than user profiles.

### Acceptance Examples

- AE1. Clone targeting Prod, an unambiguous profile exists for the Prod URL, and a different, unrelated profile is globally active.
  - **Given:** `.flowline`'s `ProdUrl` matches exactly one local PAC auth profile; a different tenant's profile is globally active.
  - **When:** `flowline clone <solution>` runs.
  - **Then:** existence and Type resolve correctly via the matched profile; the command proceeds without requiring `pac auth select`.
  - **Covers:** R1, R2, R4.
- AE2. Push targeting the Dev role, but the resolved environment's Type is Production.
  - **Given:** `.flowline`'s `DevUrl` accidentally points at a Production-type environment.
  - **When:** `flowline push` runs.
  - **Then:** the command refuses with the existing Type-guard error, sourced from the BAP-derived Type instead of `pac admin list`.
  - **Covers:** R3.
- AE3. Multiple local profiles match the same URL.
  - **Given:** two PAC auth profiles both point at the same environment URL.
  - **When:** any covered command runs.
  - **Then:** existing ambiguous-profile behavior fires unchanged (active-for-kind tiebreak, else prompt or error).
  - **Covers:** R6.

### Sources / Research

- `pac solution list --environment <url>` uses the globally active identity regardless of the URL argument — verified live (returned `403 Forbidden` when the active profile's tenant didn't match the URL's tenant).
- Dataverse `organization` table has no Production/Sandbox field — verified live via `pac env fetch --xml "<fetch><entity name='organization'><all-attributes/></entity></fetch>"` against the real AutomateValue Prod environment; no matching column across the full attribute set.
- `pac org who` has no Type field — verified live.
- `pac env list --json`'s `EnvironmentIdentifier.Type` does not correspond to Production/Sandbox — cross-referenced live against known `pac admin list` values for the same four environments (Production and Sandbox environments both returned `Type: 1`).
- BAP admin API `GET https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments?api-version=2020-10-01` returns `properties.environmentSku` (Production/Sandbox/Developer/Teams/Default) — verified live via a direct MSAL-cache token acquisition mirroring `DataverseConnector.ConnectViaPacAsync` (`src/Flowline.Core/Services/DataverseConnector.cs:25`); no `pac.exe` involved, no active-profile dependency, succeeded silently with no new interactive consent.
- Microsoft's "Troubleshoot missing environments" doc confirms `pac admin list`'s inclusion rule: a Dataverse System Administrator role in a specific environment qualifies for that environment to appear in the caller's results, independent of tenant-wide admin rights.
- Existing shared existence-check entry point: `FlowlineCommand.GetAndCheckEnvironmentInfoAsync` (`src/Flowline/Commands/FlowlineCommand.cs:136`), used by clone/push/deploy/drift/generate; standalone push path: `PushCommand.GetAndCheckStandaloneEnvironmentAsync` (`src/Flowline/Commands/PushCommand.cs:560`).
- Profile resolution mechanism to reuse: `DataverseConnector.FindBestProfile` (`src/Flowline.Core/Services/DataverseConnector.cs:215`), `ProfileResolutionService.ResolveAsync` (`src/Flowline/Services/ProfileResolutionService.cs:17`).
