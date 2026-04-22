# Flowline.Attributes

Source-only NuGet package that provides attributes for registering Dataverse plugin steps and Custom APIs with the Flowline CLI.

When you run `flowline push`, Flowline inspects your plugin assembly and automatically creates or updates all registrations in Dataverse — no Plugin Registration Tool needed.

## Installation

```xml
<PackageReference Include="Flowline.Attributes" Version="1.0.0" PrivateAssets="all" />
```

`PrivateAssets="all"` keeps this as a development-time dependency. The attributes compile directly into your plugin assembly — no extra DLL is added to the output, which keeps the Dataverse sandbox happy.

## How plugin steps work

A **plugin step** is code that Dataverse calls automatically when a specific operation (message)
happens on a table. Flowline registers the step from your class name and attributes — you write
the `IPlugin` class, Flowline handles the Dataverse registration.

### The pipeline

Every Dataverse operation passes through a pipeline with four points where your plugin can run:

| Stage keyword | When it runs | In transaction? | Use for |
|---|---|---|---|
| `Validation` | Before the transaction opens | No | Validation that blocks the operation — throw here to reject cleanly with no rollback |
| `Pre` | Before the record is saved | Yes | Enriching or correcting the incoming data — changes to `Target` are included in the save automatically |
| `Post` (synchronous) | After the record is saved | Yes | Follow-up writes that must be atomic with the triggering operation |
| `Post` + `Async` | After the transaction closes, in the background | No | Notifications, external API calls, long-running work — the user's operation completes first |

### Class naming

Flowline reads the message, stage, and processing mode directly from the class name. The pattern is:

```
{DescriptiveName}{Stage}{Message}[Async][Plugin]
```

The `Plugin` suffix is optional but recommended for readability. Flowline strips it before scanning.

| Class name | Message | Stage | Mode |
|---|---|---|---|
| `AccountPostCreatePlugin` | Create | PostOperation | Synchronous |
| `InvoicePreUpdatePlugin` | Update | PreOperation | Synchronous |
| `ContactValidationDeletePlugin` | Delete | PreValidation | Synchronous |
| `OrderPostUpdateAsyncPlugin` | Update | PostOperation | Asynchronous |
| `AccountPreCreatePlugin` | Create | PreOperation | Synchronous |

Common messages: `Create`, `Update`, `Delete`, `Retrieve`, `RetrieveMultiple`, `Associate`,
`Disassociate`, `Assign`, `SetState`. The class name segment must match exactly (case-sensitive).

If no stage keyword or no `[Entity]` attribute is found, Flowline skips the class for plugin step registration.

## Attributes

### `[Entity]` — required

Specifies the table (entity) logical name. Without this attribute, Flowline ignores the class.

```csharp
[Entity("account")]
public class AccountPostCreatePlugin : IPlugin { ... }

[Entity("cr07982_invoice")]
public class InvoicePreUpdatePlugin : IPlugin { ... }
```

The logical name is always lowercase and found in the maker portal under **Table → Properties → Name**.

Optional named properties:

| Property | Type | Default | Description |
|---|---|---|---|
| `Order` | `int` | `1` | Execution order when multiple steps fire on the same event. Lower runs first. |
| `As` | `ExecuteAs` | `CallingUser` | Which user identity the plugin runs under (`context.UserId`). |
| `Configuration` | `string?` | `null` | String passed to the plugin constructor as `unsecureConfig`. |

**`Order`** — only relevant when multiple plugins are registered on the same message, stage, and table. Guaranteed ordering; lower numbers run first.

**`As`** — use `ExecuteAs.InitiatingUser` when a Power Automate flow triggers your plugin and you need the human user's identity (not the flow service account) for auditing or row-level security:

```csharp
[Entity("account", As = ExecuteAs.InitiatingUser)]
public class AccountPostCreatePlugin : IPlugin
{
    public void Execute(IServiceProvider sp)
    {
        var ctx = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
        // ctx.UserId is the human who triggered the flow, not the flow service account
    }
}
```

**`Configuration`** — pass endpoint URLs, feature flags, or JSON settings without hardcoding them. Receive the string in a constructor overload that accepts `string unsecureConfig`:

```csharp
[Entity("account", Configuration = "{\"endpoint\":\"https://api.example.com\"}")]
public class AccountPostCreatePlugin : IPlugin
{
    private readonly string _endpoint;

    public AccountPostCreatePlugin(string unsecureConfig)
    {
        _endpoint = JsonSerializer.Deserialize<Config>(unsecureConfig)!.Endpoint;
    }

    public void Execute(IServiceProvider sp) { ... }
}
```

> **Do not store secrets in `Configuration`.** The string is visible in source control and the
> solution XML. Use environment variables or Azure Key Vault for anything sensitive.

### `[Filter]` — optional, strongly recommended on Update steps

Limits the step to fire only when at least one of the listed columns is included in the operation.

Without `[Filter]`, an Update step fires on **every** update to the table — even when the columns
your plugin cares about haven't changed. Dataverse evaluates the filter before invoking your plugin,
so a filtered step that doesn't match costs almost nothing.

```csharp
[Entity("account")]
[Filter("name", "creditlimit")]
public class AccountPreUpdatePlugin : IPlugin { ... }
```

Use `nameof` with early-bound entity classes for compile-time safety:

```csharp
[Entity("account")]
[Filter(nameof(Account.name), nameof(Account.creditlimit))]
public class AccountPreUpdatePlugin : IPlugin { ... }
```

> `[Filter]` only applies to `Update` steps. On `Create` and `Delete`, all columns are always
> included and the filter has no effect.

### `[PreImage]` — optional

Registers a snapshot of the record's column values **before** the operation runs. Retrieve it
from `context.PreEntityImages["preimage"]` inside `Execute`.

**When it is available:**

| Message | Available? |
|---|---|
| Create | No — the record did not exist before this operation |
| Update | Yes — in any stage (PreValidation, PreOperation, PostOperation) |
| Delete | Yes — in any stage |

```csharp
[Entity("account")]
[Filter("name")]
[PreImage("name", "creditlimit")]   // list only the columns you need
public class AccountPreUpdatePlugin : IPlugin
{
    public void Execute(IServiceProvider sp)
    {
        var ctx      = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
        var preImage = ctx.PreEntityImages["preimage"];
        var oldName  = preImage.GetAttributeValue<string>("name");
    }
}
```

Omit the column arguments to include all columns — use this sparingly, as large images are expensive.

### `[PostImage]` — optional

Registers a snapshot of the record's column values **after** the operation completes. Retrieve it
from `context.PostEntityImages["postimage"]` inside `Execute`.

**When it is available:**

| Message | Available? |
|---|---|
| Create | Yes — PostOperation stage only (sync or async) |
| Update | Yes — PostOperation stage only (sync or async) |
| Delete | No — the record no longer exists after this operation |

> Post-images are **not available** in PreValidation or PreOperation stages — the operation has
> not completed yet at those points.

```csharp
[Entity("account")]
[Filter("name")]
[PreImage("name")]
[PostImage("name")]
public class AccountPostUpdatePlugin : IPlugin
{
    public void Execute(IServiceProvider sp)
    {
        var ctx       = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
        var preImage  = ctx.PreEntityImages["preimage"];
        var postImage = ctx.PostEntityImages["postimage"];
        var oldName   = preImage.GetAttributeValue<string>("name");
        var newName   = postImage.GetAttributeValue<string>("name");
        if (oldName != newName) { /* name changed */ }
    }
}
```

> **Advanced:** override `Alias` when migrating from a step that was manually registered with a
> custom image alias, so existing retrieval code does not break:
> ```csharp
> [PreImage(Alias = "legacy_pre")]
> // retrieve with: ctx.PreEntityImages["legacy_pre"]
> ```

### `[SecondaryEntity]` — required for Associate / Disassociate steps

Specifies the related table for `Associate` and `Disassociate` messages, which fire when a
many-to-many relationship between two records is created or removed.

```csharp
// Fires when a contact is associated with any record type
[Entity("contact")]
[SecondaryEntity("none")]
public class ContactAssociatePlugin : IPlugin { ... }

// Fires only when a contact is associated with an account
[Entity("contact")]
[SecondaryEntity("account")]
public class ContactAccountAssociatePlugin : IPlugin
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

For all other messages (Create, Update, Delete, ...) omit `[SecondaryEntity]` — Dataverse uses
`"none"` automatically.

## Step lifecycle

Flowline treats the DLL as the source of truth. On every `flowline push`:

- **Plugin types** — created for every public `IPlugin` or `CodeActivity` class; deleted when the class is removed from the DLL.
- **Steps** — created or updated for every class with `[Entity]`; deleted when the class loses `[Entity]` or is removed entirely.

Steps Flowline creates are stamped with `[flowline]` in their description, visible in Plugin Registration Tool.

### Disabling a plugin

Remove `[Entity]` from the class — Flowline deletes its steps on the next push, but keeps the plugin type registered. Delete the class entirely to remove both the type and its steps.

### The `--save` flag

Pass `--save` to suppress all deletions for that run. Flowline prints each skipped deletion, making it useful as a dry run before a clean push:

```bash
flowline push MySolution --save
```

## Examples

### Minimal — PostOperation Create

```csharp
using Flowline.Attributes;
using Microsoft.Xrm.Sdk;

[Entity("account")]
public class AccountPostCreatePlugin : IPlugin
{
    public void Execute(IServiceProvider sp)
    {
        var ctx    = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
        var target = (Entity)ctx.InputParameters["Target"];
        // target contains the newly created account record
    }
}
```

### PreValidation — reject the operation before anything is written

```csharp
[Entity("account")]
[Filter("creditlimit")]
public class AccountValidationUpdatePlugin : IPlugin
{
    public void Execute(IServiceProvider sp)
    {
        var ctx    = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
        var target = (Entity)ctx.InputParameters["Target"];

        if (target.Contains("creditlimit") &&
            target.GetAttributeValue<Money>("creditlimit").Value > 100_000)
            throw new InvalidPluginExecutionException("Credit limit cannot exceed 100,000.");
    }
}
```

### PreOperation — enrich the record before it is saved

```csharp
[Entity("account")]
public class AccountPreCreatePlugin : IPlugin
{
    public void Execute(IServiceProvider sp)
    {
        var ctx    = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
        var target = (Entity)ctx.InputParameters["Target"];
        // Set a column on Target — it will be included in the save automatically
        target["cr123_source"] = "web";
    }
}
```

### PostOperation async — notify an external system after the save

```csharp
[Entity("cr07982_invoice")]
[Filter("cr07982_status")]
public class InvoicePostUpdateAsyncPlugin : IPlugin
{
    public void Execute(IServiceProvider sp)
    {
        // Runs in the background after the transaction commits.
        // Safe to call external APIs here — a failure does not roll back the record.
    }
}
```

### PostOperation — compare old and new values with pre/post images

```csharp
[Entity("account")]
[Filter("name", "telephone1")]
[PreImage("name", "telephone1")]
[PostImage("name", "telephone1")]
public class AccountPostUpdatePlugin : IPlugin
{
    public void Execute(IServiceProvider sp)
    {
        var ctx       = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
        var preImage  = ctx.PreEntityImages["preimage"];
        var postImage = ctx.PostEntityImages["postimage"];

        var oldName = preImage.GetAttributeValue<string>("name");
        var newName = postImage.GetAttributeValue<string>("name");

        if (oldName != newName)
        {
            // name changed — react here
        }
    }
}
```

---

## Custom APIs

A **Custom API** is a custom endpoint you define in Dataverse. Unlike a regular plugin step, which fires automatically when a record is created, updated, or deleted, a Custom API is invoked explicitly by name — from Power Automate, a canvas app, a JavaScript web resource, an external system via the Web API, or another plugin. Think of it as writing a backend function that callers can call by name.

Add `[CustomApi]` to an `IPlugin` class to register it as a Custom API instead of a plugin step. You write your `Execute` method exactly as you always would — Flowline only handles the Dataverse registration when you run `flowline push`.

### Class naming

Flowline strips the `Api`, `CustomApi`, or `Plugin` suffix from the class name to derive the unique name, then prefixes it with the solution's publisher prefix:

| Class name | Unique name |
|---|---|
| `GetAccountRiskApi` | `cr123_GetAccountRisk` |
| `SendNotificationCustomApi` | `cr123_SendNotification` |
| `ApproveOrderPlugin` | `cr123_ApproveOrder` |

The publisher prefix is read automatically from the solution — no config needed.

### `[CustomApi]` — required

Marks the class as a Custom API. Without arguments, it registers a **global API** — one not tied to any specific table. Use this for operations like sending a notification or computing a value.

```csharp
[CustomApi]
public class SendNotificationApi : IPlugin { ... }
```

Pass a table logical name for **entity binding**. Dataverse automatically provides a `Target` `EntityReference` parameter containing the record the caller invoked the API on — you do not need to declare `Target` yourself.

```csharp
[CustomApi("salesorder")]
public class ApproveOrderApi : IPlugin
{
    public void Execute(IServiceProvider serviceProvider)
    {
        var ctx = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
        var orderId = ((EntityReference)ctx.InputParameters["Target"]).Id;
    }
}
```

Use `EntityCollection` for **entity collection binding** — when the API operates on a set of records:

```csharp
[CustomApi(EntityCollection = "invoice")]
public class BulkApproveApi : IPlugin { ... }
```

Optional named properties:

| Property | Type | Default | Description |
|---|---|---|---|
| `IsFunction` | `bool` | `false` | Function (HTTP GET, read-only) vs Action (HTTP POST). Use only for side-effect-free APIs. |
| `IsPrivate` | `bool` | `false` | Hides the API from the OData catalog. Use for internal APIs. |
| `AllowedStepType` | `AllowedStepType` | `None` | Whether third parties can register plugin steps on this API. |
| `DisplayName` | `string?` | class name split | Display name in the solution explorer. |
| `Description` | `string?` | `null` | Description in the solution explorer. |
| `ExecutePrivilege` | `string?` | `null` | Privilege name required to call this API. Omit to allow any authenticated user. |

### Declaring inputs and outputs

Use `[Input]` and `[Output]` on the class to declare which parameters Flowline should register in Dataverse. These are purely registration declarations — they tell Flowline the parameter name, type, and optionality. You read and write the actual values yourself in `Execute` via `context.InputParameters` and `context.OutputParameters`.

```csharp
[CustomApi]
[Input("accountId",      FieldType.EntityReference, Entity = "account")]
[Input("includeHistory", FieldType.Boolean, IsOptional = true)]
[Output("riskScore",     FieldType.Integer)]
[Output("riskLabel",     FieldType.String)]
public class GetAccountRiskApi : IPlugin
{
    public void Execute(IServiceProvider serviceProvider)
    {
        var ctx = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));

        var accountId   = (EntityReference)ctx.InputParameters["accountId"];
        var withHistory = ctx.InputParameters.Contains("includeHistory")
                          && (bool)ctx.InputParameters["includeHistory"];

        ctx.OutputParameters["riskScore"] = ComputeScore(accountId, withHistory);
        ctx.OutputParameters["riskLabel"] = "High";
    }
}
```

`[Input]` properties:

| Property | Type | Default | Notes |
|---|---|---|---|
| `Name` | `string` | — | Name used in `context.InputParameters`. Must be unique within the API. Convention: camelCase. |
| `Type` | `FieldType` | — | Dataverse field type. See type table below. |
| `IsOptional` | `bool` | `false` | When `true`, check `Contains("name")` before reading. |
| `Entity` | `string?` | `null` | Required when type is `EntityReference` or `Entity`. |
| `DisplayName` | `string?` | unique name split | Shown in solution explorer. |
| `Description` | `string?` | `null` | |

`[Output]` has the same properties except `IsOptional`.

**Optional parameters:** always check `context.InputParameters.Contains("name")` before reading, otherwise Dataverse throws if the caller omitted the value.

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

### Custom API lifecycle

Flowline treats the DLL as the source of truth for Custom APIs, the same as for steps.

- **Created** when a class with `[CustomApi]` appears that has no matching unique name in Dataverse.
- **Updated** when mutable fields change (`DisplayName`, `Description`, `IsPrivate`, `ExecutePrivilege`).
- **Deleted and recreated** when an immutable field changes (binding type, `IsFunction`, `AllowedStepType`, or a parameter's type or optionality). Flowline warns before doing this.
- **Deleted** when the class is removed from the DLL or `[CustomApi]` is removed.

The `--save` flag suppresses deletions for Custom APIs the same way it does for steps.

---

## The "1 plugin = 1 step" rule

Each plugin class registers exactly one plugin step. This is an intentional design decision enforced
by Flowline. It gives up some flexibility, but in return it simplifies the way how you can write plugins.
Here is why it makes your life easier in the long run.

**Simpler attributes**
When a class can register multiple steps, Flowline would have needed one attribute that encodes everything — entity, message, stage, mode, filter, images — for each step.
The result would be something like:

```csharp
[Step("account", "Update", ProcessStage.PreOperation, ProcessMode.Synchronous, "name,creditlimit")]
public class AccountPreUpdatePlugin : IPlugin { ... }
```

With one class per step, each concern gets its own focused attribute. There is no ambiguity about which step a `[Filter]` or `[Image]` belongs to — it always belongs to the one step the class registers.
We can also use the class name to specify the message, process stage, and process mode: the 'convention over configuration' principle.
This makes the attributes even more small and readable:

```csharp
[Entity("account")]
[Filter("name", "creditlimit")]
[PreImage("name", "creditlimit")]
public class AccountPreUpdatePlugin : IPlugin { ... }
```

It also allows you to write columns for filter and image directly in the attribute, without the need to pass a comma-seperated string like `"name,creditlimit"`.

**Clearer telemetry and error logs**
When a plugin throws, Dataverse logs the class name. If one class handles both `Create` and
`Update`, your logs say `AccountPlugin` — you still have to look at the context to know what
triggered it. With one class per step, the log says `AccountPostCreatePlugin` — the failure is
self-describing.

**Simpler `Execute` bodies**
Without the rule, `Execute` fills up with branching logic:
```csharp
public void Execute(IServiceProvider sp)
{
    var context = ...;
    if (context.MessageName == "Create") { ... }
    else if (context.MessageName == "Update") { ... }
}
```
With the rule, every `Execute` does exactly one thing. No branching, no defensive checks on
`MessageName` or `Stage` — Dataverse guarantees which step fired because you registered only one.

**Easier unit testing**
Testing one class per step means one test class per plugin. Each test has a clear arrange/act/assert
structure with no need to set up different `MessageName` values to hit different branches.

**Shared logic still works — use a base class**
The rule does not mean duplicating code. When the same logic applies to multiple steps, put it in
a base class and declare one leaf class per step:

```csharp
public abstract class AccountSavePlugin : IPlugin
{
    public void Execute(IServiceProvider sp) { /* shared logic */ }
}

[Entity("account")]
public class AccountPreCreate : AccountSavePlugin { }

[Entity("account")]
public class AccountPreUpdate : AccountSavePlugin { }
```

This is the same pattern for multiple messages (`Create` + `Update`) and for multiple entities
(same step firing on `account`, `contact`, and `opportunity`). One solution for all cases.

```csharp
public abstract class RelatedEntityPostCreatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider)
    {
        // shared logic for all entity registrations
    }
}

[Entity("account")]
public class RelatedEntityPostCreateForAccountPlugin : RelatedEntityPostCreatePlugin { }

[Entity("contact")]
public class RelatedEntityPostCreateForContactPlugin : RelatedEntityPostCreatePlugin { }

[Entity("opportunity")]
public class RelatedEntityPostCreateForOpportunityPlugin : RelatedEntityPostCreatePlugin { }
```
