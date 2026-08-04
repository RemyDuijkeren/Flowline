---
name: flowline-plugins
description: Authoring Dataverse plugin steps and Custom APIs for Flowline — the class naming convention that determines message and stage, the `[Step]` / `[Filter]` / `[PreImage]` / `[PostImage]` / `[CustomApi]` attributes, and how registrations are verified. Use when writing, renaming, splitting, or reviewing a C# plugin class in a repo that references `Flowline.Attributes`, when converting a Power Automate flow or workflow into a plugin, or when a `flowline push` reports that a step could not be parsed or landed on the wrong message or stage.
---

# Flowline — authoring plugin steps

Registration intent lives in the code. There is no Plugin Registration Tool step and no XML to hand-edit — `flowline push` reflects the compiled assembly and makes Dataverse match it.

## The part you cannot guess

**The class name carries the message, the stage, and the processing mode. `[Step]` carries the table.**

Get the name wrong and the step registers on the wrong event, or `push` fails to parse it. Nothing else about the class communicates this.

```
{DescriptiveName}{Stage keyword}{Message}[Async][Plugin]
```

| Class name                         | Message | Stage         | Mode         |
|------------------------------------|---------|---------------|--------------|
| `SetNamePostCreatePlugin`          | Create  | PostOperation | Synchronous  |
| `RecalculateTotalsPreUpdatePlugin` | Update  | PreOperation  | Synchronous  |
| `OwnershipValidationDeletePlugin`  | Delete  | PreValidation | Synchronous  |
| `NotifyPostUpdateAsyncPlugin`      | Update  | PostOperation | Asynchronous |

Stage keywords are the short forms — `Validation`, `Pre`, `Post` — mapping to PreValidation, PreOperation, PostOperation. Append `Async` for asynchronous. `{Message}` is the Dataverse message spelled exactly (`Create`, `Update`, `Delete`, `Associate`, `AddToQueue`, …); it is case-sensitive. `{DescriptiveName}` is free — it may name the table, it doesn't have to.

Classes without `[Step]` are ignored for step registration. Classes with `[Step]` **must** parse, and `push` fails fast when they don't.

### Choosing the stage

| Keyword          | Runs                         | In transaction | Use for                                           |
|------------------|------------------------------|----------------|---------------------------------------------------|
| `Validation`     | Before the transaction opens | No             | Throwing to reject the operation cleanly          |
| `Pre`            | Before the record is saved   | Yes            | Enriching or correcting the incoming `Target`     |
| `Post`           | After the record is saved    | Yes            | Follow-up writes that must be atomic with the save |
| `Post` + `Async` | After the transaction closes | No             | Notifications, external calls, long-running work  |

Follow-up writes to *related* records belong in `Post` (sync) when they must not survive a rolled-back save — a mirror record created in `Validation` outlives a failed parent.

## Attributes

**`[Step("<table logical name>")]`** — required, one per class. Lowercase logical name; `"none"` registers on all tables. Optional: `Order`, `RunAs`, `Config`, `Description`, `DeleteJobOnSuccess`, `SecondaryTable`.

**`[Filter("col", …)]`** — Update steps only; using it on any other message is an error. Without it an Update step fires on *every* update to the table, and `push` warns. List exactly the columns the plugin reads. Prefer generated constants over string literals so a renamed column breaks the build.

**`[PreImage(…)]` / `[PostImage(…)]`** — availability is enforced at push time:

|        | PreImage      | PostImage          |
|--------|---------------|--------------------|
| Create | Not available | PostOperation only |
| Update | Any stage     | PostOperation only |
| Delete | Any stage     | Not available      |

One image per type per class. Name the columns you need — omitting them fetches all and costs performance. On Delete the `Target` is only an `EntityReference`, so a pre-image is the way to read the record that is about to disappear.

## One class = one step

This is a design rule, not a limitation to route around. It keeps `[Filter]` and the images unambiguous, keeps each `Execute` free of message branching, and makes the class name in a Dataverse error log say what actually happened.

Share logic through a base class or a helper, not through a multi-step class:

```csharp
public abstract class AccountSavePlugin : IPlugin
{
    public void Execute(IServiceProvider sp) { /* shared logic */ }
}

[Step("account")] public class AccountPreCreatePlugin : AccountSavePlugin { }
[Step("account")] public class AccountPreUpdatePlugin : AccountSavePlugin { }
```

Any class implementing `IPlugin` is eligible, directly or through a base class — a project's own `PluginBase : IPlugin` works, Flowline walks the inheritance chain.

## `[Handles]` is for migration only

`[Handles(Message, Stage)]` declares message and stage explicitly, overriding the naming convention. **It exists for brownfield migration from tools where class names were not under your control. Do not reach for it in new code — rename the class instead.**

Stacking several `[Handles]` on one class is a last resort within a migration window: all of them share the single `[Step]`, so steps on different tables cannot be merged this way regardless. Split into named leaf classes as soon as the migration allows, and expect the split to delete and recreate the steps.

## Custom APIs

`[CustomApi]` on an `IPlugin` class registers a Custom API instead of a step. No arguments = global; pass a table logical name to bind to a record, or `TableCollection` for a collection — the two are mutually exclusive. The unique name is derived by stripping the `Api`, `CustomApi`, or `Plugin` suffix and prefixing the publisher prefix.

Declare parameters on the class with `[Input(name, FieldType, …)]` and `[Output(name, FieldType)]`. Always check `InputParameters.Contains(name)` before reading an optional input.

## Verifying

1. `flowline generate` first when the schema changed **and** the plugin uses early-bound types — the generated classes in `Plugins/Models/` need to know the new columns before the plugin can reference them. Late-bound plugins (`Entity`, `entity["column"]`) never need it; skip straight to the build.
2. `dotnet build` proves the code compiles, nothing more.
3. **`flowline push --scope plugins --dry-run`** is what proves the *registration* is right:

   ```
   flowline push --scope plugins --dry-run
   ```

   It reads the built assembly and prints the plan without writing anything. Each step appears as `Message of table at Stage` — read that list against what you intended, because a class name that parsed into the wrong message or stage shows up here and nowhere else. It also surfaces the missing-`[Filter]`-on-Update warning and the blast radius before it can do damage.

   `--scope plugins` keeps the run to plugin registration: no web resource build, no form events. Omit it to preview the whole push. Inside a Flowline project the flag stands alone; `--pluginFile` is only required in standalone mode (pushing a loose DLL from outside a project folder). `--scope plugins` and `--scope assemblyonly` are mutually exclusive.
4. `push` exiting 0 means registration succeeded, not that the step behaves. Exercise the actual operation before reporting the work done.

### Outside a Flowline project

`push` also runs standalone against a loose assembly — no `.flowline`, no repo, no project layout. Useful for checking a build from CI, someone else's DLL, or a solution you haven't cloned:

```
flowline push ContosoSales --pluginFile ./bin/Release/MyPlugins.dll --dev https://contoso-dev.crm4.dynamics.com --dry-run
```

Three differences from project mode:

- **The solution name is required** — there is no `.flowline` to read it from. Same for `--dev`.
- **Nothing is built.** Standalone reflects the assembly you point at, so build it yourself first; `--no-build` has no effect here.
- **The scope follows the input.** Passing `--pluginFile` alone already scopes the run to plugins, so `--scope plugins` is redundant — and if you do pass it, it *requires* `--pluginFile`.

Run this from a folder that has no `.flowline` in it. Inside a project folder Flowline rejects `--pluginFile` and `--webresources` (exit 15) rather than guess which mode you meant — `cd` elsewhere, or drop the flags and use project mode.

Renaming a class updates the same Dataverse step in place — identity is `(message, table filter, stage, mode)`, not the display name. Changing the message, table, stage, or mode is a genuinely different registration, so it recreates the step.

## Full reference

This skill covers what you need before writing the class. For Associate/Disassociate scoping, plugin and Custom API lifecycle, `CodeActivity` packaging, multiple plugin projects, and orphan-deletion rules, read the wiki page: **[04 — Push Plugins and Custom APIs](https://github.com/RemyDuijkeren/Flowline/wiki/04-Push-Plugins-and-Custom-APIs)**.

`Flowline.Attributes` is a source-only package: every attribute ships as C# with full XML documentation under `~/.nuget/packages/flowline.attributes/<version>/contentFiles/cs/any/Flowline/`. Read `StepAttribute.cs` there when a property's exact behaviour matters.
