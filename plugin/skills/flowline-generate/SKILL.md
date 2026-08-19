---
name: flowline-generate
description: Early-bound Dataverse types via `flowline generate` — when they are needed at all, where the generated models land, which settings persist to `.flowline`, and the four generators (pac, xrmcontext, xrmcontext3, ebg). Use when a plugin references a table or column that does not compile, when `Models/` is missing or stale after a schema change, when choosing between late-bound and early-bound, or when `generate` output lands in an unexpected folder or namespace.
---

# Flowline — early-bound types

`flowline generate` writes C# classes for the solution's entities, option sets, and Custom APIs.
Flowline queries Dataverse for what the solution actually contains and passes that as the filter, so
there is no manual entity list to maintain.

## First: is it needed?

**Only early-bound code needs it.** A plugin using `Entity`, `entity["name"]`, and
`EntityReference` never needs `generate` — go straight to `dotnet build`. Run it when the plugin
references generated classes and the schema changed, or on a fresh clone where `Models/` isn't
committed.

**It does not write `DATAVERSE_CONTEXT.md`.** That file comes from `sync` (and `clone`/`init`). A
stale schema doc is not a reason to run `generate`.

## Where output lands

Default is a `Models/` folder **beside the primary plugin project** — so a relocated `Plugins/`
takes its models with it. Only when no plugin project is on disk does it fall back to the literal
`Plugins/Models`. Pass `--output` once to override; it is saved.

```
Models/
├── Entities/              one .cs per entity
├── Messages/              one .cs per Custom API
├── OptionSets/            option set enums
├── EntityOptionSetEnum.cs
└── XrmContext.cs          OrganizationServiceContext for LINQ
```

**`Models/` is overwritten on every run.** Flowline warns when there are uncommitted changes there.
Never hand-edit generated files — put customizations in a partial class in your own folder.

## Settings persist to `.flowline`

`--namespace`, `--output`, `--service-context-name`, `--extra-tables`, and `--generator` are all
written to `.flowline` on use and reused on every later run. That makes the *first* run the one that
sets project-wide defaults.

- **Namespace** is derived from the primary plugin project's assembly name on the first run if not
  given. `--namespace` overrides and saves.
- **`--extra-tables` replaces the saved list, it does not append.** Passing
  `--extra-tables account` after `--extra-tables account,contact` drops `contact`. Pass the full
  list every time. An empty value clears it.
- Extra tables are for standard tables a plugin reads but the solution doesn't own — `account`,
  `contact`, `systemuser`.

Standalone mode (no `.flowline`) requires solution name, `--dev`, and `-o` explicitly, and saves
nothing.

## Generators

| `--generator` | Engine | Output style | Platform |
|---------------|--------|--------------|----------|
| `pac` *(default)* | `pac modelbuilder build` | Microsoft-style — lowercase filenames, `OptionSetValue` | Cross-platform |
| `xrmcontext` | XrmContext v4 dotnet tool | Typed enums, `ServiceContext`, PascalCase | Cross-platform |
| `xrmcontext3` | Delegate.XrmContext v3 F# exe | Same output as `xrmcontext` | Windows only, legacy |
| `ebg` | Early Bound Generator V2, in-process | PascalCase, typed enums, `ServiceContext` | Cross-platform |

**Do not switch a project's generator on your own initiative.** Switching rewrites every file in
`Models/` and changes the shape the plugin code compiles against — a compile-breaking change
disguised as a regeneration. Report the options and let the user decide. Leave a project on whatever
`.flowline` says, or on the `pac` default when nothing is saved.

XrmContext-generated code references `System.ComponentModel.DataAnnotations`; the plugin project
needs the `System.ComponentModel.Annotations` package or it will not compile.

Both XrmContext generators pick up the PAC auth profile automatically. A service principal profile
needs `--client-secret`, and `--client-id` overrides the application id from the profile.

`ebg` is the only generator that does not shell out. It runs Early Bound Generator V2 inside
Flowline against Microsoft's `ModelBuilderLib`, reusing the Dataverse connection already open, so
`--client-id` and `--client-secret` do not apply to it and PAC CLI is never called for generation.
A `builderSettings.json` at the project root is merged under Flowline's derived settings, which is
the only way to reach EBG's remaining options.

## Verifying

`generate` succeeding means files were written — nothing more. `dotnet build` is what proves the
plugin compiles against them. If a column you just added isn't in the generated class, check that it
is actually in the solution: `generate` only emits what the solution contains, plus `--extra-tables`.

`--force config` is the only force specifier this command accepts.

## Full reference

Generator auth details, CI setup, and the separate-models-project layout:
**[09 — Generate Early Bound Types](https://github.com/RemyDuijkeren/Flowline/wiki/09-Generate-Early-Bound-Types)**.
