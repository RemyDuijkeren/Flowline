# Flowline

<table>
<tr>
<td>

**Flowline** is a Dataverse ALM CLI — structured workflow, Git-tracked solutions,
and a fast push to DEV without the enterprise overhead.

[![CI](https://github.com/RemyDuijkeren/Flowline/workflows/CI/badge.svg)](https://github.com/RemyDuijkeren/Flowline/workflows/CI/badge.svg)
[![NuGet](https://img.shields.io/nuget/v/Flowline.svg)](https://www.nuget.org/packages/Flowline)
[![NuGet](https://img.shields.io/nuget/dt/RemyDuijkeren.Flowline.svg)](https://www.nuget.org/packages/Flowline)

</td>
<td width="120" align="right" valign="top">

![Flowline CLI](https://raw.githubusercontent.com/RemyDuijkeren/Flowline/master/docs/Flowline-icon.png)

</td>
</tr>
</table>

---

## Why Flowline?

**PAC CLI gives you the primitives — Flowline gives you the workflow.** Export, unpack, pack, and import are building
blocks; Flowline turns them into a defined project structure with a clone → push → sync → deploy loop,
plus attribute-driven plugin registration and a direct push that skips the pack/import/register cycle.
Unlike Power Platform Pipelines — which require Managed Environments and managed solutions — Flowline requires neither.

> Pipelines are buried steel — permits, compressors, years to commission. A flowline goes where the pipeline can't.

Flowline is the opinionated successor to [spkl](https://github.com/scottdurow/SparkleXrm/wiki/spkl) — the same attribute-driven plugin registration that now
supports Custom APIs, push web resources without `spkl.json` mapping, a full Git-based ALM workflow, and modern PAC auth.

For small teams that want simple ALM with Git. `clone` bootstraps an existing solution into the repo and
unpacks it per component so `git diff` shows real changes — not a binary blob. `sync` captures DEV's state into
source control; `deploy` packages from the repo and imports into the target.

What sets Flowline apart:

- **Scaffolded WebResources project.** `clone` creates a web resources project with Rollup + TypeScript already set up — `dist/` is automatically wired to `push`. Swap in any bundler you prefer.
- **Fast push for code assets.** `push` syncs plugin assemblies and web resources directly to DEV without a full solution import. Use it from a Flowline project, or point it at a standalone plugin file and web resource folder. `--scope assemblyonly` updates only the assembly bytes — useful in hot iteration loops when registrations haven't changed.
- **Keeps Dataverse in sync with source.** Plugin steps, images, and web resources missing from source are deleted or removed (if exist in another solution) on push — no manual cleanup. Use `--no-delete` to skip.
- **Dry-run everything.** `--dry-run` previews every change before it touches Dataverse.
- **Human-readable sync summary.** After `sync`, Flowline translates the git diff into a developer-friendly summary — entities and components added, changed, or removed. No raw XML noise.
- **Attribute-driven plugin registration.** Decorate `IPlugin` classes with `[Step]`, `[Filter]`, `[PreImage]`, `[PostImage]`, and `[CustomApi]`; Flowline reads the compiled assembly and handles Dataverse registrations.
- **Plugins, workflow activities, and Custom APIs in one assembly.** Flowline reads all supported types from a single assembly in one pass.
- **One-command environment provisioning.** `provision` copies PROD to a fresh DEV or TEST environment — no manual admin center clicks.
- **Modern auth.** Flowline reuses the PAC CLI token cache. No passwords, no client secrets in scripts, no Windows Credential Manager.

---

## Install

```bash
dotnet tool install --global Flowline
```

Prerequisites (PAC CLI and Git):

```bash
winget install Microsoft.PowerAppsCLI
winget install Git.Git
```

Authenticate with PAC CLI before using Flowline:

```bash
pac auth create --environment https://your-org.crm4.dynamics.com
```

Authenticating in CI/CD pipelines:

```yaml
- run: pac auth create --kind ServicePrincipal --applicationId $CLIENT_ID --clientSecret $CLIENT_SECRET --tenant $TENANT_ID
- run: flowline deploy prod
```

---

## Full Project Workflow

Use this when Flowline owns the local solution structure. `clone` creates `.flowline`, the unpacked solution, a `Plugins` project, and a `WebResources` project.

```bash
# Create a Git repo for the Flowline project
mkdir contoso-flowline
cd contoso-flowline
git init

# One-time: bring an existing solution into the repo
flowline clone ContosoSales --prod https://contoso.crm4.dynamics.com

# Daily dev loop
flowline push
flowline sync
git commit -m "feat: add validation"

# Promote
flowline deploy test
flowline deploy prod
```

Project mode expects this structure:

```text
.flowline                                 # environment URLs and solution config
└── solutions/
    └── ContosoSales/
        ├── ContosoSales.sln
        ├── Package/                      # PAC-managed — do not edit manually
        │   ├── Package.cdsproj
        │   └── src/                      # unpacked solution XML (pac clone / sync)
        ├── Plugins/                      # plugin assembly project
        │   ├── Plugins.csproj
        │   └── Models/                   # early-bound C# types — generated by flowline generate
        ├── WebResources/                 # web resource files
        │   └── dist/                     # files here are synced to Dataverse by push
        └── artifacts/                    # deployment zips (generated by clone, sync, deploy)
```

Files under `WebResources/dist/` are synced to Dataverse by `flowline push`. Files named `*_nosync.*` are excluded.

---

## Commands

| Command | What it does |
|---|---|
| [`clone <solution>`](#clone) | Bootstrap an existing solution from production into the repo. Sets up the full project structure. |
| [`push [solution]`](#push) | Build and sync project assets to DEV, or push standalone artifacts with `--pluginFile` / `--webresources`. |
| [`sync [solution]`](#sync) | Pull the current solution state from DEV and unpack it into the repo. |
| [`deploy <target>`](#deploy) | Pack the solution from the repo and import it into TEST, PROD, or an explicit URL. |
| [`provision [dev\|test]`](#provision) | Provision a DEV or TEST environment by copying from production. |
| [`generate [solution]`](#generate) | Generate early-bound C# types for the solution's entities and custom APIs into `Plugins/Models/`. Works standalone outside a Flowline project with `-o <PATH>`. |
| [`status`](#status) | Show environment info, Flowline version, and PAC CLI status. |

---

## clone

Bootstraps an existing Dataverse solution into a new Git repo. Creates `.flowline`, unpacks the solution XML into `src/`, and scaffolds the `Plugins` and `WebResources` projects.

```bash
flowline clone ContosoSales --prod https://contoso.crm4.dynamics.com
```

| Argument / Option | Description |
|---|---|
| `<solution>` | Solution to clone into this repo |
| `--prod <url>` | Production environment URL to clone solution from |
| `--test <url>` | Test environment URL to clone solution from |
| `--dev <url>` | Development environment URL to clone solution from |
| `--managed` | Include managed artifacts |
| `-v`, `--verbose` | Show command details |
| `-f`, `--force` | Skip confirmation prompts |
| `--no-cache` | Refresh validation checks instead of using the local validation cache |

---

## push

In project mode, builds the solution and syncs plugin assemblies and web resources to DEV. Files under `WebResources/dist/` are synced; files named `*_nosync.*` are excluded.

```bash
flowline push
```

| Argument / Option | Description |
|---|---|
| `[solution]` | Solution to push (optional in project mode) |
| `-s`, `--scope <scope>` | Limit the push scope: `all`, `webresources`, `plugins`, or `assemblyonly`. Can be specified multiple times. |
| `-pf`, `--pluginFile <path>` | Prebuilt plugin assembly (`.dll`) to push without using a Flowline project |
| `-wr`, `--webresources <path>` | Web resource folder to push without using a Flowline project |
| `--dev <url>` | Dev environment URL |
| `--no-delete` | Push without deleting Dataverse assets that are missing from source |
| `--dry-run` | Preview changes without touching Dataverse |
| `-v`, `--verbose` | Show command details |
| `-f`, `--force` | Skip confirmation prompts |
| `--no-cache` | Refresh validation checks instead of using the local validation cache |

Use `--scope assemblyonly` to update only the assembly bytes without touching step or Custom API registrations — useful in hot iteration loops when registrations haven't changed:

```bash
flowline push --scope assemblyonly
```

### Standalone

Use this when you only want Flowline's direct Dataverse push. You do **not** need a Flowline project, `.flowline`, Git repo, `solutions/` folder, `Plugins` project, or `WebResources` project.

Run it from a normal folder that is **not** a Flowline project folder:

```bash
flowline push ContosoSales --pluginFile ./bin/Release/MyPlugins.dll --dev https://contoso-dev.crm4.dynamics.com
flowline push ContosoSales --webresources ./dist --dev https://contoso-dev.crm4.dynamics.com
flowline push ContosoSales --pluginFile ./bin/Release/MyPlugins.dll --webresources ./dist --dev https://contoso-dev.crm4.dynamics.com
```

To update only the assembly bytes without touching step or Custom API registrations:

```bash
flowline push ContosoSales --pluginFile ./bin/Release/MyPlugins.dll --scope assemblyonly --dev https://contoso-dev.crm4.dynamics.com
```

If `--dev` is omitted, Flowline uses the current resource-specific PAC auth profile.

Standalone rules:

- `--pluginFile` must point to an already-built plugin assembly (`.dll`).
- `--webresources` points directly at the folder whose files should be synced.
- `--scope` is optional. Without it, scope is derived from the provided options. With it, the given scope must match what was provided — `--scope plugins` or `--scope assemblyonly` requires `--pluginFile`; `--scope webresources` requires `--webresources`.
- `.flowline` is not read in standalone mode.
- If `.flowline` exists in the current folder, standalone mode stops because that usually means project mode and standalone mode were mixed.
- The target solution must be unmanaged.

### Plugin and Custom API Registration

Add the `Flowline.Attributes` NuGet package to your Plugins project:

```xml
<PackageReference Include="Flowline.Attributes" Version="1.0.0" PrivateAssets="all" />
```

Decorate your plugin classes. No base class is needed:

```csharp
[Step("account")]
[Filter("name", "creditlimit")]
[PreImage("name", "creditlimit")]
public class AccountPreUpdatePlugin : IPlugin
{
    public void Execute(IServiceProvider sp) { ... }
}
```

`flowline push` reads the compiled assembly and creates or updates plugin types, steps, images, and Custom API registrations in Dataverse.

Full attribute reference: [Flowline.Attributes README](src/Flowline.Attributes/README.md)
Nugget package: [Flowline.Attributes](https://www.nuget.org/packages/Flowline.Attributes)

---

## sync

Pulls the current solution state from DEV and unpacks it into the repo. Run this after making changes in the Dataverse maker portal to capture them in source control.

```bash
flowline sync
```

| Argument / Option | Description |
|---|---|
| `[solution]` | Solution to sync (optional in project mode) |
| `--dev <url>` | Development environment URL |
| `--managed` | Include managed artifacts |
| `--bump <component>` | Version component to increment: `patch`, `minor`, or `major` (default: `patch`) |
| `-v`, `--verbose` | Show command details |
| `-f`, `--force` | Skip confirmation prompts |
| `--no-cache` | Refresh validation checks instead of using the local validation cache |

---

## deploy

Packs the solution from the repo and imports it into the target environment.

```bash
flowline deploy test
flowline deploy prod
flowline deploy https://contoso-uat.crm4.dynamics.com
```

| Argument / Option | Description |
|---|---|
| `<target>` | Target environment: `prod`, `test`, or a URL |
| `--solution <name>` | Solution to deploy |
| `--managed` | Deploy the managed package |
| `-v`, `--verbose` | Show command details |
| `-f`, `--force` | Skip confirmation prompts |
| `--no-cache` | Refresh validation checks instead of using the local validation cache |

---

## provision

Provisions a DEV or TEST environment by copying from production. Use this once when setting up a new environment.

```bash
flowline provision dev --prod https://contoso.crm4.dynamics.com
flowline provision test --prod https://contoso.crm4.dynamics.com
```

| Argument / Option | Description |
|---|---|
| `[role]` | Target role: `dev` or `test` (default: `dev`) |
| `--prod <url>` | Production environment URL to copy from |
| `--copy <type>` | Copy type: `minimal` (no data) or `full` (with data) (default: `minimal` for dev, `full` for test) |
| `--suffix <text>` | Target URL suffix (default: role name) |
| `--allow-overwrite` | Overwrite an existing target environment |
| `-v`, `--verbose` | Show command details |
| `-f`, `--force` | Skip confirmation prompts |
| `--no-cache` | Refresh validation checks instead of using the local validation cache |

---

## generate

`flowline generate` wraps `pac modelbuilder build` with opinionated defaults.

| Argument / Option | Description |
|---|---|
| `[solution]` | Solution to generate types for (optional in project mode) |
| `--namespace <ns>` | Model namespace — saved to `.flowline` for future runs |
| `--extra-tables <tables>` | Comma-separated extra tables to include; replaces the saved list |
| `--dev <url>` | Dev environment URL |
| `-o`, `--output <path>` | Output folder for generated types (required outside a Flowline project) |
| `-v`, `--verbose` | Show command details |
| `-f`, `--force` | Skip confirmation prompts |
| `--no-cache` | Refresh validation checks instead of using the local validation cache | It auto-discovers the solution's entities and custom APIs from DEV, generates early-bound C# types into `Plugins/Models/`, and saves the namespace to `.flowline`.

```bash
# First run — namespace derived from Plugins.csproj and saved to .flowline
flowline generate

# Subsequent runs pick up namespace and extra tables from .flowline automatically
flowline generate

# Override or set namespace permanently
flowline generate --namespace Contoso.Plugins.Models

# Include extra tables not in the solution
flowline generate --extra-tables account,contact
```

Generated types land in `Plugins/Models/` alongside your plugin code — no separate assembly, no ILMerge. Running `generate` again fully replaces the folder, so stale files from removed entities are cleaned up automatically.

Requires an active `pac auth create` session against the DEV environment. The namespace is derived in order from `<RootNamespace>`, then `<PackageId>` (set by `pac plugin init --name`), then the csproj filename, then `<SolutionName>.Models`. Once derived, it is saved to `.flowline` and reused on every subsequent run.

**Standalone mode:** Run `flowline generate` outside a Flowline project by supplying `-o` (output path), `--dev`, and the solution name. Useful for CI pipelines or repos that don't use the full Flowline structure.

```bash
flowline generate ContosoSales \
  --dev https://contoso.crm4.dynamics.com \
  --namespace Contoso.Plugins.Models \
  -o ./src/Models
```

Outside a Flowline project, `--dev` and `-o` are required. `--namespace` is optional — defaults to `<SolutionName>.Models`. Nothing is saved to `.flowline`.

---

## status

Shows environment info, Flowline version, and PAC CLI authentication status.

```bash
flowline status
```

| Option | Description |
|---|---|
| `-v`, `--verbose` | Show command details |
| `-f`, `--force` | Skip confirmation prompts |
| `--no-cache` | Refresh validation checks instead of using the local validation cache |
