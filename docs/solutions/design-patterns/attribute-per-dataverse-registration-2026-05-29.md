---
title: "Separate Attribute = Separate Dataverse Registration"
date: 2026-05-29
last_updated: 2026-05-29
category: docs/solutions/design-patterns/
module: flowline
problem_type: design_pattern
component: tooling
severity: medium
applies_when:
  - Adding a new C# attribute to represent a Dataverse registration concept
  - Deciding whether a new property belongs on StepAttribute or deserves its own attribute class
  - Evaluating whether to add SecureConfig support to Flowline
symptoms:
  - Unclear whether a Dataverse concept is a behavioral modifier or a separate registration entity
  - Temptation to create a separate attribute for concepts that are just fields on the step record
  - Migration pressure from Spkl features that turn out to be no-ops
tags:
  - dataverse
  - plugin-registration
  - csharp-attributes
  - step-attribute
  - handles-attribute
  - secure-config
  - spkl-migration
  - api-design
---

# Separate Attribute = Separate Dataverse Registration

## Context

Flowline reads C# attributes on plugin classes to sync step registrations to Dataverse. As the attribute surface grows, a recurring design question emerges: should new data live on `StepAttribute` as a named property, or become its own attribute? Getting this wrong creates a misleading API — separate attributes that imply a distinct Dataverse entity when they're just a field, or properties that hide categorically different registrations.

Two concrete decisions settled the governing rule:
1. `SecondaryTable` — initially created as a separate `SecondaryTableAttribute`, later merged into `StepAttribute` as a named property after recognising it's a field on `sdkmessageprocessingstep`, not a separate entity
2. `[SecureConfig]` — considered as a new attribute, rejected entirely

## Guidance

**Rule: separate attribute = separate Dataverse registration.**

If the data requires creating an additional Dataverse entity during registration, it must be a separate attribute. If it only modifies a field on an otherwise-normal step (or creates no additional entity), it belongs on `StepAttribute`.

Properties that belong on `StepAttribute` (fields on `sdkmessageprocessingstep`, one Dataverse record):

```csharp
// Convention-based: class name drives message + stage
[Step("account")]
public class AccountPreCreatePlugin : IPlugin { ... }

// With SecondaryTable — a field on the step record, stays on [Step]
[Step("contact", SecondaryTable = "account")]
public class AccountContactPreAssociatePlugin : IPlugin { ... }

// With behavioral modifiers
[Step("account", Order = 2, Config = "endpoint=https://...", DeleteJobOnSuccess = false)]
public class AccountPreCreatePlugin : IPlugin { ... }
```

`PreImageAttribute` and `PostImageAttribute` must be separate attributes — each creates a separate `sdkmessageprocessingstepimage` record in Dataverse:

```csharp
[Step("account")]
[PreImage("name", "telephone1")]
[PostImage]
public class AccountPreUpdatePlugin : IPlugin { ... }
```

`HandlesAttribute` is a separate attribute not because it creates a separate entity, but because it is an explicit **opt-in escape hatch** — it overrides the naming convention and signals "this class does not follow convention." It belongs on a separate attribute precisely so it stands out visually as a deviation:

```csharp
// Brownfield class that can't be renamed
[Step("account")]
[Handles(Message.Update, Stage.PreOperation)]
public class AccountPlugin : IPlugin { ... }
```

**Do not add `[SecureConfig]`.** It is intentionally excluded — see Why This Matters.

## Why This Matters

**Attribute shape should mirror Dataverse entity shape** for registration concepts, with escape hatches handled by opt-in attributes that signal deviation:

| Attribute | Dataverse entity / role |
|---|---|
| `[Step]` | `sdkmessageprocessingstep` — one record, all step fields including `SecondaryTable` |
| `[PreImage]` / `[PostImage]` | `sdkmessageprocessingstepimage` (one record each) |
| `[Handles]` | No separate entity — explicit opt-in escape hatch for non-convention class names |

`SecondaryTable` belongs on `[Step]` because it IS a field on `sdkmessageprocessingstep` (via the message filter). Creating a separate `[SecondaryTable]` attribute would imply it's a separate Dataverse entity — which misleads readers. It's a modifier, not a registration.

**Why SecureConfig is excluded:**

SecureConfig was considered to ease migration from Spkl, which declares a `SecureConfiguration` property on its step attribute. Investigation of Spkl's source code revealed it **never deploys SecureConfig to Dataverse** — `PluginRegistraton.cs` only writes `UnSecureConfiguration`:

```csharp
// Spkl PluginRegistraton.cs line 449 — only UnSecureConfiguration is deployed:
step.Configuration = pluginStep.UnSecureConfiguration;
// SecureConfiguration is declared on the attribute but never read by the deployer
```

Spkl users have no live SecureConfig in their environments. The migration argument is void.

Dataverse SecureConfig is also a poor fit for modern ALM:
- Not encrypted — "secure" means RBAC only (System Admins can read via API)
- Not exported in solutions — values must be set manually per environment
- Stored in a separate `sdkmessageprocessingstepsecureconfig` entity

The modern replacement is **Dataverse Environment Variables** (since ~2020): they travel with solutions, support per-environment "current value" overrides, and require no manual deployment tooling.

## When to Apply

When deciding whether to extend `StepAttribute` or create a new attribute:

- Does this data create a separate Dataverse entity during registration? → Separate attribute.
- Does this data only modify a field on the step record? → Property on `StepAttribute`.
- Is this a convention-override or opt-in escape hatch that should be visually distinct? → Separate attribute.
- Does this involve SecureConfig or Spkl SecureConfiguration? → Do not add it. Redirect to Dataverse Environment Variables.

## Examples

**Step-level fields — property on `StepAttribute`:**

```csharp
// SecondaryTable is a field on sdkmessageprocessingstep — belongs on [Step]
[Step("contact", SecondaryTable = "account")]
public class AccountContactPreAssociatePlugin : IPlugin { ... }

// Other behavioral modifiers also on [Step]
[Step("account", Order = 2, Config = "{\"timeout\":30}", DeleteJobOnSuccess = false)]
public class AccountPreCreatePlugin : IPlugin { ... }
```

**Separate Dataverse image records — must be separate attributes:**

```csharp
// CORRECT: each attribute = one sdkmessageprocessingstepimage record
[Step("account")]
[PreImage("name", "telephone1", "emailaddress1")]
[PostImage]
public class AccountPreUpdatePlugin : IPlugin { ... }
```

**Convention override — separate attribute by design:**

```csharp
// [Handles] is separate because it's an escape hatch, not a Dataverse entity
[Step("account")]
[Handles(Message.Update, Stage.PreOperation)]
public class AccountPlugin : IPlugin { ... }

// Prefer renaming the class so [Handles] can be removed:
[Step("account")]
public class AccountPreUpdatePlugin : IPlugin { ... }
```

**SecureConfig — do not add; use Dataverse Environment Variables:**

```csharp
// WRONG: don't add this attribute
[Step("account")]
[SecureConfig("apiKey=abc123")]
public class AccountPreCreatePlugin : IPlugin { ... }

// CORRECT: use a Dataverse Environment Variable, retrieved in plugin execute
public class AccountPreCreatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider)
    {
        var orgService = (IOrganizationService)serviceProvider
            .GetService(typeof(IOrganizationService));
        // retrieve env var via environmentvariabledefinition / environmentvariablevalue entities
    }
}
```

## Related

- `src/Flowline.Attributes/StepAttribute.cs`
- `src/Flowline.Attributes/HandlesAttribute.cs`
- `src/Flowline.Attributes/PreImageAttribute.cs`
- Spkl reference: `E:\Code\SparkleXrm\spkl\SparkleXrm.Tasks\PluginRegistraton.cs` line 449
