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

**PAC CLI gives you the primitives — Flowline gives you the workflow.**

Attribute-driven plugin registration that covers Custom APIs, a full Git-based ALM loop from dev to prod, and automatic web resource dependency registration — in one tool. Where PAC CLI already handles it, Flowline wraps — it doesn't re-implement. Unlike Power Platform Pipelines, Flowline requires neither Managed Environments nor managed solutions.

Familiar with [spkl](https://github.com/scottdurow/SparkleXrm/wiki/spkl)? Flowline is its actively maintained successor (last meaningful commit 2021).

`flowline push` goes beyond file sync:

- **Plugins registered from attributes** — `[Step]`, `[Filter]`, `[CustomApi]` with sensible defaults; less boilerplate than `[CrmPluginRegistration]`, no Plugin Registration Tool
- **Web resources pushed from folder structure** — no `spkl.json` mapping; orphaned Dataverse records deleted or removed automatically on every push
- **Web resource dependencies auto-wired** — RESX files linked to parent JS by base name; `// flowline:depends` for JS-to-JS; registered on every push, no Maker Portal visit needed
- **Full Git-based ALM** — `clone → push → sync → deploy` in one tool, unmanaged or managed

> Pipelines are buried steel — permits, compressors, years to commission. A flowline goes where the pipeline can't.

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

For full setup, auth, and project workflow: **[Getting Started](https://github.com/RemyDuijkeren/Flowline/wiki/01-Getting-Started)**

---

## Commands

| Command | What it does |
|---|---|
| [`clone <solution>`](https://github.com/RemyDuijkeren/Flowline/wiki/03-Command-Reference#clone) | Bootstrap an existing solution from Dataverse into the repo |
| [`push [solution]`](https://github.com/RemyDuijkeren/Flowline/wiki/03-Command-Reference#push) | Build and sync code assets to DEV; or push standalone with `--pluginFile` / `--webresources` |
| [`sync [solution]`](https://github.com/RemyDuijkeren/Flowline/wiki/03-Command-Reference#sync) | Pull the current solution state from DEV into source control |
| [`deploy <target>`](https://github.com/RemyDuijkeren/Flowline/wiki/03-Command-Reference#deploy) | Pack from the repo and import into `test`, `uat`, `prod`, or a URL |
| [`provision [dev\|test]`](https://github.com/RemyDuijkeren/Flowline/wiki/03-Command-Reference#provision) | Provision a DEV or TEST environment by copying from production |
| [`generate [solution]`](https://github.com/RemyDuijkeren/Flowline/wiki/05-Generate-Early-Bound-Types) | Generate early-bound C# types into `Plugins/Models/` |
| [`status`](https://github.com/RemyDuijkeren/Flowline/wiki/03-Command-Reference#status) | Show environment info, Flowline version, and PAC CLI status |

**Plugin attributes NuGet:** [Flowline.Attributes](src/Flowline.Attributes/README.md) — add to your plugin project to use `[Step]`, `[Filter]`, `[CustomApi]`, and friends — full reference: [Plugin Registration](https://github.com/RemyDuijkeren/Flowline/wiki/04-Plugin-Registration)

See the [Wiki for the full documentation.](https://github.com/RemyDuijkeren/Flowline/wiki)

Coming from another tool? [Migration from spkl](https://github.com/RemyDuijkeren/Flowline/wiki/08-Migration-from-spkl) · [Migration from Daxif](https://github.com/RemyDuijkeren/Flowline/wiki/09-Migration-from-Daxif) · [Migration from PACX](https://github.com/RemyDuijkeren/Flowline/wiki/10-Migration-from-PACX)
