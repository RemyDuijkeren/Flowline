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

A professional Git-based workflow for plugin developers and solution architects, without the overhead of Power Platform Pipelines or managed solutions. `clone → push → sync → deploy`, from the inner dev loop to production, in one tool.

Where PAC CLI already handles it, Flowline wraps — it doesn't re-implement. Where PAC has no answer, Flowline fills the gap.

Familiar with [spkl](https://github.com/scottdurow/SparkleXrm/wiki/spkl)? Flowline is its actively maintained successor (last meaningful commit 2021).

What sets Flowline apart:

- **Attribute-driven plugin registration** — decorate your `IPlugin` classes with `[Step]`, `[Filter]`, `[CustomApi]`; Flowline reads the assembly and handles every Dataverse registration. No Plugin Registration Tool, no `spkl.json`, no boilerplate.
- **Web resource dependencies auto-wired** — RESX files linked to parent JS by base name; `// flowline:depends` for JS-to-JS; registered on every `push`. No Maker Portal visits, no manual dependency trees.
- **Orphan cleanup built in** — steps, step images, and web resources missing from source are deleted from Dataverse on every `push`. `deploy` cleans up removed solution components too. No stale registrations, no ghost records. Use `--no-delete` to opt out.
- **Dry-run before you touch anything** — `--dry-run` shows exactly what would change before a single Dataverse record is touched. Run it as a CI safety gate or any time you want confidence. No other Dataverse ALM tool offers this.
- **AI-native schema context** — `sync` writes `DATAVERSE_CONTEXT.md` with your full schema (entities, attributes, option sets, forms, views, plugin steps); Claude Code, Copilot, and Codex load it automatically via `AGENTS.md`. Your AI assistant knows your field names without live queries.

> Pipelines are buried steel — permits, compressors, years to commission. A flowline goes where the pipeline can't.

---

## Install

```bash
dotnet tool install --global Flowline
```

Prerequisites: [.NET SDK](https://dot.net) 10 or later, [Git](https://git-scm.com), and [PAC CLI](https://learn.microsoft.com/en-us/power-platform/developer/cli/introduction):

Authenticate using PAC CLI before using Flowline:

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

| Command                                                                                                       | What it does                                                                                 |
|---------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------|
| [`clone <solution>`](https://github.com/RemyDuijkeren/Flowline/wiki/03-Command-Reference#clone)               | Bootstrap an existing solution from Dataverse into the repo                                  |
| [`push [solution]`](https://github.com/RemyDuijkeren/Flowline/wiki/03-Command-Reference#push)                 | Build and sync code assets to DEV; or push standalone with `--pluginFile` / `--webresources` |
| [`sync [solution]`](https://github.com/RemyDuijkeren/Flowline/wiki/03-Command-Reference#sync)                 | Pull the current solution state from DEV into source control                                 |
| [`deploy <target>`](https://github.com/RemyDuijkeren/Flowline/wiki/03-Command-Reference#deploy)               | Pack from the repo and import into `test`, `uat`, `prod`, or a URL                           |
| [`provision [dev\|test\|uat]`](https://github.com/RemyDuijkeren/Flowline/wiki/03-Command-Reference#provision) | Provision a DEV, TEST or UAT environment by copying from production                          |
| [`generate [solution]`](https://github.com/RemyDuijkeren/Flowline/wiki/05-Generate-Early-Bound-Types)         | Generate early-bound C# types into `Plugins/Models/` (configurable with `--output`)          |
| [`status`](https://github.com/RemyDuijkeren/Flowline/wiki/03-Command-Reference#status)                        | Show environment info, Flowline version, and PAC CLI status                                  |
| [`drift <target>`](https://github.com/RemyDuijkeren/Flowline/wiki/03-Command-Reference#drift)                 | Compare committed source against a live environment; read-only, never deletes or modifies    |

---

## Plugin attributes

Attribute-driven registration is core to Flowline, not an add-on. Add [Flowline.Attributes](src/Flowline.Attributes/README.md) to your plugin project and decorate `IPlugin` classes with `[Step]`, `[Filter]`, `[PreImage]`, `[PostImage]`, or `[CustomApi]`. `flowline push` reads the assembly and handles every registration — steps, images, and Custom APIs — no Plugin Registration Tool, no `spkl.json`.

```xml
<PackageReference Include="Flowline.Attributes" Version="1.0.0" PrivateAssets="all" />
```

Source-only package: the attributes compile straight into your plugin assembly, so nothing extra ships to the Dataverse sandbox.

**[Flowline.Attributes reference](src/Flowline.Attributes/README.md)** · **[Plugin Registration wiki](https://github.com/RemyDuijkeren/Flowline/wiki/04-Plugin-Registration)**

---

## Documentation

Full docs live on the **[Wiki](https://github.com/RemyDuijkeren/Flowline/wiki)**.

Coming from another tool? [Migration from spkl](https://github.com/RemyDuijkeren/Flowline/wiki/10-Migration-from-spkl) · [Migration from Daxif](https://github.com/RemyDuijkeren/Flowline/wiki/11-Migration-from-Daxif) · [Migration from PACX](https://github.com/RemyDuijkeren/Flowline/wiki/12-Migration-from-PACX)
