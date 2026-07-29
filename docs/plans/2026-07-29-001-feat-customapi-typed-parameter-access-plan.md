---
title: Custom API Typed Parameter Access - Plan
type: feat
date: 2026-07-29
topic: customapi-typed-parameter-access
artifact_contract: ce-unified-plan/v1
artifact_readiness: requirements-only
product_contract_source: ce-brainstorm
execution: code
---

# Custom API Typed Parameter Access - Plan

## Goal Capsule

- **Objective:** Give Custom API authors typed, per-invocation access to their declared `[Input]`/`[Output]` parameters — generated from the attributes that already exist — so `Execute` bodies stop casting out of `ctx.InputParameters` by string key.
- **Authority hierarchy:** This plan's Requirements and Key Decisions govern the approach.
- **Stop conditions:** None identified.
- **Scope boundary:** Custom APIs only. Plugin steps (`Target`, `PreImage`, `PostImage`) are deliberately out of scope; the vocabulary is designed so they can be added later without renaming anything.
- **Milestone:** Post-v1. Not in the 2026-08-01 v1.0 milestone (`STRATEGY.md:109`).
- **Tail ownership:** Implementer commits locally. No push, no PR unless separately requested.

---

## Product Contract

### Summary

Writing a Custom API today means reading loosely-typed values out of `context.InputParameters` by string key and casting each one by hand, then writing results back to `context.OutputParameters` the same way. The parameter's name and type are already declared on the class via `[Input]`/`[Output]`, so every `Execute` body restates information the attributes already carry.

This plan adds a generated, per-invocation **accessor**: one line, `var api = Bind(sp);`, then `api.AccountId` and `api.OrderTotal` to read and `api.DiscountAmount = x` to write. A Roslyn source generator emits one accessor type per `[CustomApi]` class directly from the attribute declarations; a small set of hand-written runtime helpers, shipped as source in the existing package, carries the casts, the optional handling, and the error messages. A companion analyzer validates the attribute declarations themselves at build time.

Registration is untouched. `[Input]`/`[Output]` remain the sole source of truth for what `flowline push` writes to Dataverse; the generator only reads them.

### Problem Frame

Three distinct costs land on every Custom API author, all of them visible in the package's own documented example (`src/Flowline.Attributes/README.md:326-346`):

1. **Casting noise.** `(EntityReference)ctx.InputParameters["accountId"]` — no IntelliSense, and a wrong cast is an `InvalidCastException` in a Dataverse sandbox trace.
2. **Duplicated magic strings.** `"accountId"` is written in the attribute and again in the body. Rename one and the other rots silently; nothing links them.
3. **The optional dance.** `ctx.InputParameters.Contains("x") && (bool)ctx.InputParameters["x"]` on every optional parameter. Omit the `Contains` and it is a `KeyNotFoundException` in production.

The information needed to remove all three is already declared. `[Input("orderTotal", FieldType.Money)]` states the name, the type, and (via `IsOptional`) whether it can be absent. `FieldType` maps one-to-one onto C# types — `src/Flowline.Attributes/FieldType.cs:8-23` documents the table, and `src/Flowline.Core/Plugins/PluginTypeMetadataScanner.cs:32-48` holds the same mapping in the reverse direction as a `FieldTypeMap` dictionary that is currently referenced nowhere in the codebase.

Two platform constraints bound the solution.

**Plugin instances are cached and reused.** Microsoft is explicit: *"the platform caches a class instance and re-uses it... never store any service instance or context data as a property in your class"* ([Write a plug-in](https://learn.microsoft.com/power-apps/developer/data-platform/write-plug-in#iplugin-interface)). Typed access therefore cannot be exposed as properties on the plugin class holding a context field — that is a data-bleed bug across concurrent invocations. It must live on an object created per invocation.

**The package ships source, not an assembly.** `Flowline.Attributes` sets `IncludeBuildOutput=false` and ships its `.cs` files as `contentFiles` with `BuildAction=Compile` (`src/Flowline.Attributes/Flowline.Attributes.csproj:11,31-35`), so nothing extra is uploaded to the sandbox. A source generator does not violate this: analyzers ship under `analyzers/dotnet/cs/`, are loaded into the compiler, and never appear in the plugin project's `net462/publish/` output — which is what `push` uploads (`src/Flowline/Commands/PushCommand.cs:647`).

### Requirements

**Accessor shape**

- R1. A `[CustomApi]` class gains a generated way to obtain a typed accessor for the current invocation from its `IServiceProvider`, in one statement.
- R2. The accessor is created per invocation and is never stored on the plugin instance, satisfying the platform's stateless-plugin rule.
- R2a. The `IPluginExecutionContext` is held by the accessor, not by the plugin class. Generation adds no instance field, property, or other mutable state to the plugin type itself. R1–R5 are otherwise satisfiable by an implementation that caches the context on the plugin — the exact defect KD2 exists to prevent.
- R3. The accessor exposes one readable member per `[Input]` and one writable member per `[Output]`, typed according to the declared `FieldType`.
- R4. Member names are derived from the declared parameter name by the generator. The parameter string appears exactly once in user code — in the attribute — and is never retyped in `Execute`.
- R5. The accessor exposes the underlying `IPluginExecutionContext` as an escape hatch, so nothing the accessor does not model becomes unreachable.

**Optional parameters**

- R6. An `[Input]` with `IsOptional = true` is readable in a form that distinguishes "not supplied" from "supplied as the type's default value", without the caller writing a `Contains` check.
- R7. A required `[Input]` that the caller did not supply raises an error naming the parameter and the plugin class, rather than surfacing as a `KeyNotFoundException`. The API's prefixed unique name is not available at runtime — it is derived at push time from the class name plus the solution's publisher prefix (`src/Flowline.Core/Plugins/PluginTypeMetadataScanner.cs:146-164`) and never written into the assembly. Use the class name, or `context.MessageName`, for API identity in the message.

**Runtime checks**

- R8. Reading a parameter whose supplied value does not match the declared `FieldType` raises an error naming the parameter, the declared type, and the type actually received.
- R9. Runtime error messages follow `docs/tone-of-voice.md` and are actionable from a Dataverse sandbox trace alone — the author will not have a debugger attached.

**Generation and packaging**

- R10. The generator runs in the consumer's compilation and produces no artifact that reaches the Dataverse sandbox; `Flowline.Attributes` remains a source-only, development-time dependency.
- R11. Generated code compiles under C# 7.3, matching the `LangVersion` the package pins for consumers (`src/Flowline.Attributes/Flowline.Attributes.csproj:6`).
- R12. Consumers require no change to their existing `PackageReference` to receive the generator.
- R13. Plugin projects continue to target .NET Framework 4.6.2–4.8, Dataverse's supported range ([Supported customizations](https://learn.microsoft.com/power-apps/developer/data-platform/supported-customizations#support-for-net-framework-versions)).

**Declaration analyzer**

- R14. Attribute declaration errors are reported as build diagnostics: `FieldType.EntityReference` or `FieldType.Entity` without `Table`, duplicate parameter names within one API, and `[Input]`/`[Output]` on a class without `[CustomApi]`.
- R15. Of the three R14 checks, only `[Input]`/`[Output]` without `[CustomApi]` has a push-time counterpart today — `ValidateCustomApiAttributesOnStep` (`src/Flowline.Core/Plugins/PluginTypeMetadataScanner.cs:561-572`). That one must report the same verdict at build time as at push time. The other two are **new** validations with no existing equivalent: `ReadClassLevelParameters` (`:201-224`) appends parameters without checking for duplicates, and nothing anywhere requires `Table` on an `EntityReference` parameter.

**Registration unchanged**

- R16. `flowline push` behavior is byte-for-byte unchanged. The scanner reads the same `[Input]`/`[Output]` attributes it reads today; generated types do not appear as plugin types or Custom APIs in any registration output.

### Non-Goals

- **Plugin steps.** Typed `Target`, `PreImage`, and `PostImage` access. The accessor vocabulary is chosen to extend to steps later, but no step-side member is built here.
- **Typed image columns.** `[PreImage]`/`[PostImage]` declare column names but no types (`src/Flowline.Attributes/PreImageAttribute.cs`), so typed columns would require cross-referencing early-bound classes from `flowline generate`. Out.
- **Request/Response class pairs.** Considered and rejected: two generated types plus a binding step plus a property-name convention, for nothing the single accessor does not already provide.
- **Flowline owning `Execute`.** The package promises *"You write `Execute` exactly as normal"* (`src/Flowline.Attributes/README.md:275`). Generating the entry point would make Flowline a plugin framework rather than a registration tool. Rejected as a strategy change, not an ergonomics one.
- **POCO-driven registration.** Deriving `[Input]`/`[Output]` from a request class's properties. Rejected at intake; attributes stay authoritative.
- **Typed callers.** Generating typed request wrappers for *invoking* deployed Custom APIs from other plugins. Genuinely useful, plausibly a `flowline generate` feature, separate work.
- **Optional outputs.** `OutputAttribute` has no `IsOptional` (`src/Flowline.Attributes/OutputAttribute.cs:37-82`, documented at `README.md:361`). Not added here.

### Key Decisions

- **KD1. `[Input]`/`[Output]` stay the single source of truth for registration.** The accessor is a generated read/write face over the declarations, never an input to them. Keeps `Flowline.Core` out of this change entirely. *(session-settled: chosen at intake)*
- **KD2. Typed access lives on a per-invocation accessor, not on the plugin class.** Forced by the platform's documented instance-caching behavior. This is the decision that ruled out putting a property per parameter directly on the plugin, which was otherwise the most attractive shape.
- **KD3. Parameter-name → member-name is a generator-owned one-way derivation.** Because the generator owns both ends, the two cannot drift. This is what removes the need for any convention the author must remember, and it is why an analyzer checking hand-written classes against attributes is unnecessary.
- **KD4. The analyzer validates declarations, not correspondence.** With KD3 in place there is no correspondence left to check. The analyzer's value is moving existing push-time declaration checks to build time.
- **KD5. Two layers: hand-written runtime helpers, generated members on top.** Helpers own casting, optional handling, and error text; the generator owns names and types. Neither reimplements the other. The layering is for separation of concerns, not for staged delivery — KD8 commits to shipping both together.
- **KD6. The generator ships inside the existing `Flowline.Attributes` package** under `analyzers/dotnet/cs/`, alongside the existing `contentFiles`. One package, no consumer change, nothing new in the sandbox.
- **KD7. `LangVersion` stays 7.3 and the `net462` floor is retained.** `net481` sits outside Dataverse's supported 4.6.2–4.8 range, and every .NET Framework version defaults to C# 7.3 regardless ([C# language versioning](https://learn.microsoft.com/dotnet/csharp/language-reference/language-versioning#defaults)) — so raising the TFM buys nothing here. Shipped source is additionally bounded by each consumer's own `LangVersion`, not by the package's TFM.
- **KD8. Post-v1.** v1.0 lands 2026-08-01 (`STRATEGY.md:109`) and this is not in its scope. Chosen as one coherent delivery rather than splitting the helper layer into v1.0.

### Acceptance Examples

**A1 — Global API, required and optional inputs**

```csharp
[CustomApi]
[Input("accountId", FieldType.EntityReference, Table = "account")]
[Input("includeHistory", FieldType.Boolean, IsOptional = true)]
[Output("riskScore", FieldType.Integer)]
public class GetAccountRiskApi : IPlugin
{
    public void Execute(IServiceProvider sp)
    {
        var api = Bind(sp);
        api.RiskScore = ComputeScore(api.AccountId, api.IncludeHistory);
    }
}
```

`api.AccountId` is an `EntityReference`. `api.IncludeHistory` distinguishes omitted from `false`. The strings `"accountId"`, `"includeHistory"`, `"riskScore"` appear only in the attributes.

**A2 — Required input omitted by the caller.** The API is invoked without `accountId`. Reading `api.AccountId` raises an error naming the parameter and the plugin class (per R7 — the prefixed unique name is not available at runtime). It is not a `KeyNotFoundException`, and it is not `null` flowing silently into business logic.

**A3 — Registration is unaffected.** Adding the accessor to a class that already pushes cleanly produces an identical `flowline push` plan — no new Custom API, no new plugin type, no parameter diff.

**A4 — Declaration error caught at build.** `[Input("owner", FieldType.EntityReference)]` with no `Table` fails the build with a diagnostic, rather than reaching the push-time check in `PluginTypeMetadataScanner`.

### Open Questions

- **Q1. Naming.** The verb (`Bind`) and the accessor type name. Constraint: both must still read naturally when step support adds `Target`, `PreImage`, and `PostImage` members. `GetRequest`/`SetResponse` was the originating sketch and was set aside because "request/response" has no meaning for a step.
- **Q2. `Target` on entity-bound APIs.** Dataverse injects a `Target` parameter for entity- and collection-bound APIs (`src/Flowline.Attributes/CustomApiAttribute.cs:33-36,48-51`) that no `[Input]` declares. Does the accessor surface it, and how does it behave if an author declares their own parameter that derives the same member name?
- **Q3. Tooling floor.** Minimum Roslyn/MSBuild/VS version for the generator, and the failure mode on older tooling. Dataverse plugin developers on `net462` are disproportionately likely to be on older toolchains, so this is an adoption risk, not a footnote.
- **Q4. Output member accessibility.** Write-only, or read-write against `OutputParameters`?
- **Q5. Member-name collisions within one accessor.** Cross-class collision is already precluded — accessors are nested per plugin class, which is also what makes R16 hold (`IsPublic` is false for nested types, so the scanner's filter skips them). The open risk is narrower: two parameter names deriving to the same member name, or a derived name colliding with something else the generator emits on the same accessor (the R5 context escape hatch, the Q2 `Target` member).

### Related Observations

Not in scope; recorded because they surfaced during this brainstorm.

- **`CustomApiAttribute`'s positional parameter is `table`**, but reaching for `[CustomApi("new_CalculateDiscount")]` to name the API is a natural first instinct. The unique name is the `UniqueName` named property, with publisher-prefix validation (`src/Flowline.Attributes/CustomApiAttribute.cs:67-77,86-112`). Whether the constructor's shape is right is its own change.
- **Property-level parameter declaration existed once and was removed, 2026-05-02.** Commit `d3a4a6a` ("Remove property-level parameter handling in `AssemblyAnalysisService`") deleted a path that inferred a parameter's `FieldType` from the C# type of a property on the plugin class, via a `FieldTypeMap` lookup on `propType.FullName`. This is worth knowing before planning: the shape it removed is adjacent to the accessor being proposed here, and whatever motivated that removal may bear on KD2 and KD3. The orphaned `FieldTypeMap` dictionary survived that deletion and the later file split (`9cc60da`); it was removed 2026-07-29 after confirming zero references repo-wide. Mapping today is not inferred at all — the author states `FieldType` explicitly in the attribute and `PluginTypeMetadataScanner.cs:215` reads the enum ordinal straight through.
- **XML doc drift, fixed 2026-07-29.** `CustomApiAttribute`'s examples used `partial class` and a `FlowlineExecute` entry point that does not satisfy `IPlugin`, contradicting `README.md`. Corrected to `class`/`Execute`, along with two `cref`s in `FieldType.cs` pointing at a non-existent `InputAttribute.Entity`/`OutputAttribute.Entity` (the property is `Table`). If the accessor design later requires `partial`, these examples change again.
