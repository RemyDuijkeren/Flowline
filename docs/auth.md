# Authentication

Flowline delegates all auth to PAC CLI. There are no credentials in Flowline config files.

## How it works

When Flowline needs a Dataverse connection, it reuses the credentials PAC CLI already cached
and acquires a token silently — no browser, no password prompt. Flowline stores no credentials
of its own.

Where PAC keeps that token depends on the profile — see the `Type` column in `pac auth list`:

- **`OperatingSystem`** — the Windows account broker (WAM). Default for profiles created with
  recent PAC CLI (2.9+).
- **`File`** — PAC's MSAL token cache at
  `%LOCALAPPDATA%\Microsoft\PowerAppsCLI\tokencache_msalv3.dat`.

On Windows, Flowline asks the account broker first, then falls back to the file cache — so both
profile types work with no extra setup. On macOS and Linux (no broker) it uses the file cache.
Service principal profiles authenticate directly with their client secret.

Profile selection order:
1. A resource-specific profile whose URL matches the target environment
2. A UNIVERSAL profile (the active session from `pac auth create` without `--url`)

## Developer setup

Authenticate once with PAC CLI. Flowline reuses the session automatically.

```
pac auth create --url https://yourorg.crm.dynamics.com
```

To work across multiple environments, create a profile per environment and select as needed:

```
pac auth create --url https://dev.crm.dynamics.com
pac auth create --url https://test.crm.dynamics.com
pac auth list
pac auth select --index 2
```

Sessions are cached and refreshed automatically. Re-authenticate when the token expires
(typically after a password change or 90 days of inactivity):

```
pac auth create --url https://yourorg.crm.dynamics.com
```

## CI/CD pipelines

PAC CLI is a hard requirement — Flowline will not run without it. Add it to your container
image and authenticate with a service principal before running any Flowline commands:

```yaml
# GitHub Actions / Azure Pipelines
- run: pac auth create --kind ServicePrincipal --applicationId $CLIENT_ID --clientSecret $CLIENT_SECRET --tenant $TENANT_ID
- run: flowline deploy --prod https://yourorg.crm.dynamics.com
```

Store `CLIENT_ID`, `CLIENT_SECRET`, and `TENANT_ID` as pipeline secret variables. Never
commit credentials to the repo or the `.flowline` config file.