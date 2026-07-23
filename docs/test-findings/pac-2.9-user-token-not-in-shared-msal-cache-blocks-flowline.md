# Flowline user-auth breaks on session expiry: PAC 2.9.x refresh doesn't repopulate the shared MSAL cache

- **Status**: **fixed and live-verified (2026-07-23)** — WAM broker support added to the user-auth
  path. `flowline clone` + `flowline status` against DEV now connect silently (`✓ remy@automatevalue.com`,
  no "Session expired"), full suite green. See "Confirmed root cause" and "Fix" below.
- **Severity**: high (operational) — blocks *all* live Flowline commands for user (non-SPN) auth
  profiles once the cached token expires, even while `pac` itself keeps working. Not a data-safety
  issue; the error message and exit code are correct.

## Repro (observed 2026-07-23, session 3)

Machine: single unnamed **UNIVERSAL** PAC auth profile (`remy@automatevalue.com`), PAC CLI **2.9.3**.

1. `pac org who` → `Connected as remy@automatevalue.com` (works).
2. `pac org select --environment https://automatevalue-dev.crm4.dynamics.com` → `Connected to...
   AutomateValue Dev` (works, non-interactive — refresh token still valid).
3. `pac org who --environment <devurl>` → returns full org info (real Dataverse token acquired).
4. `pac solution export --name Cr07982 ...` → `Solution export succeeded` (real Dataverse call, works).
5. **Immediately after**, any `flowline` command that connects (`clone`, `push`, `sync`, `deploy`,
   `drift`, `status`) → `Error: Session expired for 'remy@automatevalue.com'. Run 'pac auth create
   --url <url>' to re-authenticate.` (exit 4 / NotAuthenticated).

So a *fresh, successful* PAC connection to the exact target environment does **not** unblock Flowline.

## Root cause

`DataverseConnector.CreateMsalCacheHelperAsync` (`src/Flowline.Core/Services/DataverseConnector.cs`)
reads the PAC token cache file `tokencache_msalv3.dat` in the PAC CLI data directory via
`MsalCacheHelper`, then `AcquireUserTokenAsync` calls `AcquireTokenSilent` with an account selected
from `app.GetAccountsAsync()`. The inner failure is
`MsalUiRequiredException: No account or login hint was passed to the AcquireTokenSilent call` — i.e.
`GetAccountsAsync()` returns **empty**: the file cache has no account for the Dataverse
`.../.default` scope.

## Confirmed root cause (WAM broker) — probe-verified 2026-07-23

The profile shows **`Type: OperatingSystem`** in `pac auth list`. That is PAC's credential-storage
backend: the token lives in the **Windows OS broker (WAM)**, not in the file MSAL cache. Confirmed by
direct probe against the real cache (throwaway MSAL harness in `Flowline.Core.Tests`, since removed):

- `tokencache_msalv3.dat` exists and is non-empty (3542 bytes, recently written) — so "PAC stopped
  writing the file" was *not* the cause.
- Yet `GetAccountsAsync()` returns **0 accounts** for Flowline's app id `9cee029c-…`, *and* for every
  other candidate first-party app id tried (Dynamics sample, Azure PowerShell/CLI/AD, Dataverse
  first-party, published PAC id). So the file cache holds no queryable public-client account under any
  id — the refresh token is in WAM.
- Building the **same** app id `9cee029c-…` **with** `.WithBroker(BrokerOptions(Windows))` +
  `.WithParentActivityOrWindow(GetConsoleOrTerminalWindow)`: `GetAccountsAsync()` returns
  `remy@automatevalue.com` and **`AcquireTokenSilent` succeeds** (both via the discovered account and
  via `PublicClientApplication.OperatingSystemAccount`) — a real Dataverse `.default` token, no prompt.

So: no app-id change is needed. Flowline builds its `PublicClientApplication` **without** `.WithBroker(...)`,
so it can't see WAM-stored tokens. Adding the broker (Windows-only) recovers them silently.

Note: PAC's `Type: OperatingSystem` is **not** a plain readable field in `authprofiles_v2.json` (that
file has `ProfileType: 4` / `Kind: UNIVERSAL` / `CloudInstance: 0` — the "OperatingSystem" label is
derived). So dispatching purely on a stored "uses WAM" flag is not reliable across PAC versions; prefer
try-broker-then-file with a fallback (see Fix).

Flowline is not misbehaving — it reads the documented PAC token cache and reports a correct, actionable
error. The gap is that its assumption ("PAC keeps a usable user token in `tokencache_msalv3.dat`") no
longer holds once PAC defaults new profiles to OS/WAM storage.

## Workaround (this run)

- Interactive `pac auth create --url <devurl>` (browser login) repopulates the file cache and
  restores Flowline — but it's interactive, so it can't be driven headlessly.
- For read-only verification that only needs *unpacked solution content* (not a Flowline connection),
  route through PAC directly: `pac solution export` + `pac solution unpack`. This is how the
  dotted-classic-assembly fix was live-verified this run without a working Flowline session.

## Fix (designed + probe-verified, not yet implemented)

Add WAM/broker support to Flowline's **user** (non-SPN) auth path:

1. Package: `Microsoft.Identity.Client.Broker` (pin `4.86.1` to match `Identity.Client`), added to
   `Flowline.Core`. Regenerate the committed lock file.
2. `DataverseConnector.AcquireUserTokenAsync` — build two app variants:
   - **Broker app (Windows only)**: `.WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows))`
     + `.WithParentActivityOrWindow(GetConsoleOrTerminalWindow)` (documented `GetConsoleWindow`+`GetAncestor`
     P/Invoke, `[SupportedOSPlatform("windows")]`). Silent flow: `GetAccountsAsync()` → existing
     `SelectCachedAccount` → `AcquireTokenSilent`; if no account, retry with
     `PublicClientApplication.OperatingSystemAccount`.
   - **File-cache app**: today's exact build (unchanged — proven to work for `File`-type profiles).
3. On Windows: try broker silent first (PAC's modern default is OS/WAM), fall back to the file-cache app
   on `MsalUiRequiredException`/broker-unavailable. Non-Windows: file-cache only (broker N/A). Both fail →
   the existing "Session expired… run `pac auth create`" error.
4. Silent-only — never call `AcquireTokenInteractive` (preserve the current UX contract; PAC owns
   interactive login). Memoize the winning path per process so repeat connects in one run don't re-probe.
5. Scope boundaries: **service-principal path untouched** (client secret, no WAM). WAM requires an
   interactive Windows session — CI/headless keeps using SPN, so no regression there.

Rejected alternatives: shelling out to `pac` for a token (fragile coupling to PAC's CLI surface);
message-only tweak (doesn't restore silent auth).

## Implemented + live-verified (2026-07-23)

- `Microsoft.Identity.Client.Broker` 4.86.1 added (`Directory.Packages.props` + `Flowline.Core.csproj`).
- `DataverseConnector.AcquireUserTokenAsync` refactored into `TryAcquireUserTokenViaBrokerAsync`
  (Windows, `.WithBroker` + console window handle, `GetAccountsAsync`→`SelectCachedAccount`→
  `OperatingSystemAccount` fallback, returns null to fall through) and `AcquireUserTokenViaFileCacheAsync`
  (unchanged prior behaviour). Broker-first on Windows, file-cache fallback everywhere. Silent only.
  SPN path untouched.
- Full suite green (1017 Core + 931 CLI, 0 failures) — the 2 previously-failing live-MSAL
  `ConnectToDataverseAsync_*` tests now pass because Flowline can acquire a token silently on a
  WAM-enabled Windows host; on headless CI they fall back to the file cache exactly as before (no
  regression).
- Live: `flowline clone Cr07982 --dev …` connected and exported (`✓ Dev env … exists`, cloned in
  3m20s), and `flowline status` shows `Dev … ✓ remy@automatevalue.com` / `🚀 All environments in
  sync`, exit 0 — no `pac auth create`, no "Session expired".
- Coverage note: the broker/WAM orchestration can't be unit-tested (MSAL public-client types are
  concrete, WAM needs a real OS broker) — same reason the existing `ConnectToDataverseAsync_*` tests
  hit real MSAL. Verified by live run + the throwaway MSAL/WhoAmI probes (since removed).
