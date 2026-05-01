# Flowline.Attributes

Source-only NuGet package that provides attributes for registering Dataverse plugin steps and Custom APIs with the Flowline CLI.

Run `flowline push` and Flowline inspects your plugin assembly and automatically creates or updates all registrations in Dataverse — no Plugin Registration Tool needed.

## Installation

```xml
<PackageReference Include="Flowline.Attributes" Version="1.0.0" PrivateAssets="all" />
```

`PrivateAssets="all"` keeps this as a development-time dependency. The attributes compile directly into your plugin assembly — no extra DLL is shipped, which keeps the Dataverse sandbox happy.

---

## Plugin steps

Each `IPlugin` class registers **exactly one** plugin step. The message, stage, and processing mode come from the class name; the table and options come from attributes. This keeps each `Execute` body focused on one thing and makes log entries self-describing.

### The pipeline

| Stage keyword | When it runs | In transaction? | Use for |
|---|---|---|---|
| `Validation` | Before the transaction opens | No | Throwing to reject the operation cleanly — no rollback needed |
| `Pre` | Before the record is saved | Yes | Enriching or correcting incoming data — changes to `Target` are saved automatically |
| `Post` (sync) | After the record is saved | Yes | Follow-up writes that must be atomic with the triggering operation |
| `Post` + `Async` | After the transaction closes | No | Notifications, external API calls, long-running work |

### Class naming

```
{DescriptiveName}{Stage}{Message}[Async][Plugin]
```

The `Plugin` suffix is optional but recommended. Flowline strips it before parsing.

| Class name | Message | Stage | Mode |
|---|---|---|---|
| `AccountPostCreatePlugin` | Create | PostOperation | Synchronous |
| `InvoicePreUpdatePlugin` | Update | PreOperation | Synchronous |
| `ContactValidationDeletePlugin` | Delete | PreValidation | Synchronous |
| `OrderPostUpdateAsyncPlugin` | Update | PostOperation | Asynchronous |

Common messages: `Create`, `Update`, `Delete`, `Retrieve`, `RetrieveMultiple`, `Associate`, `Disassociate`, `Assign`, `SetState`. Names are case-sensitive.

Classes without a recognisable stage keyword or without `[Entity]` are skipped.

### `[Entity]` — required

Specifies the table logical name. Without it, Flowline ignores the class.

```csharp
[Entity("account")]
public class AccountPostCreatePlugin : IPlugin { ... }
```

The logical name is always lowercase and found in the maker portal under **Table → Properties → Name**.

Optional named properties:

| Property | Type | Default | Description |
|---|---|---|---|
| `Order` | `int` | `1` | Execution order when multiple steps fire on the same event. Lower runs first. |
| `As` | `ExecuteAs` | `CallingUser` | User identity the plugin runs under (`context.UserId`). |
| `Configuration` | `string?` | `null` | Passed to the plugin constructor as `unsecureConfig`. |
| `DeleteJobOnSuccess` | `bool` | `false` | Automatically delete the `AsyncOperation` job record when the step succeeds. Async post-operation steps only. |

Use `ExecuteAs.InitiatingUser` when a Power Automate flow triggers your plugin and you need the human user's identity rather than the flow service account:

```csharp
[Entity("account", As = ExecuteAs.InitiatingUser)]
public class AccountPostCreatePlugin : IPlugin { ... }
```

Use `Configuration` to pass endpoint URLs, feature flags, or JSON settings. Receive the value in a constructor overload that accepts `string unsecureConfig`:

```csharp
[Entity("account", Configuration = "{\"endpoint\":\"https://api.example.com\"}")]
public class AccountPostCreatePlugin : IPlugin
{
    private readonly string _endpoint;

    public AccountPostCreatePlugin(string unsecureConfig)
    {
        _endpoint = JsonSerializer.Deserialize<Config>(unsecureConfig)!.Endpoint;
    }
}
```

> **Do not store secrets in `Configuration`.** The value is visible in source control and the solution XML. Use environment variables or Azure Key Vault for anything sensitive.

Use `DeleteJobOnSuccess` on async post-operation steps to keep the system job queue clean. Every async step execution creates an `AsyncOperation` record; without this flag those records accumulate indefinitely. Setting it on a synchronous step has no effect — Flowline will emit a warning during `flowline push`:

```csharp
[Entity("cr07982_invoice", DeleteJobOnSuccess = true)]
[Filter("cr07982_status")]
public class InvoicePostUpdateAsyncPlugin : IPlugin { ... }
```

> `DeleteJobOnSuccess` only applies to asynchronous (post-operation) steps. Flowline warns if you set it on a synchronous step.

### `[Filter]` — optional, strongly recommended on Update steps

Limits the step to fire only when at least one of the listed columns is included in the operation. Dataverse evaluates the filter before invoking your plugin — a filtered step that doesn't match costs almost nothing.

Without `[Filter]`, an Update step fires on **every** update to the table, regardless of which columns changed.

```csharp
[Entity("account")]
[Filter("name", "creditlimit")]
public class AccountPreUpdatePlugin : IPlugin { ... }
```

Use `nameof` with early-bound classes for compile-time safety:

```csharp
[Filter(nameof(Account.name), nameof(Account.creditlimit))]
```

> `[Filter]` only applies to Update steps. Using it on Create, Delete, or any other message is an error — Flowline will throw during `flowline push`.

### `[SecondaryEntity]` — required for Associate / Disassociate

Scopes the step to a specific secondary table. Use `"none"` to fire on any table.

```csharp
// Fires when a contact is associated with any record type
[Entity("contact")]
[SecondaryEntity("none")]
public class ContactPreAssociatePlugin : IPlugin { ... }

// Fires only when a contact is associated with an account
[Entity("contact")]
[SecondaryEntity("account")]
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

Omitting `[SecondaryEntity]` on an Associate or Disassociate step produces a warning during `flowline push`. Using it on any other message (Create, Update, Delete, ...) is an error.

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
[Entity("account")]
[Filter("name", "creditlimit")]
[PreImage("name", "creditlimit")]
[PostImage("name", "creditlimit")]
public class AccountPostUpdatePlugin : IPlugin
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

### Examples

**Minimal — reject an operation before anything is written:**

```csharp
[Entity("account")]
[Filter("creditlimit")]
public class AccountValidationUpdatePlugin : IPlugin
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
[Entity("account")]
public class AccountPreCreatePlugin : IPlugin
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
[Entity("cr07982_invoice", DeleteJobOnSuccess = true)]
[Filter("cr07982_status")]
public class InvoicePostUpdateAsyncPlugin : IPlugin
{
    public void Execute(IServiceProvider sp)
    {
        // Runs after the transaction commits — safe to call external APIs here.
        // A failure does not roll back the record.
        // DeleteJobOnSuccess = true keeps the AsyncOperation job queue clean.
    }
}
```

### Lifecycle

Flowline treats the DLL as the source of truth. On every `flowline push`:

- **Plugin types** — created for every public `IPlugin` or `CodeActivity` class; deleted when the class is removed.
- **Steps** — created or updated for every class with `[Entity]`; deleted when `[Entity]` is removed or the class is deleted.

Steps created by Flowline are stamped with `[flowline]` in their description, visible in Plugin Registration Tool.

**Disabling a step without deleting it:** remove `[Entity]` — Flowline deletes the step but keeps the plugin type registered.

**`--save` flag:** suppresses all deletions for that run and prints each skipped item — useful as a dry run:

```bash
flowline push MySolution --save
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

Use `EntityCollection` for **entity collection binding**:

```csharp
[CustomApi(EntityCollection = "invoice")]
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

### `[Input]` and `[Output]`

Declare parameters on the class. These are registration declarations only — Flowline registers them in Dataverse; you read and write the values yourself in `Execute`.

```csharp
[CustomApi]
[Input("accountId",      FieldType.EntityReference, Entity = "account")]
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
| `Entity` | `string?` | `null` | Required when type is `EntityReference` or `Entity`. |
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

The `--save` flag suppresses deletions the same way it does for steps.

---

## Why one class per step

Each plugin class registers exactly one step. This constraint pays dividends:

**Focused `Execute` bodies.** Without the rule, `Execute` needs branching logic to handle different messages. With it, every `Execute` does one thing — Dataverse guarantees which step fired because only one is registered.

**Self-describing logs.** When a plugin throws, Dataverse logs the class name. `AccountPostCreatePlugin` tells you exactly what happened; `AccountPlugin` does not.

**Unambiguous attributes.** `[Filter]`, `[PreImage]`, and `[PostImage]` always belong to exactly one step — the one the class registers. No need to associate them with a particular step in a multi-step registration.

**Shared logic via base classes.** The rule does not mean duplicating code. Put shared logic in a base class and declare one leaf class per step:

```csharp
public abstract class AccountSavePlugin : IPlugin
{
    public void Execute(IServiceProvider sp) { /* shared logic */ }
}

[Entity("account")]
public class AccountPreCreatePlugin : AccountSavePlugin { }

[Entity("account")]
public class AccountPreUpdatePlugin : AccountSavePlugin { }

// Same pattern for multiple entities:
[Entity("contact")]
public class ContactPreCreatePlugin : AccountSavePlugin { }
```

---

## One assembly for everything

Flowline supports `IPlugin` classes, `CodeActivity` workflow activities, and `[CustomApi]` classes all in the same assembly. One `Extensions` project, one `Extensions.dll`, one `flowline push`.

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
