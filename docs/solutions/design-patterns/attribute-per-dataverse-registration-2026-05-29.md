---
title: "Separate Attribute = Separate Dataverse Registration"
date: 2026-05-29
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
  - Temptation to add a property to StepAttribute for concepts that change registration shape
  - Migration pressure from Spkl features that turn out to be no-ops
tags:
  - dataverse
  - plugin-registration
  - csharp-attributes
  - step-attribute
  - secondary-table
  - secure-config
  - spkl-migration
  - api-design
---

# Separate Attribute = Separate Dataverse Registration

## Context

Flowline reads C# attributes on plugin classes to sync step registrations to Dataverse. As the attribute surface grows, a recurring design question emerges: should new data live on `StepAttribute` as a named property, or become its own attribute? Getting this wrong creates a misleading API — attributes that imply one Dataverse entity when two are involved, or properties that imply modifying behavior when they actually change registration shape entirely.

Two concrete decisions settled the governing rule:
1. `SecondaryTableAttribute` — debated as a StepAttribute property, kept separate
2. `[SecureConfig]` — considered as a new attribute, rejected entirely

## Guidance

**Rule: separate attribute = separate Dataverse registration.**

If adding data changes how many Dataverse entities are involved in the registration, it must be a separate attribute. If it only modifies behavior on an otherwise-normal step, it belongs on `StepAttribute`.

Properties that belong on `StepAttribute` (behavioral modifiers — one Dataverse record, different behavior):

```csharp
[Step("account", Message.Create, Stage.PreOperation,
    Order = 1,
    RunAs = "service-user",
    Config = "endpoint=https://...",
    DeleteJobOnSuccess = true)]
public class MyPlugin : IPlugin { ... }
```

`SecondaryTableAttribute` must be a separate attribute — it changes the registration from a single-entity step to a two-entity step (Associate/Disassociate):

```csharp
[Step("account", Message.Associate, Stage.PostOperation)]
[SecondaryTable("contact")]
public class AccountContactPlugin : IPlugin { ... }
```

`PreImageAttribute` and `PostImageAttribute` must be separate attributes — each maps to a separate `sdkmessageprocessingstepimage` record:

```csharp
[Step("account", Message.Update, Stage.PreOperation)]
[PreImage("name", "telephone1")]
[PostImage]
public class AccountUpdatePlugin : IPlugin { ... }
```

**Do not add `[SecureConfig]`.** It is intentionally excluded — see Why This Matters.

## Why This Matters

**Attribute shape maps to Dataverse entity shape.** The existing pattern is not arbitrary:

| Attribute | Dataverse entity |
|---|---|
| `[Step]` | `sdkmessageprocessingstep` |
| `[PreImage]` / `[PostImage]` | `sdkmessageprocessingstepimage` (one record each) |
| `[SecondaryTable]` | Two-entity form of `sdkmessageprocessingstep` |

Merging `SecondaryTable` into `StepAttribute` as a named property hides the fact that this is a categorically different registration. A developer reading `[Step("account", ..., SecondaryTable = "contact")]` would not know they are configuring an Associate/Disassociate step — the two-table nature is invisible.

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

- Does this data change how many Dataverse records are created during registration? → Separate attribute.
- Does this data only change behavior or configuration of an otherwise-normal step? → Property on `StepAttribute`.
- Does this involve SecureConfig or Spkl SecureConfiguration? → Do not add it. Redirect to Dataverse Environment Variables.

## Examples

**Behavioral modifier — property on `StepAttribute`:**

```csharp
// CORRECT: RunAs, Config, Order, DeleteJobOnSuccess are behavior modifiers
[Step("account", Message.Create, Stage.PreOperation,
    RunAs = "integration-user",
    Config = "timeout=30",
    DeleteJobOnSuccess = true,
    Order = 2)]
public class AccountCreatePlugin : IPlugin { ... }
```

**Registration shape change — must be a separate attribute:**

```csharp
// CORRECT: SecondaryTable changes the step to a two-entity registration
[Step("account", Message.Associate, Stage.PostOperation)]
[SecondaryTable("contact")]
public class AccountContactAssociatePlugin : IPlugin { ... }

// WRONG: hides the two-entity nature, misleads the reader
[Step("account", Message.Associate, Stage.PostOperation, SecondaryTable = "contact")]
public class AccountContactAssociatePlugin : IPlugin { ... }
```

**Separate Dataverse image records — must be separate attributes:**

```csharp
// CORRECT: each attribute = one sdkmessageprocessingstepimage record
[Step("account", Message.Update, Stage.PreOperation)]
[PreImage("name", "telephone1", "emailaddress1")]
[PostImage]
public class AccountUpdatePlugin : IPlugin { ... }
```

**SecureConfig — do not add; use Dataverse Environment Variables:**

```csharp
// WRONG: don't add this attribute
[Step("account", Message.Create, Stage.PreOperation)]
[SecureConfig("apiKey=abc123")]
public class MyPlugin : IPlugin { ... }

// CORRECT: use a Dataverse Environment Variable, retrieved in plugin execute
public class MyPlugin : IPlugin
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
- `src/Flowline.Attributes/SecondaryTableAttribute.cs`
- `src/Flowline.Attributes/PreImageAttribute.cs`
- Spkl reference: `E:\Code\SparkleXrm\spkl\SparkleXrm.Tasks\PluginRegistraton.cs` line 449
