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
| `Validate` or `Validation` | `ProcessingStage.PreValidation` | 10 |
| `Pre` | `ProcessingStage.PreOperation` | 20 |
| `Post` | `ProcessingStage.PostOperation` | 40 |
| `Create`, `Update`, `Delete`, ... | `MessageName` | — |
| `Async` (before `Plugin` suffix) | `ProcessingMode.Asynchronous` | — |
| _(absent)_ | `ProcessingMode.Synchronous` | — |

If no stage keyword or no `[Entity]` attribute is found, Flowline skips the class.

## The "1 plugin = 1 step" rule

Each plugin class registers exactly one step. This is an intentional design decision enforced
by Flowline. Here is why it makes your life easier in the long run.

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

**Unambiguous image and filter ownership**
When a class registers multiple steps, `[Filter]` and `[Image]` become ambiguous — which step do
they belong to? With one class per step, every attribute on the class unambiguously belongs to
that single step.

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
public abstract class AccountSavePlugin : IPlugin
{
    public void Execute(IServiceProvider sp) { /* shared logic */ }
}

[Entity("account")]
public class AccountPreCreatePlugin : AccountSavePlugin { }

[Entity("account")]
public class AccountPreUpdatePlugin : AccountSavePlugin { }
```

## Attributes

### `[Entity]` — required

Specifies the Dataverse entity logical name. Without this attribute Flowline ignores the class.

```csharp
[Entity("account")]
public class AccountPostCreatePlugin : IPlugin { ... }

[Entity("cr07982_invoice")]
public class InvoicePreUpdatePlugin : IPlugin { ... }
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

### `[Image]` — optional, stackable

Registers a pre- or post-image snapshot on the step. Stack multiple times for both.

```csharp
[Entity("account")]
[Image("Pre Image", "preimage", ImageType.PreImage)]   // all attributes
public class AccountPreUpdatePlugin : IPlugin { ... }

[Entity("account")]
[Image("Pre Image", "preimage", ImageType.PreImage, "name", "telephone1")]  // specific attributes
public class AccountPreUpdatePlugin : IPlugin { ... }
```

Use `nameof` here too:

```csharp
[Entity("account")]
[Image("Pre Image", "preimage", ImageType.PreImage, nameof(Account.name))]
public class AccountPreUpdatePlugin : IPlugin { ... }
```

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
public class AccountValidateUpdatePlugin : IPlugin
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
[Image("Pre Image", "preimage", ImageType.PreImage, "name", "telephone1")]
[Image("Post Image", "postimage", ImageType.PostImage, "name", "telephone1")]
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

### Same logic, multiple entities

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
