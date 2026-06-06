# Flowline

<table>
<tr>
<td>

**Flowline** is a Dataverse ALM CLI — structured workflow, Git-tracked solutions,
and a fast push to DEV without the enterprise overhead.

[![Docs](https://img.shields.io/badge/docs-wiki-blue?logo=readthedocs&logoColor=white)](https://github.com/RemyDuijkeren/Flowline/wiki)
[![CI](https://github.com/RemyDuijkeren/Flowline/workflows/CI/badge.svg)](https://github.com/RemyDuijkeren/Flowline/workflows/CI/badge.svg)
[![NuGet](https://img.shields.io/nuget/v/Flowline.svg)](https://www.nuget.org/packages/Flowline)
[![NuGet downloads](https://img.shields.io/nuget/dt/Flowline.svg)](https://www.nuget.org/packages/Flowline)
[![GitHub last commit](https://img.shields.io/github/last-commit/RemyDuijkeren/Flowline)](https://github.com/RemyDuijkeren/Flowline/commits/master)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/RemyDuijkeren?label=sponsor&logo=githubsponsors&color=EA4AAA)](https://github.com/sponsors/RemyDuijkeren)

</td>
<td width="120" align="right" valign="top">

![Flowline CLI](https://raw.githubusercontent.com/RemyDuijkeren/Flowline/master/docs/Flowline-icon.png)

</td>
</tr>
</table>

---

**PAC CLI gives you the primitives — Flowline gives you the workflow.** Clone, push, sync, and deploy are a defined loop
with attribute-driven plugin registration and a direct push that skips the pack/import/register cycle.
Unlike Power Platform Pipelines, Flowline requires neither Managed Environments nor managed solutions.

> Pipelines are buried steel — permits, compressors, years to commission. A flowline goes where the pipeline can't.

Flowline is the opinionated successor to [spkl](https://github.com/scottdurow/SparkleXrm/wiki/spkl) — the same
attribute-driven plugin registration that now supports Custom APIs, push web resources without `spkl.json` mapping,
a full Git-based ALM workflow, and modern PAC auth.

---

## Install

```bash
dotnet tool install --global Flowline
```

Prerequisites:

```bash
dotnet tool install --global Microsoft.PowerApps.CLI.Tool
winget install Git.Git
```

On Windows, winget is an alternative for PAC CLI: `winget install Microsoft.PowerAppsCLI`
If .NET 10 is installed, Flowline uses dnx — no separate PAC CLI install needed.

Authenticate before using Flowline:

```bash
pac auth create --environment https://your-org.crm4.dynamics.com
```

---

## Quick start

```bash
# Bootstrap an existing solution into the repo
flowline clone ContosoSales --prod https://contoso.crm4.dynamics.com

# Daily dev loop
flowline push
flowline sync
git commit -m "feat: add validation"

# Promote
flowline deploy test
flowline deploy prod
```

---

## Commands

| Command | What it does |
|---|---|
| `clone <solution>` | Bootstrap an existing solution from Dataverse into the repo |
| `push [solution]` | Build and sync code assets to DEV; or push standalone with `--pluginFile` / `--webresources` |
| `sync [solution]` | Pull the current solution state from DEV into source control |
| `deploy <target>` | Pack from the repo and import into `test`, `uat`, `prod`, or a URL |
| `provision [dev\|test]` | Provision a DEV or TEST environment by copying from production |
| `generate [solution]` | Generate early-bound C# types into `Plugins/Models/` |
| `status` | Show environment info, Flowline version, and PAC CLI status |

**Full documentation:** [github.com/RemyDuijkeren/Flowline/wiki](https://github.com/RemyDuijkeren/Flowline/wiki)
