# Flowline

![CI](https://github.com/RemyDuijkeren/Flowline/workflows/CI/badge.svg)
[![NuGet](https://img.shields.io/nuget/v/Flowline.svg)](https://www.nuget.org/packages/Flowline)
[![NuGet](https://img.shields.io/nuget/dt/RemyDuijkeren.Flowline.svg)](https://www.nuget.org/packages/Flowline)

**Flowline** is a CLI tool for delivering Dataverse solutions with unmanaged packages, Git as the source of truth, and a straightforward DEV → STAGING → PROD workflow.

---

## Why Flowline?

**Power Platform Pipelines only support managed solutions.** Flowline exists for teams that choose unmanaged — keeping full control over every environment without locked-down layers or forced upgrade paths.

Beyond the unmanaged-vs-managed difference, Flowline brings a few things the other tools don't:

- **Git is the source of truth.** `clone` bootstraps an existing solution into the repo. `sync` pulls the current state from DEV back into source control. `deploy` packages from the repo and imports into the target — not environment-to-environment.
- **Separate inner loop for code assets.** `push` syncs your plugin assemblies and web resources directly to DEV without a full solution import. Use it from a full Flowline project, or point it at a standalone DLL and web resource folder. No Plugin Registration Tool, no manual upload, no waiting for a heavy import cycle to iterate on code.
- **Attribute-driven plugin registration.** Decorate your `IPlugin` classes with `[Step]`, `[Filter]`, `[PreImage]`, and `[PostImage]` — Flowline reads the compiled assembly and handles all Dataverse registrations. No base class required, no XML config file.
- **Plugins, workflow activities, and Custom APIs in one assembly.** Other tools require separate DLLs for each type — syncing them in sequence causes one pass to delete the registrations from the previous one. Flowline reads all types from a single assembly in one pass, so everything can live in one `Extensions` project.
- **Modern auth.** Flowline reuses the PAC CLI token cache. No passwords, no client secrets in scripts, no Windows Credential Manager.
- **No unnecessary ceremony.** Designed for small teams, not large implementation partners.

---

## Install

```bash
dotnet tool install --global Flowline
```

**Prerequisites**

```bash
winget install Microsoft.PowerAppsCLI
winget install Git.Git
```

Authenticate with PAC CLI before using Flowline:

```bash
pac auth create --environment https://your-org.crm4.dynamics.com
```

---

## Commands

| Command | What it does |
|---|---|
| `clone <solution>` | Bootstrap an existing solution from production into the repo. Sets up the full project structure: unpacked solution, Extensions (plugins) project, WebResources project. |
| `push [solution]` | Build and sync local plugin assemblies and web resources to the DEV environment, or push standalone artifacts with `--dll` / `--webresources`. Fast inner loop — no solution import needed. |
| `sync [solution]` | Pull the current solution state from DEV, unpack it into the repo. Run `git commit` afterwards to save the checkpoint. |
| `deploy <target>` | Pack the solution from the repo and import it into a target environment. Target can be `prod`, `staging`, or an explicit URL. |
| `provision [dev\|staging]` | Provision a DEV or STAGING environment by copying from production. |
| `status` | Show environment info, Flowline version, and PAC CLI status. |

---

## Typical workflow

```bash
# One-time: bring an existing solution into the repo
flowline clone ContosoCustomizations --prod https://contoso.crm4.dynamics.com

# Daily dev loop
flowline push                          # sync plugin DLL + web resources to DEV
flowline sync                          # pull solution state from DEV back into repo
git commit -m "feat: add validation"   # commit the checkpoint

# Promote
flowline deploy staging                # import into STAGING from repo
flowline deploy prod                   # import into PROD from repo
```

For fresh environments:

```bash
flowline provision dev --prod https://contoso.crm4.dynamics.com
flowline provision staging --prod https://contoso.crm4.dynamics.com
```

---

## Push Modes

`flowline push` has two modes. Use one or the other.

### Project mode

Project mode is the default after `flowline clone`. It uses `.flowline` for the DEV URL and solution name, builds the conventional projects, and pushes their output:

```bash
flowline push
flowline push ContosoCustomizations --scope plugins
flowline push ContosoCustomizations --scope webresources
```

Expected structure:

```text
solutions/<solution>/Extensions/
solutions/<solution>/WebResources/
.flowline
```

### Standalone mode

Standalone mode is for developers who only want Flowline's direct Dataverse push. It does **not** require a Flowline project, `.flowline`, Git repo, `solutions/` folder, `Extensions` project, or `WebResources` project.

Run it from a normal folder that is **not** a Flowline project folder:

```bash
flowline push ContosoCustomizations --dll ./bin/Release/MyPlugins.dll --dev https://contoso-dev.crm4.dynamics.com
flowline push ContosoCustomizations --webresources ./dist --dev https://contoso-dev.crm4.dynamics.com
flowline push ContosoCustomizations --dll ./bin/Release/MyPlugins.dll --webresources ./dist --dev https://contoso-dev.crm4.dynamics.com
```

If `--dev` is omitted, Flowline uses the current resource-specific PAC auth profile.

Standalone rules:

- `--dll` must point to an already-built plugin assembly.
- `--webresources` points directly at the folder whose files should be synced.
- `--scope` is not allowed with `--dll` or `--webresources`.
- `.flowline` is not read in standalone mode.
- If a `.flowline` file exists in the current folder, standalone mode stops because that usually means the user mixed project mode and standalone mode.
- The target solution must be unmanaged.

---

## Plugin and Custom API registration

Add the `Flowline.Attributes` NuGet package to your Extensions project:

```xml
<PackageReference Include="Flowline.Attributes" Version="1.0.0" PrivateAssets="all" />
```

Decorate your plugin classes — no base class needed:

```csharp
[Step("account")]
[Filter("name", "creditlimit")]
[PreImage("name", "creditlimit")]
public class AccountPreUpdatePlugin : IPlugin
{
    public void Execute(IServiceProvider sp) { ... }
}
```

`flowline push` reads the compiled assembly and creates or updates all plugin types, steps, images, and Custom API registrations in Dataverse automatically.

Full attribute reference: [Flowline.Attributes README](src/Flowline.Attributes/README.md)

---

## Project structure after `clone`

```
solutions/
  ContosoCustomizations/
    SolutionPackage/          # unpacked solution (PAC cdsproj)
      SolutionPackage.cdsproj
      Mapping.xml
    Extensions/               # plugin assembly project
      Extensions.csproj
    WebResources/             # web resource files
      dist/                   # files here are synced to Dataverse by `push`
    ContosoCustomizations.sln
.flowline                     # environment URLs and solution config
```

Web resources placed under `WebResources/dist/` are synced to Dataverse by `flowline push`. Files named `*_nosync.*` are excluded.

---

## Development

```bash
dotnet pack
dotnet tool uninstall -g Flowline
dotnet tool install -g Flowline --add-source ./artifacts/nupkg --prerelease
```
