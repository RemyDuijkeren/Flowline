# Flowline.Attributes

Source-only NuGet package that provides attributes for registering Dataverse plugin steps with the Flowline CLI.

When you run `flowline push`, Flowline inspects your plugin assembly and automatically creates or updates the plugin step registrations in Dataverse — no Plugin Registration Tool needed.

## Installation

```xml
<PackageReference Include="Flowline.Attributes" Version="1.0.0" PrivateAssets="all" />
```

`PrivateAssets="all"` keeps this as a development-time dependency. The attributes compile directly into your plugin assembly — no extra DLL is added to the output, which keeps the Dataverse sandbox happy.

## How it works

Flowline detects a registerable plugin step by combining two things:

1. **Class name convention** — encodes the message, stage, and processing mode
2. **`[Entity]` attribute** — specifies the Dataverse entity (table) logical name

The naming pattern is:

```
{DescriptiveName}{Stage}{Message}[Async][Plugin]
```

The `Plugin` suffix is recommended but not required. Flowline strips it before scanning for
keywords, so `AccountPreUpdate` and `AccountPreUpdatePlugin` are detected identically. The suffix
helps readability in the IDE and in Plugin Registration Tool.

| Class name segment | Maps to | Value |
|---|---|---|
| `Validation` | `ProcessingStage.PreValidation` | 10 |
| `Pre` | `ProcessingStage.PreOperation` | 20 |
| `Post` | `ProcessingStage.PostOperation` | 40 |
| `Create`, `Update`, `Delete`, ... | `MessageName` | — |
| `Async` (before `Plugin` suffix) | `ProcessingMode.Asynchronous` | — |
| _(absent)_ | `ProcessingMode.Synchronous` | — |

If no stage keyword or no `[Entity]` attribute is found, Flowline skips the class.

## Attributes

### `[Entity]` — required

Specifies the Dataverse entity logical name. Without this attribute, Flowline ignores the class for step registration.

```csharp
[Entity("account")]
public class AccountPostCreatePlugin : IPlugin { ... }

[Entity("cr07982_invoice")]
public class InvoicePreUpdatePlugin : IPlugin { ... }
```

Optional named properties:

| Property | Type | Default | Maps to |
|---|---|---|---|
| `Order` | `int` | `1` | Execution Order in Plugin Registration Tool |
| `As` | `ExecuteAs` | `CallingUser` | Run in User's Context |
| `Configuration` | `string?` | `null` | Unsecure Configuration |

**`Order`** — controls ordering when multiple plugins are registered on the same step. Lower numbers run first.

**`(Execute)As`** — controls `context.UserId` inside `Execute`. Use `InitiatingUser` when a Flow or workflow triggers your plugin and you need the human user's context rather than the service account that owns the automation:

```csharp
[Entity("account", As = ExecuteAs.InitiatingUser)]
public class AccountPostCreatePlugin : IPlugin
{
    public void Execute(IServiceProvider sp)
    {
        var context = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
        // context.UserId is the human user who triggered the flow, not the flow service account
    }
}
```

**`Configuration`** — a plain string passed to your plugin constructor as the first parameter. Use it to supply endpoint URLs, feature flags, or serialized JSON config without hardcoding them:

```csharp
[Entity("account", Configuration = "{\"endpoint\":\"https://my-service.example.com\"}")]
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

> **Secure Configuration is intentionally not supported.** Secrets should not be committed to
> source code. Use environment variables or Azure Key Vault instead.

Full example with all properties:

```csharp
[Entity("account",
    Order = 2,
    As = ExecuteAs.InitiatingUser,
    Configuration = "{\"endpoint\":\"https://my-service.example.com\"}")]
public class AccountPreUpdatePlugin : IPlugin { ... }
```

### `[Filter]` — optional

Limits the step to fire only when at least one of the listed attributes is included in the
operation. Omit to fire on every change.

```csharp
[Entity("account")]
[Filter("name", "telephone1")]
public class AccountPreUpdatePlugin : IPlugin { ... }
```

Use `nameof` with your early-bound generated context class for refactor-safe names:

```csharp
[Entity("account")]
[Filter(nameof(Account.name), nameof(Account.telephone1))]
public class AccountPreUpdatePlugin : IPlugin { ... }
```

### `[PreImage]`, `[PostImage]` — optional

Registers an image snapshot on the step. Use `[PreImage]` for a pre-image and `[PostImage]` for a
post-image. Stack both on the same class when you need both.

```csharp
[Entity("account")]
[PreImage]
public class AccountPreUpdatePlugin : IPlugin { ... }

[Entity("account")]
[PreImage("name", "telephone1")]
public class AccountPreUpdatePlugin : IPlugin { ... }
```

Use `nameof` with your early-bound generated context class for refactor-safe names:

```csharp
[Entity("account")]
[PreImage(nameof(Account.name), nameof(Account.telephone1))]
public class AccountPreUpdatePlugin : IPlugin { ... }
```

Retrieve the image in plugin code using the fixed aliases:

```csharp
var preImage = context.PreEntityImages["preimage"];
var postImage = context.PostEntityImages["postimage"];
```

> **Advanced:** `Alias` can be overridden as a named property. The display name shown in Plugin
> Registration Tool is always derived from Alias. Use this when migrating a solution that was
> registered manually with a custom alias.
>
> ```csharp
> [PreImage(Alias = "legacy_pre")]
> // retrieve with: context.PreEntityImages["legacy_pre"]
> ```

## Examples

### Minimal — standard entity, no filter, no image

```csharp
using Flowline.Attributes;
using Microsoft.Xrm.Sdk;

[Entity("account")]
public class AccountPostCreatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider)
    {
        // your logic here
    }
}
```

### PreValidation — validate before the operation commits

```csharp
[Entity("account")]
[Filter("creditlimit")]
public class AccountValidationUpdatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider)
    {
        var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
        var target = (Entity)context.InputParameters["Target"];

        if (target.Contains("creditlimit") && target.GetAttributeValue<Money>("creditlimit").Value > 100_000)
            throw new InvalidPluginExecutionException("Credit limit cannot exceed 100,000.");
    }
}
```

### PostOperation async — fire after the record is saved

```csharp
[Entity("cr07982_invoice")]
[Filter("cr07982_status")]
public class InvoicePostUpdateAsyncPlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider)
    {
        // runs asynchronously after the update commits
    }
}
```

### PostOperation with pre-image and post-image

```csharp
[Entity("account")]
[Filter("name", "telephone1")]
[PreImage("name", "telephone1")]
[PostImage("name", "telephone1")]
public class AccountPostUpdatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider)
    {
        var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
        var preImage = context.PreEntityImages["preimage"];
        var postImage = context.PostEntityImages["postimage"];

        var oldName = preImage.GetAttributeValue<string>("name");
        var newName = postImage.GetAttributeValue<string>("name");

        if (oldName != newName)
        {
            // name changed — react here
        }
    }
}
```

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
