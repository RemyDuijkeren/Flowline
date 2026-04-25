# Plugin Step Registration — Design Brainstorm

How should plugin developers declare their Dataverse plugin steps so that Flowline CLI can
detect and register them automatically?

---

## Approach 1 — Attributes on the class (current direction)

```csharp
[Step(MessageName.Update, "account", ProcessingStage.PreOperation, ProcessingMode.Synchronous)]
[Step(MessageName.Create, "account", ProcessingStage.PostOperation)]
public class AccountPlugin : IPlugin { ... }
```

**Pros:**
- Explicit — no magic, the step config is exactly where you declared it
- Multiple steps per class, arbitrary combinations
- IDE-discoverable: hover `[Step]` and see the full signature
- Already implemented in Flowline today (`StepAttribute` exists)
- Handles edge cases cleanly: custom messages, filtering attributes, configuration string

**Cons:**
- Verbose — even simple single-step plugins need an attribute block
- Ints for Stage/Mode are error-prone (current implementation uses raw `int`, not the enums in `PluginEnums.cs`)
- Plugin devs need to reference the Flowline.Core assembly just to get the attributes

---

## Approach 2 — Convention: method naming (ASP.NET MVC style)

```csharp
public class AccountPlugin : IPlugin
{
    public void PreValidation_account_Update(IServiceProvider sp) { }   // stage=10, sync
    public void PostAsync_account_Create(IServiceProvider sp) { }       // stage=40, async
}
```

**Pros:**
- Zero dependencies — no attribute assembly needed
- Self-documenting method names
- Works with any reflection without custom attributes

**Cons:**
- `IPlugin.Execute` is a single method — Dataverse doesn't call named methods. You'd have to
  dispatch internally, which means plugin devs write infrastructure code, not just business logic
- Naming gets ugly fast: `PreOperation_account_Update_FilteredByName_Async` is unreadable
- Fragile — a typo in the method name silently produces no step registration
- Doesn't compose: filtering attributes or configuration strings can't be encoded in a name
- Breaks the `IPlugin` contract that Dataverse expects

---

## Approach 3 — Convention: class naming

```csharp
public class PreValidation_Account_Update : IPlugin { }
public class PostAsync_Account_Create : IPlugin { }
```

**Pros:**
- Zero dependencies
- Class name is the step descriptor — one class = one step (clean separation)
- Easy to parse with reflection: split on `_`

**Cons:**
- One step per class forced — some plugins logically handle Create+Update in one class
- Long ugly class names
- Impossible to express filtering attributes, order, configuration
- One typo = silently wrong registration

---

## Approach 4 — Hybrid: typed attributes using the enums already in PluginEnums.cs ✅ Recommended

Upgrade `StepAttribute` to use enums instead of raw `int`:

```csharp
[Step(MessageName.Update, "account", ProcessingStage.PreOperation, ProcessingMode.Synchronous,
      FilteringAttributes = "name,telephone1")]
[Step(MessageName.Create, "account")]  // defaults: PostOperation, Synchronous
public class AccountPlugin : IPlugin { }
```

**Pros:**
- Takes what already exists and makes it type-safe and readable
- `MessageName` enum prevents typos on message names
- `ProcessingStage` / `ProcessingMode` are self-documenting
- Minimal change — enum swap only
- Sensible defaults (`PostOperation`, `Synchronous`) reduce boilerplate for the common case

**Cons:**
- Still requires referencing the Flowline.Core (or a thin `Flowline.Attributes`) assembly
- Attribute model has limits on expression (no lambdas, no complex objects)

---

## Approach 5 — Interface-based fluent registration (escape hatch for complex cases)

```csharp
public class AccountPlugin : IPlugin, IPluginSteps
{
    public static IEnumerable<StepDefinition> Steps =>
    [
        Step.On(MessageName.Update, "account").PreOperation().Sync().Filter("name", "telephone1"),
        Step.On(MessageName.Create, "account").PostOperation().Async()
    ];
}
```

**Pros:**
- Fluent, composable, fully type-safe — no magic strings anywhere
- IDE autocomplete guides the developer
- Complex config (filtering, images, order) is naturally expressible
- Static property — reflection reads it at deploy time without instantiating the plugin
- No attribute expression limitations

**Cons:**
- More complex for Flowline to detect (interface check + static property reflection)
- Plugin devs must implement `IPluginSteps` — adds coupling
- Slightly more code per plugin than attributes

---

## Verdict

**Approach 4 (typed attributes) for 80% of cases, Approach 5 (fluent interface) as an escape hatch.**

The attribute model covers every real plugin step cleanly once the enums replace the raw ints.
The fluent interface is available when someone needs filtering attributes, images, or configuration
strings that become unwieldy in attribute syntax.

Convention-based naming (Approaches 2 & 3) falls apart because `IPlugin.Execute` is a single
entry point — you can't encode enough information in a name without sacrificing readability or
expressiveness, and a typo silently misfires.

**Immediate fix regardless of chosen path:** replace `int Stage` and `int Mode` in `StepAttribute`
with `ProcessingStage` and `ProcessingMode` — those enums already exist in `PluginEnums.cs` and
are currently unused by `StepAttribute`.

---

## Applying the "1 Plugin = 1 Step" Rule

A Microsoft product manager guideline states: one `IPlugin` class should handle exactly one plugin
step, even if Dataverse technically allows multiple steps on the same assembly type.

**Why this makes sense:**
- Clearer telemetry and error logs — you know exactly which class fired
- Simpler `Execute` body — no internal `if (context.MessageName == "Update")` branching
- Easier unit testing — one class, one responsibility
- Matches how Plugin Registration Tool displays plugin types to admins

### Impact on each approach

**Approach 2 (method naming)** — still dead. `IPlugin.Execute` is one method regardless of the rule.

**Approach 3 (class naming)** — becomes significantly more viable. The "one step per class forced"
con disappears entirely. The class *is* the step, so encoding stage, entity, and message in the
name is natural rather than a limitation. The remaining hard limit: filtering attributes, images,
and configuration strings cannot be expressed in a class name.

**Approach 4 (typed attribute)** — becomes cleaner. You go from potentially stacking multiple
`[Step]` attributes to always having exactly one per class:

```csharp
[Step(MessageName.Update, "account", ProcessingStage.PreOperation)]
public class AccountUpdatePlugin : IPlugin { }
```

One attribute, one class, one step. Minimal noise, full expressiveness.

**Approach 5 (fluent interface)** — also benefits. The `Steps` collection always has one entry,
so the fluent chain reads as a single declaration rather than a list.

### New preferred design: Convention-first, attribute as override

The "1 plugin = 1 step" rule opens up a hybrid that wasn't practical before:

```csharp
// Convention path — zero dependencies, Flowline infers step from class name
public class Account_Update_PreOperation : IPlugin { }

// Attribute path — full control when you need filtering, images, or config
[Step(MessageName.Update, "account", ProcessingStage.PreOperation,
      FilteringAttributes = "name,telephone1")]
public class AccountUpdatePlugin : IPlugin { }
```

Flowline detection logic:
1. Does the class have a `[Step]` attribute? → use it (full control)
2. Does the class name match `{Entity}_{Message}_{Stage}`? → infer from convention
3. Neither → not a registerable step, skip

This mirrors ASP.NET routing: convention by default, attribute to override. Plugin devs with
simple steps get zero-dependency projects; devs with complex filtering or images reach for
the attribute.

**Tradeoff to be aware of:** class names are visible to admins in Plugin Registration Tool and
in Dataverse solution explorer. `Account_Update_PreOperation` is descriptive but not idiomatic
C#. Teams that care about naming aesthetics will prefer the attribute path.

### Updated verdict under "1 plugin = 1 step"

**Convention (class naming) as the default, `[Step]` attribute as the escape hatch** — the
reverse of the original verdict. The rule eliminates the main weakness of Approach 3 and makes
zero-dependency plugin projects the happy path. The attribute stays for anything that needs
filtering attributes, images, custom configuration, or non-standard class names.

---

## Approach A vs Approach C — Final Comparison

Two realistic candidates after applying the "1 plugin = 1 step" rule.

### Approach A — Monolithic `[Step]` attribute

```csharp
[Step(MessageName.Update, "account", ProcessingStage.PreOperation, ProcessingMode.Synchronous,
      FilteringAttributes = "name,telephone1")]
public class AccountUpdatePlugin : IPlugin { }
```

Everything for one step lives in one attribute. No convention, no inference from the class name.

### Approach C — Convention + focused attributes ✅ Recommended

Class name encodes message, stage, and async mode. `[Entity]` provides the logical name (required
for custom tables with publisher prefixes). `[Filter]` and `[Image]` are optional and always
belong unambiguously to the single step.

```csharp
// Pure convention — zero attributes needed for standard entities
public class AccountUpdatePreOperationPlugin : IPlugin { }

// Custom entity
[Entity("cr07982_invoice")]
public class InvoiceUpdatePreOperationPlugin : IPlugin { }

// With filtering
[Entity("cr07982_invoice")]
[Filter("cr07982_amount", "cr07982_status")]
public class InvoiceUpdatePreOperationPlugin : IPlugin { }

// With filtering + image — image unambiguously belongs to this one step
[Entity("cr07982_invoice")]
[Filter("cr07982_amount")]
[Image("Pre Image", "preimage", ImageType.PreImage, "cr07982_amount")]
public class InvoiceUpdatePreOperationPlugin : IPlugin { }

// Multiple entities, same logic — separate classes, shared base
public abstract class OpportunityCreatePostOperationPlugin : IPlugin
{
    public void Execute(IServiceProvider sp) { /* shared logic */ }
}

[Entity("account")]
public class OpportunityCreatePostOperationForAccountPlugin : OpportunityCreatePostOperationPlugin { }

[Entity("contact")]
public class OpportunityCreatePostOperationForContactPlugin : OpportunityCreatePostOperationPlugin { }
```

### Side-by-side

| | A — Monolithic `[Step]` | C — Convention + focused attrs |
|---|---|---|
| **Message + stage declared in** | `[Step]` attribute | Class name |
| **Entity declared in** | `[Step]` attribute | `[Entity]` attribute (or class name for standard entities) |
| **Per-step filtering** | `FilteringAttributes` on `[Step]` | `[Filter]` — always unambiguous |
| **Image ownership** | Ambiguous if multiple steps | Always unambiguous — only one step |
| **Multiple entities, same logic** | Stack `[Step]` on one class | Separate classes with shared base |
| **Rename class = broken registration** | No | Yes — class name is part of the contract |
| **Verbosity (simple case)** | High — attribute required always | Lowest — zero attributes for standard entities |
| **Verbosity (complex case)** | Medium — one attribute, named params | Medium — more classes for multi-entity |
| **Familiar to plugin devs** | Yes — matches other libs | No — Flowline-specific convention |
| **Aligns with Microsoft "1 plugin = 1 step"** | No — allows stacking | Yes — enforced by design |
| **Flowline detection complexity** | Low | Low |
| **Testability** | Medium | Best — one class, one responsibility |

### Verdict

**Approach C** is the better fit for Flowline. It produces the least boilerplate for the common
case, eliminates all image and filter ambiguity, and enforces the Microsoft "1 plugin = 1 step"
guideline by design rather than convention.

The main trade-off — class name is part of the registration contract — is acceptable: plugin
developers already treat class names as meaningful identifiers (they appear in Plugin Registration
Tool and Dataverse logs), so renaming without updating the registration is already a conscious act.

Approach A remains the familiar choice for teams coming from other plugin frameworks, but it
requires more typing for every step and leaves image-to-step correlation implicit.

---

## Approach C — Refined Specification

### Rules

1. **`[Entity]` is always required.** No entity inference from the class name. Every registerable
   plugin class must carry an `[Entity("logicalname")]` attribute — this makes the registration
   contract explicit and handles custom tables with publisher prefixes naturally.

2. **Class name encodes message + stage + mode** using short, readable keywords:

   | Keyword in class name | Maps to | Stage value |
   |---|---|---|
   | `Validate` or `Validation` | `ProcessingStage.PreValidation` | 10 |
   | `Pre` | `ProcessingStage.PreOperation` | 20 |
   | `Post` | `ProcessingStage.PostOperation` | 40 |

   Message is detected by scanning for a known `MessageName` value (`Create`, `Update`, `Delete`,
   `Retrieve`, etc.) anywhere in the class name.

3. **Processing mode defaults to Synchronous.** Append `Async` at the end of the class name
   (before `Plugin` suffix if present) to mark the step as Asynchronous.

4. **`[Filter]` and `[Image]` are optional** and always belong unambiguously to the single step.

### Naming pattern

```
{DescriptiveName}{Stage}{Message}[Async][Plugin]
```

Stage is a qualifier *of* the message — `PreUpdate`, `PostCreate`, `ValidateDelete` are compound
words where stage modifies message, just like "pre-flight" or "post-match". They belong together.

The `Plugin` suffix is **recommended but not required**. Flowline strips it before scanning for
keywords, so `AccountPreUpdate` and `AccountPreUpdatePlugin` are detected identically. The suffix
is useful for readability in the IDE and in Plugin Registration Tool — it makes the class
immediately recognisable as a Dataverse plugin rather than a domain model or service class.

### Examples

```csharp
// PreOperation, Sync
[Entity("account")]
public class AccountPreUpdatePlugin : IPlugin { }

// PostOperation, Sync
[Entity("account")]
public class AccountPostCreatePlugin : IPlugin { }

// PreValidation, Sync
[Entity("account")]
public class AccountValidateUpdatePlugin : IPlugin { }

// PostOperation, Async
[Entity("account")]
public class AccountPostUpdateAsyncPlugin : IPlugin { }

// Custom entity, PreOperation, Sync, with filtering
[Entity("cr07982_invoice")]
[Filter("cr07982_amount", "cr07982_status")]
public class InvoicePreUpdatePlugin : IPlugin { }

// Custom entity, PostOperation, Async, with pre-image
[Entity("cr07982_invoice")]
[Image("Pre Image", "preimage", ImageType.PreImage, "cr07982_amount")]
public class InvoicePostUpdateAsyncPlugin : IPlugin { }

// Multiple entities, same logic — shared base, one class per entity
public abstract class OpportunityPostCreatePlugin : IPlugin
{
    public void Execute(IServiceProvider sp) { /* shared logic */ }
}

[Entity("account")]
public class OpportunityPostCreateForAccountPlugin : OpportunityPostCreatePlugin { }

[Entity("contact")]
public class OpportunityPostCreateForContactPlugin : OpportunityPostCreatePlugin { }
```

### Flowline detection algorithm

```
For each class implementing IPlugin:
  1. Has [Entity] attribute?         → no  = skip (not a registerable step)
  2. Find MessageName in class name  → no match = skip
  3. Find stage keyword in class name:
       "Validate" or "Validation"    → PreValidation (10)
       "Pre"                         → PreOperation (20)
       "Post"                        → PostOperation (40)
       no match                      → skip
  4. Class name ends with "Async"
     (before "Plugin" suffix)?       → Asynchronous, else Synchronous
  5. Read [Filter] if present        → FilteringAttributes on the step
  6. Read [Image] if present         → image registration on the step
```

---

## Developer Experience — Will "1 Plugin = 1 Step" Irritate Developers?

### Where it won't irritate anyone

The simple case — one entity, one message, one stage — produces *less* code than the traditional
approach. No attribute at all for standard entities, just a well-named class. Developers will
appreciate that.

### Where it will irritate

Both cases that cause friction resolve with the **same base class pattern** — one solution for
all multi-step scenarios.

**Same logic, multiple entities** — like Microsoft's `OpportunityCreate` firing on 5 entities.
Put the shared logic in a base class, declare one leaf class per entity:

```csharp
public abstract class OpportunityPostCreatePlugin : IPlugin
{
    public void Execute(IServiceProvider sp) { /* shared logic */ }
}

[Entity("account")]
public class OpportunityPostCreateForAccountPlugin : OpportunityPostCreatePlugin { }

[Entity("contact")]
public class OpportunityPostCreateForContactPlugin : OpportunityPostCreatePlugin { }

[Entity("opportunity")]
public class OpportunityPostCreateForOpportunityPlugin : OpportunityPostCreatePlugin { }
```

**Same logic, multiple messages (Create + Update)** — developers who previously branched on
`context.MessageName` inside one `Execute` body use the same pattern:

```csharp
public abstract class AccountSavePlugin : IPlugin
{
    public void Execute(IServiceProvider sp) { /* shared Create + Update logic */ }
}

[Entity("account")]
public class AccountPreCreatePlugin : AccountSavePlugin { }

[Entity("account")]
public class AccountPreUpdatePlugin : AccountSavePlugin { }
```

Both cases are identical in structure — **one solution for all multi-step scenarios**. This
simplifies the mental model: "need the same logic on multiple steps? Use a base class."

### Why it is not a big problem

- Flowline is an opinionated tool — developers choosing it accept that it has a point of view,
  the same way Rails or ASP.NET MVC impose conventions
- The Microsoft PM guideline provides cover — "this is Microsoft's recommendation, not just ours"
- The benefits are tangible and visible: cleaner telemetry, separate unit tests per step,
  no ambiguous image ownership
- The base class pattern is idiomatic C# — it's not a workaround, it's good OOP

### The bigger irritant: the class name contract

Renaming a class silently breaks its step registration until the next `flowline push`. A developer
who renames `AccountPreUpdatePlugin` to `AccountUpdateValidatorPlugin` for clarity will not get
a compiler error — the old step remains registered under the old name.

Mitigation: a clear warning during `flowline push` ("class renamed since last registration,
step will be re-registered") prevents silent misfires.

### Further mitigation: scaffolding

If `flowline new plugin` generates the boilerplate class from a prompt, the 1 plugin = 1 step
rule stops feeling like a constraint and starts feeling like a feature.

---

## Proposed Attribute Code

`StepAttribute` and the raw-int `ImageAttribute` from `PluginModels.cs` are replaced by three
focused, type-safe attributes. `StepAttribute` is removed entirely — the convention handles what
it did, and stacking multiple registrations on one class is no longer supported.

### `EntityAttribute` — required, one per class

```csharp
/// Specifies the Dataverse entity (table) logical name this plugin step fires on.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class EntityAttribute : Attribute
{
    public string LogicalName { get; }
    public int ExecutionOrder { get; set; } = 1;
    public ExecuteAs ExecuteAs { get; set; } = ExecuteAs.CallingUser;
    public string? UnsecureConfiguration { get; set; }

    public EntityAttribute(string logicalName) => LogicalName = logicalName;
}
```

- `AllowMultiple = false` enforces 1 plugin = 1 step at the attribute level
- `logicalName` is the Dataverse logical name: `"account"`, `"cr07982_invoice"`, etc.
- `ExecutionOrder` — controls ordering when multiple plugins fire on the same step (default 1)
- `ExecuteAs` — maps to "Run in User's Context" in Plugin Registration Tool (default `CallingUser`)
- `UnsecureConfiguration` — plain string passed to the plugin constructor; Secure Configuration is intentionally omitted — it should not be committed to source code

### `ExecuteAs` enum

```csharp
public enum ExecuteAs
{
    CallingUser = 0,
    InitiatingUser = 1
}
```

Renamed from the ambiguous `User`/`Initiating` in `PluginEnums.cs` for clarity. Moved to
`Flowline.Attributes` so plugin developers can use it without referencing `Flowline.Core`.

### `FilterAttribute` — optional, one per class

```csharp
/// Specifies which attributes trigger this step. Only fires when at least one listed
/// attribute is included in the operation. Omit to fire on all attribute changes.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class FilterAttribute : Attribute
{
    public string[] Attributes { get; }

    public FilterAttribute(params string[] attributes) => Attributes = attributes;
}
```

- `params string[]` gives a clean call site: `[Filter("name", "telephone1")]`
- `AllowMultiple = false` — one filter set per step

### `ImageAttribute` — optional, multiple per class

```csharp
/// Registers a pre- or post-image snapshot on the step.
/// Stack multiple times to register both a PreImage and a PostImage.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class ImageAttribute : Attribute
{
    public string Name { get; }
    public string Alias { get; }
    public ImageType ImageType { get; }
    public string[] Attributes { get; }

    public ImageAttribute(string name, string alias,
        ImageType imageType = ImageType.PostImage, params string[] attributes)
    {
        Name = name;
        Alias = alias;
        ImageType = imageType;
        Attributes = attributes;
    }
}

public enum ImageType
{
    PreImage = 0,
    PostImage = 1,
    Both = 2
}
```

- `AllowMultiple = true` — a step can have both a PreImage and a PostImage
- `params string[]` for attributes: `[Image("Pre Image", "preimage", ImageType.PreImage, "name", "telephone1")]`
- Omit `attributes` to snapshot all fields: `[Image("Pre Image", "preimage", ImageType.PreImage)]`
- Default `imageType` is `PostImage` — most common snapshot type

### Design decision: `params string[]` over `string?` for attribute lists

The original `ImageAttribute` used `string? Attributes` — a comma-separated string set via a
named property. `FilterAttribute` and the new `ImageAttribute` use `params string[]` instead.

| | `string? Attributes = "name,telephone1"` | `params string[] attributes` |
|---|---|---|
| **Parsing in Flowline** | Must split on comma, trim whitespace | Values already discrete — no parsing |
| **Type safety** | Comma convention invisible to compiler | Each name is a typed argument |
| **Typo detection** | Silent — wrong delimiter passes | N/A — no delimiter |
| **`nameof` support** | No — string concatenation required | Yes — refactor-safe |
| **Omit = all attributes** | `null` | Empty array `[]` |

The `nameof` support is the most valuable benefit for plugin developers. Attribute logical names
in Dataverse often mirror C# property names in early-bound generated classes. Using `nameof`
makes the filter and image configuration refactor-safe — renaming a property in the generated
context class shows as a compile error rather than a silent registration mismatch:

```csharp
// Fragile — "name" is a magic string, rename in generated class goes undetected
[Filter("name", "telephone1")]

// Refactor-safe — rename Account.Name in generated context → compiler error here too
[Filter(nameof(Account.name), nameof(Account.telephone1))]
[Image("Pre Image", "preimage", ImageType.PreImage, nameof(Account.name))]
```

Flowline's `AssemblyAnalysisService` reads both forms identically via `MetadataLoadContext` —
`nameof` resolves to a string at compile time, so the IL contains plain strings either way.

### Full usage example

```csharp
// Minimal — standard entity, no filter, no image
[Entity("account")]
public class AccountPreUpdatePlugin : IPlugin { ... }

// Custom entity with filtering
[Entity("cr07982_invoice")]
[Filter("cr07982_amount", "cr07982_status")]
public class InvoicePreUpdatePlugin : IPlugin { ... }

// Full — custom entity, filter, pre-image and post-image
[Entity("cr07982_invoice")]
[Filter("cr07982_amount")]
[Image("Pre Image", "preimage", ImageType.PreImage, "cr07982_amount")]
[Image("Post Image", "postimage", ImageType.PostImage, "cr07982_amount")]
public class InvoicePostUpdateAsyncPlugin : IPlugin { ... }
```

### What changes in `PluginModels.cs`

| Current | Change |
|---|---|
| `StepAttribute` | **Remove** — replaced by class name convention |
| `ImageAttribute` (raw `int ImageType`) | **Replace** with typed version above |
| `IsolationMode` enum | Keep as-is |
| `PluginAssemblyMetadata` record | Keep as-is |
| `PluginTypeMetadata` record | Keep as-is |
| `PluginStepMetadata` record | Keep — internal model, populated by `AssemblyAnalysisService` |
| `PluginImageMetadata` record | Keep — update `ImageType` field from `int` to `ImageType` enum |

---

## Distributing Attributes to Plugin Developers

The attributes need to reach plugin developers without pulling in Flowline's heavy dependencies
(`Microsoft.PowerPlatform.Dataverse.Client`, etc.) and without introducing extra DLLs into the
Dataverse plugin sandbox.

### Options

| | Option | Runtime DLL | Versioned | Sandbox-safe |
|---|---|---|---|---|
| 1 | Reference `Flowline.Core` | Yes — pulls everything | Yes | No — sandbox rejects extra DLLs |
| 2 | Copy source files manually | No | No | Yes |
| 3 | Standard NuGet (`Flowline.Attributes.dll`) | Yes — extra DLL | Yes | No — needs ILMerge or separate registration |
| 4 | Source-only NuGet (`Flowline.Attributes`) | No — compiles into plugin | Yes | Yes |

### Why the Dataverse sandbox matters

Dataverse executes plugins in an isolated sandbox with strict assembly loading rules. Any DLL
that isn't the plugin assembly itself must be explicitly registered as a dependent assembly in
Dataverse — or merged into the plugin DLL using ILMerge/ILRepack. A stray `Flowline.Attributes.dll`
in the output folder will cause a sandbox load failure at runtime.

### Recommended: Option 4 — Source-only NuGet package

A source-only NuGet package delivers `.cs` source files instead of a compiled DLL. The attributes
compile directly into the plugin assembly as if the developer wrote them locally — no extra DLL,
no sandbox issues, no ILMerge needed.

**Package:** `Flowline.Attributes`
**Contents:** `EntityAttribute.cs`, `FilterAttribute.cs`, `ImageAttribute.cs`, `ImageType.cs`
**Dependencies:** none
**Target framework:** `netstandard2.0` — compatible with both .NET Framework 4.6.2 (required by
Dataverse plugins) and modern .NET

Plugin developers add one line to their `.csproj`:

```xml
<PackageReference Include="Flowline.Attributes" Version="1.0.0" PrivateAssets="all" />
```

`PrivateAssets="all"` marks this as a development-time dependency — it won't propagate to
consumers of the plugin package, which is correct since the attributes compile into the assembly.

The result: the plugin assembly is fully self-contained, the attributes appear in the plugin's
own namespace, and Flowline CLI reads them via `MetadataLoadContext` at deploy time — exactly
as it does today with `StepAttribute`.
