# Flowline

![CI](https://github.com/RemyDuijkeren/Flowline/workflows/CI/badge.svg)
[![NuGet](https://img.shields.io/nuget/v/Flowline.svg)](https://www.nuget.org/packages/Flowline)
[![NuGet](https://img.shields.io/nuget/dt/RemyDuijkeren.Flowline.svg)](https://www.nuget.org/packages/Flowline)

**Flowline** is a CLI tool for delivering Dataverse solutions with unmanaged packages, Git as the source of truth, and a straightforward DEV to STAGING to PROD workflow.

---

## Why Flowline?

**Power Platform Pipelines only support managed solutions.** Flowline exists for teams that choose unmanaged, keeping full control over every environment without locked-down layers or forced upgrade paths.

Beyond the unmanaged-vs-managed difference, Flowline brings a few things the other tools don't:

- **Git is the source of truth.** `clone` bootstraps an existing solution into the repo. `sync` pulls the current state from DEV back into source control. `deploy` packages from the repo and imports into the target.
- **Fast push for code assets.** `push` syncs plugin assemblies and web resources directly to DEV without a full solution import. Use it from a Flowline project, or point it at a standalone DLL and web resource folder.
- **Attribute-driven plugin registration.** Decorate `IPlugin` classes with `[Step]`, `[Filter]`, `[PreImage]`, and `[PostImage]`; Flowline reads the compiled assembly and handles the Dataverse registrations.
- **Plugins, workflow activities, and Custom APIs in one assembly.** Flowline reads all supported types from a single assembly in one pass.
- **Modern auth.** Flowline reuses the PAC CLI token cache. No passwords, no client secrets in scripts, no Windows Credential Manager.

---

## Install

```bash
dotnet tool install --global Flowline
```

Prerequisites:

```bash
winget install Microsoft.PowerAppsCLI
winget install Git.Git
```

Authenticate with PAC CLI before using Flowline:

```bash
pac auth create --environment https://your-org.crm4.dynamics.com
```

---

## Usage Modes

Flowline can be used in two ways.

### Full Project Workflow

Use this when Flowline owns the local solution structure. `clone` creates `.flowline`, the unpacked solution, an `Extensions` project, and a `WebResources` project.

```bash
# Create a Git repo for the Flowline project
mkdir contoso-flowline
cd contoso-flowline
git init

# One-time: bring an existing solution into the repo
flowline clone ContosoCustomizations --prod https://contoso.crm4.dynamics.com

# Daily dev loop
flowline push
flowline sync
git commit -m "feat: add validation"

# Promote
flowline deploy staging
flowline deploy prod
```

For fresh environments:

```bash
flowline provision dev --prod https://contoso.crm4.dynamics.com
flowline provision staging --prod https://contoso.crm4.dynamics.com
```

Project mode expects this structure:

```text
solutions/
  ContosoCustomizations/
    SolutionPackage/          # unpacked solution (PAC cdsproj)
      SolutionPackage.cdsproj
      Mapping.xml
    Extensions/               # plugin assembly project
      Extensions.csproj
    WebResources/             # web resource files
      dist/                   # files here are synced to Dataverse by push
    ContosoCustomizations.sln
.flowline                     # environment URLs and solution config
```

Files under `WebResources/dist/` are synced to Dataverse by `flowline push`. Files named `*_nosync.*` are excluded.

### Standalone Push

Use this when you only want Flowline's direct Dataverse push. You do **not** need a Flowline project, `.flowline`, Git repo, `solutions/` folder, `Extensions` project, or `WebResources` project.

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
- If `.flowline` exists in the current folder, standalone mode stops because that usually means project mode and standalone mode were mixed.
- The target solution must be unmanaged.

---

## Plugin And Custom API Registration

Add the `Flowline.Attributes` NuGet package to your Extensions project:

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

---

## Commands

| Command | What it does |
|---|---|
| `clone <solution>` | Bootstrap an existing solution from production into the repo. Sets up the full project structure. |
| `push [solution]` | Build and sync project assets to DEV, or push standalone artifacts with `--dll` / `--webresources`. |
| `sync [solution]` | Pull the current solution state from DEV and unpack it into the repo. |
| `deploy <target>` | Pack the solution from the repo and import it into STAGING, PROD, or an explicit URL. |
| `provision [dev\|staging]` | Provision a DEV or STAGING environment by copying from production. |
| `status` | Show environment info, Flowline version, and PAC CLI status. |
