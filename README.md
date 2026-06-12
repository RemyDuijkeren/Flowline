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

</td>
<td width="120" align="right" valign="top">

![Flowline CLI](https://raw.githubusercontent.com/RemyDuijkeren/Flowline/master/docs/Flowline-icon.png)

</td>
</tr>
</table>

---

**PAC CLI gives you the primitives — Flowline gives you the workflow.** Clone, push, sync, and deploy are a defined loop
with attribute-driven plugin registration and a direct push that skips the pack/import/register cycle.
Where PAC CLI already handles it, Flowline wraps — it doesn't re-implement.
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

Prerequisites: [.NET SDK](https://dot.net) (8 or later), [Git](https://git-scm.com), and PAC CLI:

```bash
dotnet tool install --global Microsoft.PowerApps.CLI.Tool
```

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

For full setup, auth, and project workflow: **[Getting Started](https://github.com/RemyDuijkeren/Flowline/wiki/Getting-Started)**

---

## Commands

| Command | What it does |
|---|---|
| [`clone <solution>`](https://github.com/RemyDuijkeren/Flowline/wiki/Command-Reference#clone) | Bootstrap an existing solution from Dataverse into the repo |
| [`push [solution]`](https://github.com/RemyDuijkeren/Flowline/wiki/Command-Reference#push) | Build and sync code assets to DEV; or push standalone with `--pluginFile` / `--webresources` |
| [`sync [solution]`](https://github.com/RemyDuijkeren/Flowline/wiki/Command-Reference#sync) | Pull the current solution state from DEV into source control |
| [`deploy <target>`](https://github.com/RemyDuijkeren/Flowline/wiki/Command-Reference#deploy) | Pack from the repo and import into `test`, `uat`, `prod`, or a URL |
| [`provision [dev\|test]`](https://github.com/RemyDuijkeren/Flowline/wiki/Command-Reference#provision) | Provision a DEV or TEST environment by copying from production |
| [`generate [solution]`](https://github.com/RemyDuijkeren/Flowline/wiki/Command-Reference#generate) | Generate early-bound C# types into `Plugins/Models/` |
| [`status`](https://github.com/RemyDuijkeren/Flowline/wiki/Command-Reference#status) | Show environment info, Flowline version, and PAC CLI status |

**Plugin attributes NuGet:** [Flowline.Attributes](src/Flowline.Attributes/README.md) — add to your plugin project to use `[Step]`, `[Filter]`, `[CustomApi]`, and friends — full reference: [Plugin Registration](https://github.com/RemyDuijkeren/Flowline/wiki/Plugin-Registration)

See the [Wiki for the full documentation.](https://github.com/RemyDuijkeren/Flowline/wiki)
