# Flowline.Attributes

<table>
<tr>
<td>

Source-only NuGet package that provides attributes for registering Dataverse plugin steps and Custom APIs with the [Flowline CLI](https://github.com/RemyDuijkeren/Flowline).

Run `flowline push` and Flowline inspects your plugin assembly and automatically creates or updates all registrations in Dataverse — no Plugin Registration Tool needed.


[![Docs](https://img.shields.io/badge/docs-wiki-blue?logo=readthedocs&logoColor=white)](https://github.com/RemyDuijkeren/Flowline/wiki)
[![CI](https://github.com/RemyDuijkeren/Flowline/workflows/CI/badge.svg)](https://github.com/RemyDuijkeren/Flowline/workflows/CI/badge.svg)
[![NuGet](https://img.shields.io/nuget/v/Flowline.Attributes.svg)](https://www.nuget.org/packages/Flowline.Attributes)
[![NuGet downloads](https://img.shields.io/nuget/dt/Flowline.Attributes.svg)](https://www.nuget.org/packages/Flowline.Attributes)

</td>
<td width="120" align="right" valign="top">

![Flowline CLI](https://raw.githubusercontent.com/RemyDuijkeren/Flowline/master/docs/Flowline-icon.png)

</td>
</tr>
</table>

## Installation

```xml
<PackageReference Include="Flowline.Attributes" Version="1.0.0" PrivateAssets="all" />
```

`PrivateAssets="all"` keeps this as a development-time dependency. The attributes compile directly into your plugin assembly — no extra DLL is shipped, which keeps the Dataverse sandbox happy.

---

## Plugin steps

Flowline registers **[exactly one](#why-one-class-per-step)** plugin step per `IPlugin` class.

The _message, stage, and processing mode_ come from the **class name**; the _table and options_ come from **attributes**.

Any class that implements `IPlugin` — directly or through a base class — is eligible. Using your own `PluginBase : IPlugin` works fine; Flowline walks the full inheritance chain.

### The pipeline

| Stage keyword    | When it runs                 | In transaction? | Use for                                                                             |
|------------------|------------------------------|-----------------|-------------------------------------------------------------------------------------|
| `Validation`     | Before the transaction opens | No              | Throwing to reject the operation cleanly — no rollback needed                       |
| `Pre`            | Before the record is saved   | Yes             | Enriching or correcting incoming data — changes to `Target` are saved automatically |
| `Post` (sync)    | After the record is saved    | Yes             | Follow-up writes that must be atomic with the triggering operation                  |
| `Post` + `Async` | After the transaction closes | No              | Notifications, external API calls, long-running work                                |

### Class naming convention

```
{DescriptiveName}{Stage keyword}{Message}[Async][Plugin]
```

| Class name                         | Message | Stage         | Mode         |
|------------------------------------|---------|---------------|--------------|
| `SetNamePostCreatePlugin`          | Create  | PostOperation | Synchronous  |
| `RecalculateTotalsPreUpdatePlugin` | Update  | PreOperation  | Synchronous  |
| `OwnershipValidationDeletePlugin`  | Delete  | PreValidation | Synchronous  |
| `NotifyPostUpdateAsyncPlugin`      | Update  | PostOperation | Asynchronous |

The `{DescriptiveName}` describes what the plugin does, it can have the table name but it doesn't need to. The table is declared on `[Step]`.

For `{Stage keyword}` we use the shortened versions **Pre**, **Post** and **Validation** to match the Dataverse pipeline terminology (PreOperation, PostOperation, PreValidation) because it makes the class names more concise and readable, like `SetNamePostCreatePlugin`. To make it async you add the `Async` suffix, like the normal C# convention.

The `{Message}` is the Dataverse message, like Create, Update, Delete, Associate, Disassociate, AddToQueue, etc. The message is case-sensitive and must match the Dataverse message exactly. See also [Available events](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/event-framework#available-events).

Classes without `[Step]` are skipped for step registration. Classes with `[Step]` must follow the naming convention — Flowline fails fast when it cannot parse the stage and message or when invalid combinations are used.

**Brownfield class names.** If you can't rename an existing class, use `[Handles]` — see [below](#handles--brownfield-escape-hatch).

### `[Step]` — required

Specifies the table logical name. Without it, Flowline ignores the class.

```csharp
[Step("account")]
public class SetNamePostCreatePlugin : IPlugin { ... }
```

The logical name is always lowercase and found in the maker portal under **Table → Properties → Name**.

**Registering on all tables:** pass `"none"` explicitly:

```csharp
[Step("none")]
public class GlobalPreCreatePlugin : IPlugin { ... }
```

Optional named properties:

| Property             | Type      | Default | Description                                                                                     |
|----------------------|-----------|---------|-------------------------------------------------------------------------------------------------|
| `Order`              | `int`     | `1`     | Execution order when multiple steps fire on the same event. Lower runs first.                   |
| `RunAs`              | `string?` | `null`  | GUID of the `systemuser` to impersonate. `null` runs as the calling user.                       |
| `Config`             | `string?` | `null`  | Passed to the plugin constructor as `unsecureConfig`.                                           |
| `Description`        | `string?` | `null`  | Description shown in the Plugin Registration Tool.                                              |
| `DeleteJobOnSuccess` | `bool`    | `true`  | Delete the `AsyncOperation` job record on success. Async steps only.                            |
| `SecondaryTable`     | `string?` | `null`  | Secondary table for Associate / Disassociate steps. Pass `"none"` to match any secondary table. |

### `[Filter]` — optional, strongly recommended on Update steps

Limits the step to fire only when at least one of the listed columns is included in the operation. Without `[Filter]`, an Update step fires on **every** update to the table. Flowline warns if you omit it on an Update step.

```csharp
[Step("account")]
[Filter("name", "creditlimit")]
public class RecalculateTotalsPreUpdatePlugin : IPlugin { ... }
```

Use `nameof` with early-bound classes for compile-time safety:

```csharp
[Filter(nameof(Account.name), nameof(Account.creditlimit))]
```

> `[Filter]` only applies to Update steps. Using it on any other message is an error.

### `[PreImage]` and `[PostImage]` — optional

Register snapshots of the record before and after the operation.

**Availability by message and stage:**

|        | PreImage      | PostImage          |
|--------|---------------|--------------------|
| Create | Not available | PostOperation only |
| Update | Any stage     | PostOperation only |
| Delete | Any stage     | Not available      |

Violations are errors — Flowline throws during `flowline push`.

Specify only the columns your plugin needs. Omitting columns fetches all of them, which negatively impacts performance. Flowline warns when no columns are specified.

```csharp
[Step("account")]
[Filter("name", "creditlimit")]
[PreImage("name", "creditlimit")]
[PostImage("name", "creditlimit")]
public class RecalculateTotalsPostUpdatePlugin : IPlugin
{
    public void Execute(IServiceProvider sp)
    {
        var ctx       = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
        var preImage  = ctx.PreEntityImages["preimage"];
        var postImage = ctx.PostEntityImages["postimage"];

        if (preImage.GetAttributeValue<string>("name") != postImage.GetAttributeValue<string>("name"))
        {
            // name changed — react here
        }
    }
}
```

Default aliases are `"preimage"` and `"postimage"`. Override `Alias` when migrating from a manually registered step with a different alias:

```csharp
[PreImage(Alias = "legacy_pre")]  // retrieve with: ctx.PreEntityImages["legacy_pre"]
```

**One image per type, by design.** `[PreImage]` and `[PostImage]` each allow at most one instance
per class — this is intentional, not a Dataverse limitation (the platform itself allows multiple
images of the same type on one step). Splitting one step's columns across several same-type images
has no benefit over declaring them together on a single image, so Flowline doesn't expose the
option. Flowline finds an existing image by `(step, image type)`; renaming `Alias` or changing the
column list updates the same record in place.

### `[Handles]` — brownfield escape hatch

When a class name doesn't follow the naming convention, use `[Handles]` to declare message and stage explicitly:

```csharp
[Step("account")]
[Handles(Message.Update, Stage.PreOperation)]
public class AccountPlugin : IPlugin { ... }

// Async
[Step("account")]
[Handles(Message.Create, Stage.PostOperationAsync)]
public class AccountPlugin : IPlugin { ... }

// Custom API message
[Step("account")]
[Handles("mynamespace_MyAction", Stage.PostOperation)]
public class AccountPlugin : IPlugin { ... }
```

`[Handles]` requires `[Step]` to be present.

#### Stacking `[Handles]` — last resort for brownfield migration

> [!CAUTION]
> Stacking `[Handles]` goes against Flowline's intent. Flowline is designed around **one class = one step** — the class name carries the step's identity. Multi-step classes obscure that contract and make `push` output harder to read.
>
> **Hard limitation:** all stacked handles share the same `[Step]`, which sets the primary table. If any two steps fire on **different primary tables**, stacking cannot help — you need two separate classes regardless.

Use only when migrating from a tool where one class handled multiple steps and splitting right now is not feasible. Split into named subclasses as soon as the migration window allows.

```csharp
// Valid — both steps fire on the same table ("account")
[Step("account")]
[Handles(Message.Create, Stage.PostOperation)]
[Handles(Message.Update, Stage.PostOperation)]
public class AccountPlugin : IPlugin { ... }

// Not possible via stacking — steps fire on different tables
// These must be separate classes with separate [Step] attributes
[Step("account")]
[Handles(Message.Create, Stage.PostOperation)]
[Step("contact")]                                 // ← compile error: duplicate attribute
[Handles(Message.Update, Stage.PostOperation)]
public class MultiTablePlugin : IPlugin { ... }
```

Each `[Handles]` produces one step. When two handles share the same message, all step names include the stage suffix to stay distinct:

- `MyNamespace.AccountPlugin: Update of account at PreOperation`
- `MyNamespace.AccountPlugin: Update of account at PostOperation`

`[Filter]`, `[PreImage]`, and `[PostImage]` are applied per-step based on message compatibility.

Flowline emits a warning once per class (on the first step) to flag the multi-step pattern.

**Splitting changes step names.** The next push deletes the old steps and creates new ones — plan for a maintenance window.

---

### Associate / Disassociate

Use `SecondaryTable` on `[Step]` to scope the step to a specific secondary table:

```csharp
// Fires when a contact is associated with any record type
[Step("contact", SecondaryTable = "none")]
public class ContactPreAssociatePlugin : IPlugin { ... }

// Fires only when a contact is associated with an account
[Step("contact", SecondaryTable = "account")]
public class ContactAccountPreAssociatePlugin : IPlugin { ... }
```

On Associate/Disassociate steps, pass `"none"` to match any secondary table.

---

### Plugin Lifecycle

On every `flowline push`:

- **Plugin types** — created for every public `IPlugin` or `CodeActivity` class; deleted when the class is removed.
- **Steps** — created or updated for every class with `[Step]`; deleted when `[Step]` is removed or the class is deleted.

Steps created by Flowline are stamped with `[flowline]` in their description, visible in Plugin Registration Tool.

**Step identity.** Flowline finds an existing step by `(message, table filter, stage, mode)` on the
owning plugin type — never by the generated display name. Renaming a class or otherwise changing
the display text updates the same Dataverse row in place; it never deletes and recreates the step.
Changing the message, table, stage, or mode itself is a different registration, so it does recreate
the step — a `PreValidation` step runs at a genuinely different point in the pipeline than a
`PostOperation` step, so this is a real behavior change, not just cosmetics.

Use `--no-delete` to suppress all deletions for a run. Use `--dry-run` to preview changes without applying them.

---

## Custom APIs

A [Custom API](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/custom-api) is a custom endpoint you define in Dataverse, invoked explicitly by name — from Power Automate, a canvas app, a web resource, or another plugin.

Add `[CustomApi]` to an `IPlugin` class to register it as a Custom API. You write `Execute` exactly as normal — Flowline handles the Dataverse registration.

### Class naming

Flowline strips the `Api`, `CustomApi`, or `Plugin` suffix and prefixes the result with the solution's publisher prefix:

| Class name                  | Unique name           |
|-----------------------------|-----------------------|
| `GetAccountRiskApi`         | `av_GetAccountRisk`   |
| `SendNotificationCustomApi` | `av_SendNotification` |
| `ApproveOrderPlugin`        | `av_ApproveOrder`     |

### `[CustomApi]` — required

Without arguments, registers a **global API** — not tied to any table:

```csharp
[CustomApi]
public class SendNotificationApi : IPlugin { ... }
```

Pass a table logical name for **entity binding**:

```csharp
[CustomApi("salesorder")]
public class ApproveOrderApi : IPlugin
{
    public void Execute(IServiceProvider sp)
    {
        var ctx     = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
        var orderId = ((EntityReference)ctx.InputParameters["Target"]).Id;
    }
}
```

Optional named properties:

| Property           | Type              | Default            | Description                                                                                                                                                                                                    |
|--------------------|-------------------|--------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `IsFunction`       | `bool`            | `false`            | `true` = Function (HTTP GET). `false` = Action (HTTP POST).                                                                                                                                                    |
| `IsPrivate`        | `bool`            | `false`            | Hides the API from the OData catalog.                                                                                                                                                                          |
| `AllowedStepType`  | `AllowedStepType` | `None`             | Whether third parties can register plugin steps on this API.                                                                                                                                                   |
| `DisplayName`      | `string?`         | class name split   | Shown in solution explorer.                                                                                                                                                                                    |
| `Description`      | `string?`         | `null`             | Shown in solution explorer.                                                                                                                                                                                    |
| `ExecutePrivilege` | `string?`         | `null`             | Privilege required to call this API.                                                                                                                                                                           |
| `UniqueName`       | `string?`         | class name derived | Overrides the unique name — **complete** name, publisher prefix included (e.g. `"av_MyCustomApi"`); throws if it doesn't start with the solution's prefix. For migrating an unrenamed class from another tool. |

### `[Input]` and `[Output]`

Declare Custom API parameters on the class:

```csharp
[CustomApi]
[Input("accountId",      FieldType.EntityReference, Table = "account")]
[Input("includeHistory", FieldType.Boolean, IsOptional = true)]
[Output("riskScore",     FieldType.Integer)]
[Output("riskLabel",     FieldType.String)]
public class GetAccountRiskApi : IPlugin
{
    public void Execute(IServiceProvider sp)
    {
        var ctx = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));

        var accountId   = (EntityReference)ctx.InputParameters["accountId"];
        var withHistory = ctx.InputParameters.Contains("includeHistory")
                          && (bool)ctx.InputParameters["includeHistory"];

        ctx.OutputParameters["riskScore"] = ComputeScore(accountId, withHistory);
        ctx.OutputParameters["riskLabel"] = "High";
    }
}
```

Always check `Contains` before reading an optional input.

`[Input]` properties:

| Property      | Type        | Default    | Notes                                                    |
|---------------|-------------|------------|----------------------------------------------------------|
| `Name`        | `string`    | —          | Key in `context.InputParameters`. Convention: camelCase. |
| `Type`        | `FieldType` | —          | See type table below.                                    |
| `IsOptional`  | `bool`      | `false`    | Check `Contains("name")` before reading when `true`.     |
| `Table`       | `string?`   | `null`     | Required when type is `EntityReference` or `Entity`.     |
| `DisplayName` | `string?`   | name split | Shown in solution explorer.                              |
| `Description` | `string?`   | `null`     |                                                          |

`[Output]` has the same properties except `IsOptional`.

### Supported types

| C# type in `Execute` | `FieldType`        |
|----------------------|--------------------|
| `bool`               | `Boolean`          |
| `DateTime`           | `DateTime`         |
| `decimal`            | `Decimal`          |
| `Entity`             | `Entity`           |
| `EntityCollection`   | `EntityCollection` |
| `EntityReference`    | `EntityReference`  |
| `float` / `double`   | `Float`            |
| `int`                | `Integer`          |
| `Money`              | `Money`            |
| `OptionSetValue`     | `Picklist`         |
| `string`             | `String`           |
| `string[]`           | `StringArray`      |
| `Guid`               | `Guid`             |

### Custom API Lifecycle

Flowline treats the DLL as the source of truth for Custom APIs, the same as for steps.

- **Created** when a class with `[CustomApi]` has no matching unique name in Dataverse.
- **Updated** when mutable fields change (`DisplayName`, `Description`, `IsPrivate`, `ExecutePrivilege`).
- **Deleted and recreated** when an immutable field changes (binding type, `IsFunction`, `AllowedStepType`, or a parameter's type or optionality). Flowline warns before doing this.
- **Deleted** when the class or `[CustomApi]` is removed.

The `--no-delete` flag suppresses deletions the same way it does for steps.

---

## Why one class per step

Each plugin class registers exactly one step. This constraint pays dividends:

**Unambiguous attributes.** `[Filter]`, `[PreImage]`, and `[PostImage]` always belong to exactly one step — the one the class registers. No need to associate them with a particular step in a multi-step registration.

**Focused `Execute` bodies.** Without the rule, `Execute` needs branching logic to handle different messages. With it, every `Execute` does one thing — Dataverse guarantees which step fired because only one is registered.

**Self-describing logs.** When a plugin throws, Dataverse logs the class name. `SetNamePostCreatePlugin` tells you exactly what happened; `AccountPlugin` does not.

**Shared logic via base classes.** The rule does not mean duplicating code. Put shared logic in a base class and declare one leaf class per step:

```csharp
public abstract class AccountSavePlugin : IPlugin
{
    public void Execute(IServiceProvider sp) { /* shared logic */ }
}

[Step("account")]
public class AccountPreCreatePlugin : AccountSavePlugin { }

[Step("account")]
public class AccountPreUpdatePlugin : AccountSavePlugin { }

// Same pattern for multiple entities:
[Step("contact")]
public class ContactPreCreatePlugin : AccountSavePlugin { }
```

---

## One assembly for everything

Flowline supports `IPlugin` classes, `CodeActivity` workflow activities, and `[CustomApi]` classes all in the same assembly. One `Plugins` project, one `Plugins.dll`, one `flowline push`.

Other tools run a separate sync pass per type — each pass deletes registrations it doesn't recognise, causing the passes to destroy each other's work. Flowline reads all types in a single pass and registers everything together.

This also means:
- No separate early-bound types project — generated classes live directly in `Plugins/`.
- No ILMerge — external references are resolved normally by the build.
- Fewer projects, less cognitive overhead, simpler pipeline.

Microsoft's best practices [recommend consolidating plug-ins and custom workflow activities into a single assembly](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/best-practices/business-logic/optimize-assembly-development#consolidate-plug-ins-or-custom-workflow-activities-into-a-single-assembly).
