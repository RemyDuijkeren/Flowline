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

The _message, stage, and processing mode_ come from the **class name**; the _table and options_ come from **attributes**. This keeps each `Execute` body focused on one thing and makes log entries self-describing.

### The pipeline

| Stage keyword | When it runs | In transaction? | Use for |
|---|---|---|---|
| `Validation` | Before the transaction opens | No | Throwing to reject the operation cleanly — no rollback needed |
| `Pre` | Before the record is saved | Yes | Enriching or correcting incoming data — changes to `Target` are saved automatically |
| `Post` (sync) | After the record is saved | Yes | Follow-up writes that must be atomic with the triggering operation |
| `Post` + `Async` | After the transaction closes | No | Notifications, external API calls, long-running work |

### Class naming convention

```
{DescriptiveName}{Stage keyword}{Message}[Async][Plugin]
```

The `{DescriptiveName}` describes what the plugin does — it can include the table name but doesn't need to. The table is declared on `[Step]`.

The `{Stage keyword}` uses the shortened forms **Pre**, **Post**, and **Validation** to match Dataverse pipeline terminology (PreOperation, PostOperation, PreValidation). Shortened forms keep class names concise and readable. For async post-operation steps add the `Async` suffix, following normal C# convention.

For **validation steps**, the `Validation` keyword already carries the intent — use a noun-based description so the keyword does double duty as part of the name's meaning. `OwnershipValidationDeletePlugin` reads as "ownership validation on delete." Avoid verb-first names like `ValidateOwnership` here: `ValidateOwnershipValidationDeletePlugin` stutters.

The `{Message}` is the Dataverse message: `Create`, `Update`, `Delete`, `Retrieve`, `RetrieveMultiple`, `Associate`, `Disassociate`, `Assign`, `SetState`, etc. Names are case-sensitive and must match Dataverse exactly.

The `Plugin` suffix is optional but recommended. Flowline strips it before parsing.

| Class name | Message | Stage | Mode |
|---|---|---|---|
| `SetNamePostCreatePlugin` | Create | PostOperation | Synchronous |
| `RecalculateTotalsPreUpdatePlugin` | Update | PreOperation | Synchronous |
| `OwnershipValidationDeletePlugin` | Delete | PreValidation | Synchronous |
| `NotifyPostUpdateAsyncPlugin` | Update | PostOperation | Asynchronous |

Classes without `[Step]` are skipped. Classes with `[Step]` must follow the naming convention;
Flowline fails fast when it cannot parse the stage and message, because `[Step]` is explicit
intent to register a Dataverse plugin step.

**Brownfield class names.** If you can't rename an existing class, use `[Handles]` to declare the message and stage explicitly — see [`[Handles]`](#handles--brownfield-escape-hatch) below.

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

| Property | Type | Default | Description |
|---|---|---|---|
| `Order` | `int` | `1` | Execution order when multiple steps fire on the same event. Lower runs first. |
| `RunAs` | `string?` | `null` | GUID of the Dataverse `systemuser` to impersonate (`impersonatinguserid`). `null` runs as the calling user. |
| `Config` | `string?` | `null` | Passed to the plugin constructor as `unsecureConfig`. |
| `Description` | `string?` | `null` | Description shown in the Plugin Registration Tool. |
| `DeleteJobOnSuccess` | `bool` | `true` | Automatically delete the `AsyncOperation` job record when the step succeeds. Async post-operation steps only. Set to `false` to retain the record for auditing. |
| `SecondaryTable` | `string?` | `null` | Secondary table for Associate / Disassociate steps. Pass `"none"` to match any secondary table. See [Associate / Disassociate](#associate--disassociate) below. |

Use `RunAs` to run the plugin as a specific service account. Pass the string form of the user's GUID:

```csharp
[Step("account", RunAs = "3b36b50c-03e5-4b5f-8882-123456789abc")]
public class SetNamePostCreatePlugin : IPlugin { ... }
```

> **Use environment-stable GUIDs.** The value is stored in source control and the solution XML. Avoid personal accounts or accounts whose GUID differs between environments.

Use `Config` to pass endpoint URLs, feature flags, or JSON settings. Receive the value in a constructor overload that accepts `string unsecureConfig`:

```csharp
[Step("account", Config = "{\"endpoint\":\"https://api.example.com\"}")]
public class SetNamePostCreatePlugin : IPlugin
{
    private readonly string _endpoint;

    public SetNamePostCreatePlugin(string unsecureConfig)
    {
        _endpoint = JsonSerializer.Deserialize<Config>(unsecureConfig)!.Endpoint;
    }
}
```

> **Do not store secrets in `Config`.** The value is visible in source control and the solution XML. Use environment variables or Azure Key Vault for anything sensitive.

`DeleteJobOnSuccess` defaults to `true` — every async step execution creates an `AsyncOperation` record and Dataverse deletes it automatically on success, keeping the job queue clean. Set it to `false` explicitly if you need to retain the record for auditing or debugging:

```csharp
[Step("cr07982_invoice", DeleteJobOnSuccess = false)]
[Filter("cr07982_status")]
public class InvoicePostUpdateAsyncPlugin : IPlugin { ... }
```

> `DeleteJobOnSuccess` only applies to asynchronous (post-operation) steps. Flowline warns if you explicitly set it to `true` on a synchronous step.

### Associate / Disassociate

Use `SecondaryTable` on `[Step]` to scope the step to a specific secondary table. Pass `"none"` to match any secondary table.

```csharp
// Fires when a contact is associated with any record type
[Step("contact", SecondaryTable = "none")]
public class ContactPreAssociatePlugin : IPlugin { ... }

// Fires only when a contact is associated with an account
[Step("contact", SecondaryTable = "account")]
public class ContactAccountPreAssociatePlugin : IPlugin
{
    public void Execute(IServiceProvider sp)
    {
        var ctx             = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
        var target          = (EntityReference)ctx.InputParameters["Target"];
        var relatedEntities = (EntityReferenceCollection)ctx.InputParameters["RelatedEntities"];
        var relationship    = (Relationship)ctx.InputParameters["Relationship"];
    }
}
```

Omitting `SecondaryTable` on an Associate or Disassociate step produces a warning during `flowline push`. Passing an empty string is an error. Using `SecondaryTable` on any other message (Create, Update, Delete, ...) is an error.

### `[Filter]` — optional, strongly recommended on Update steps

Limits the step to fire only when at least one of the listed columns is included in the operation. Dataverse evaluates the filter before invoking your plugin — a filtered step that doesn't match costs almost nothing.

Without `[Filter]`, an Update step fires on **every** update to the table, regardless of which columns changed. Flowline warns if you omit `[Filter]` on an Update step.

```csharp
[Step("account")]
[Filter("name", "creditlimit")]
public class RecalculateTotalsPreUpdatePlugin : IPlugin { ... }
```

Use `nameof` with early-bound classes for compile-time safety:

```csharp
[Filter(nameof(Account.name), nameof(Account.creditlimit))]
```

> `[Filter]` only applies to Update steps. Using it on Create, Delete, or any other message is an error — Flowline will throw during `flowline push`.

### `[PreImage]` and `[PostImage]` — optional

Register snapshots of the record before and after the operation. Retrieve them from `context.PreEntityImages` and `context.PostEntityImages` inside `Execute`.

**Availability by message and stage:**

| | PreImage | PostImage |
|---|---|---|
| Create | Not available — record didn't exist yet | PostOperation only |
| Update | Any stage | PostOperation only |
| Delete | Any stage | Not available — record no longer exists |

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

### Examples

**Minimal — reject an operation before anything is written:**

```csharp
[Step("account")]
[Filter("creditlimit")]
public class CreditLimitValidationUpdatePlugin : IPlugin
{
    public void Execute(IServiceProvider sp)
    {
        var ctx    = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
        var target = (Entity)ctx.InputParameters["Target"];

        if (target.GetAttributeValue<Money>("creditlimit")?.Value > 100_000)
            throw new InvalidPluginExecutionException("Credit limit cannot exceed 100,000.");
    }
}
```

**Enrich a record before it is saved:**

```csharp
[Step("account")]
public class SetSourceTagPreCreatePlugin : IPlugin
{
    public void Execute(IServiceProvider sp)
    {
        var ctx    = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
        var target = (Entity)ctx.InputParameters["Target"];
        target["cr123_source"] = "web";  // included in the save automatically
    }
}
```

**Call an external system after the save, in the background:**

```csharp
[Step("cr07982_invoice")]
[Filter("cr07982_status")]
public class InvoicePostUpdateAsyncPlugin : IPlugin
{
    public void Execute(IServiceProvider sp)
    {
        // Runs after the transaction commits — safe to call external APIs here.
        // A failure does not roll back the record.
        // DeleteJobOnSuccess defaults to true — the AsyncOperation record is cleaned up automatically.
    }
}
```

### `[Handles]` — brownfield escape hatch

When a class name doesn't follow the naming convention, use `[Handles]` to declare the message and stage explicitly. Prefer renaming the class — a conventional name documents intent and makes `[Handles]` unnecessary.

```csharp
// Explicit message and stage
[Step("account")]
[Handles(Message.Update, Stage.PreOperation)]
public class AccountPlugin : IPlugin { ... }

// Async — PostOperationAsync maps to PostOperation + asynchronous mode
[Step("account")]
[Handles(Message.Create, Stage.PostOperationAsync)]
public class AccountPlugin : IPlugin { ... }

// Custom API message (string overload)
[Step("account")]
[Handles("mynamespace_MyAction", Stage.PostOperation)]
public class AccountPlugin : IPlugin { ... }
```

`[Handles]` requires `[Step]` to be present. The `Message` enum covers all built-in Dataverse messages; the string overload accepts any Custom API message name.

If the class name would also parse to the same registration (message, stage, and mode all match), Flowline warns that `[Handles]` is redundant.

#### Stacking `[Handles]` — last resort for brownfield migration

> [!CAUTION]
> Stacking `[Handles]` goes against Flowline's intent. Flowline is designed around **one class = one step** — the class name carries the step's identity. Multi-step classes obscure that contract and make `push` output harder to read.
>
> **Hard limitation:** all stacked handles share the same `[Step]`, which sets the primary table. If any two steps fire on **different primary tables**, stacking cannot help — you need two separate classes regardless.

Use only when migrating from a tool where one class handled multiple steps and splitting right now is not feasible. Split into named subclasses as soon as the migration window allows.

```csharp
[Step("account")]
[Handles(Message.Create, Stage.PostOperation)]
[Handles(Message.Update, Stage.PostOperation)]
public class AccountPlugin : IPlugin { ... }
```

Each `[Handles]` produces one step. Step names are derived from the class name and the message:

- `MyNamespace.AccountPlugin: Create of account`
- `MyNamespace.AccountPlugin: Update of account`

When two handles share the same message (e.g., `Update PreOperation + Update PostOperation`), all step names in the class include the stage to stay distinct:

- `MyNamespace.AccountPlugin: Update of account at PreOperation`
- `MyNamespace.AccountPlugin: Update of account at PostOperation`

`[Filter]`, `[PreImage]`, and `[PostImage]` are applied per-step based on compatibility — `[Filter]` applies only to Update/UpdateMultiple steps; `[PreImage]` not on Create; `[PostImage]` not on Delete at PostOperation.

**Flowline emits a warning** (once per class, on the first step) nudging you to split the class into named subclasses — each covering one step — as the long-term goal.

**Splitting warning:** renaming the class after splitting changes the Dataverse step names. The next `flowline push` deletes the old steps and creates new ones. Plan the split for a maintenance window to avoid registered-step downtime.

### Lifecycle

Flowline treats the DLL as the source of truth. On every `flowline push`:

- **Plugin types** — created for every public `IPlugin` or `CodeActivity` class; deleted when the class is removed.
- **Steps** — created or updated for every class with `[Step]`; deleted when `[Step]` is removed or the class is deleted.

Steps created by Flowline are stamped with `[flowline]` in their description, visible in Plugin Registration Tool.

**Step identity.** Flowline finds an existing step by `(message, table filter, stage, mode)` on the
owning plugin type — never by the generated display name. Renaming a class, adding a
`[DescriptiveName]`, or otherwise changing the display text updates the same Dataverse row in
place; it never deletes and recreates the step. Changing the message, table, stage, or mode itself
is a different registration, so it does recreate the step — Dataverse runs a `PreValidation` step
at a genuinely different point in the pipeline than a `PostOperation` step, so this reflects a real
behavior change, not just cosmetics.

**Disabling a step without deleting it:** remove `[Step]` — Flowline deletes the step but keeps the plugin type registered.

**`--no-delete` flag:** suppresses all deletions for that run and prints each skipped item:

```bash
flowline push MySolution --no-delete
```

**`--dry-run` flag:** prints the changes that would be made without actually making them:

```bash
flowline push MySolution --dry-run
```

---

## Custom APIs

A Custom API is a custom endpoint you define in Dataverse, invoked explicitly by name — from Power Automate, a canvas app, a web resource, or another plugin. Unlike a plugin step, it does not fire automatically on record changes.

Add `[CustomApi]` to an `IPlugin` class to register it as a Custom API. You write `Execute` exactly as normal — Flowline handles the Dataverse registration.

### Class naming

Flowline strips the `Api`, `CustomApi`, or `Plugin` suffix and prefixes the result with the solution's publisher prefix:

| Class name | Unique name |
|---|---|
| `GetAccountRiskApi` | `cr123_GetAccountRisk` |
| `SendNotificationCustomApi` | `cr123_SendNotification` |
| `ApproveOrderPlugin` | `cr123_ApproveOrder` |

The publisher prefix is read from the solution automatically.

**Migrating an existing class from another tool?** Use `UniqueName` to pin the exact live Custom API name instead:

```csharp
// spkl: [CrmPluginRegistration("dev1_ApproveOrder")]
// Flowline: keep the class as-is, adopt the same live Custom API in place
[CustomApi(UniqueName = "dev1_ApproveOrder")]
public class LegacyOrderApprovalPlugin : IPlugin { ... }
```

`UniqueName` must be the **complete** Dataverse unique name, publisher prefix included — Flowline validates it starts with the solution's actual prefix and throws if it doesn't; it never prepends the prefix on your behalf when `UniqueName` is set. Two Custom APIs resolving to the same final name — whether derived or explicit — fail the push.

> **This does not preserve identity across a C# class rename.** Plugin type identity follows the class's fully-qualified name; renaming the class always produces a new plugin type, regardless of `UniqueName`. Use `UniqueName` only when the class itself is unchanged.

### `[CustomApi]` — required

Without arguments, registers a **global API** — not tied to any table:

```csharp
[CustomApi]
public class SendNotificationApi : IPlugin { ... }
```

Pass a table logical name for **entity binding**. Dataverse automatically provides a `Target` `EntityReference` — you do not declare it yourself:

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

Use `TableCollection` for **table collection binding**:

```csharp
[CustomApi(TableCollection = "invoice")]
public class BulkApproveApi : IPlugin { ... }
```

Optional named properties:

| Property | Type | Default | Description |
|---|---|---|---|
| `IsFunction` | `bool` | `false` | `false` = Action (HTTP POST). `true` = Function (HTTP GET). |
| `IsPrivate` | `bool` | `false` | Hides the API from the OData catalog. |
| `AllowedStepType` | `AllowedStepType` | `None` | Whether third parties can register plugin steps on this API. |
| `DisplayName` | `string?` | class name split | Shown in solution explorer. |
| `Description` | `string?` | `null` | Shown in solution explorer. |
| `ExecutePrivilege` | `string?` | `null` | Privilege required to call this API. Omit to allow any authenticated user. |
| `UniqueName` | `string?` | class name derived | Overrides the unique name. **Complete** name, publisher prefix included (e.g. `"dev1_MyCustomApi"`). See [Class naming](#class-naming). |

### `[Input]` and `[Output]`

Declare parameters on the class. These are registration declarations only — Flowline registers them in Dataverse; you read and write the values yourself in `Execute`.

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

Always check `Contains` before reading an optional input — Dataverse throws if the caller omitted it.

`[Input]` properties:

| Property | Type | Default | Notes |
|---|---|---|---|
| `Name` | `string` | — | Key in `context.InputParameters`. Convention: camelCase. |
| `Type` | `FieldType` | — | See type table below. |
| `IsOptional` | `bool` | `false` | Check `Contains("name")` before reading when `true`. |
| `Table` | `string?` | `null` | Required when type is `EntityReference` or `Entity`. |
| `DisplayName` | `string?` | name split | Shown in solution explorer. |
| `Description` | `string?` | `null` | |

`[Output]` has the same properties except `IsOptional`.

### Supported types

| C# type in `Execute` | `FieldType` |
|---|---|
| `bool` | `Boolean` |
| `DateTime` | `DateTime` |
| `decimal` | `Decimal` |
| `Entity` | `Entity` |
| `EntityCollection` | `EntityCollection` |
| `EntityReference` | `EntityReference` |
| `float` / `double` | `Float` |
| `int` | `Integer` |
| `Money` | `Money` |
| `OptionSetValue` | `Picklist` |
| `string` | `String` |
| `string[]` | `StringArray` |
| `Guid` | `Guid` |

### Lifecycle

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

### Why other tools can't do this

Other tools run a separate sync pass per type — first workflows, then plugins (or vice versa). Each pass treats the assembly as its own domain and deletes any registrations it doesn't recognise. The result: syncing plugins after workflow activities wipes the workflow registrations, and syncing workflow activities after plugins wipes the plugin registrations. You end up maintaining separate projects and separate DLLs just to keep the two sync passes from destroying each other.

Flowline reads all types from the assembly in a single pass and registers everything together. There is no ordering problem.

### Why one assembly is better

A DLL is a deployment unit. Plugins and workflow activities are always deployed to the same environment at the same time — splitting them into separate DLLs reflects a historical assumption about project structure, not a technical requirement.

When you consolidate into one assembly you get four concrete benefits:

**No separate early-bound types project.** Early-bound generated classes are shared across plugin steps and workflow activities. With one assembly project, they live there directly. With separate projects you need a third project just for the shared types.

**ILMerge is not needed.** When everything lives in one project, external references are resolved normally by the build. Separate projects often require ILMerge to bundle shared dependencies into each DLL — an extra build step with its own failure modes.

**Maintainability.** At minimum three projects (plugins, workflow activities, early-bound types) collapse into one. Fewer projects means less cognitive overhead for developers, fewer things to go wrong during deployment, and a simpler ALM/DevOps pipeline.

**Performance.** Microsoft's best practices [recommend consolidating plug-ins and custom workflow activities into a single assembly](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/best-practices/business-logic/optimize-assembly-development#consolidate-plug-ins-or-custom-workflow-activities-into-a-single-assembly). [Multiple assemblies cause additional loading and caching work on the server](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/best-practices/business-logic/optimize-assembly-development#multiple-assemblies), which can increase overall execution time.
