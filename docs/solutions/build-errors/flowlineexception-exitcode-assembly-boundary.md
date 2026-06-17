---
title: "FlowlineException and ExitCode must live in Flowline.Core, not Flowline"
date: 2026-06-17
category: build-errors
module: Flowline.Core
problem_type: build_error
component: development_workflow
symptoms:
  - "CS0246: type 'FlowlineException' not found in Flowline.Core"
  - "CS0246: type 'ExitCode' not found in Flowline.Core"
root_cause: logic_error
resolution_type: code_fix
severity: high
tags:
  - assembly-boundary
  - circular-dependency
  - exception-handling
  - project-structure
related_components:
  - Flowline
---

# FlowlineException and ExitCode must live in Flowline.Core, not Flowline

## Problem

`DataverseConnector` (in `Flowline.Core`) needs to throw `FlowlineException` for precondition failures. Both `FlowlineException` and `ExitCode` were defined in the `Flowline` application project — which `Flowline.Core` cannot reference due to the one-way dependency constraint (`Flowline` → `Flowline.Core`). Result: CS0246 compile errors as soon as any `Flowline.Core` service uses these types.

## Symptoms

- `CS0246: The type or namespace name 'FlowlineException' could not be found` in any `Flowline.Core` file that adds `using Flowline;` and calls `throw new FlowlineException(...)`
- `CS0246: The type or namespace name 'ExitCode' could not be found` alongside the above
- Error only surfaces in `Flowline.Core` — the `Flowline` project itself builds fine because it owns the types

## What Didn't Work

- Writing `throw new FlowlineException(ExitCode.NotAuthenticated, "...")` directly in `DataverseConnector.cs` without checking which assembly owns the type — fails immediately because `Flowline.Core` has no reference path to `Flowline`
- Adding `<ProjectReference>` from `Flowline.Core` to `Flowline` would introduce a circular dependency and is not viable

## Solution

Move both types from `Flowline` to `Flowline.Core`, keeping the original `namespace Flowline;` so all callers compile unchanged.

**Step 1: Move the files**

```
src/Flowline/ExitCode.cs          → src/Flowline.Core/ExitCode.cs
src/Flowline/FlowlineException.cs → src/Flowline.Core/FlowlineException.cs
```

**Step 2: Keep the namespace unchanged**

```csharp
// src/Flowline.Core/ExitCode.cs — same content, same namespace, different physical project
namespace Flowline;

public enum ExitCode
{
    Success = 0,
    NotAuthenticated = 4,
    // ...
}
```

```csharp
// src/Flowline.Core/FlowlineException.cs — same content, same namespace
namespace Flowline;

public class FlowlineException : Exception
{
    public ExitCode ExitCode { get; init; } = ExitCode.GeneralError;
    // ...
}
```

**Step 3: Delete the originals from `Flowline`**

Remove `src/Flowline/ExitCode.cs` and `src/Flowline/FlowlineException.cs`.

**Result:** `Flowline.Core` adds `using Flowline;` and throws freely. Callers in `Flowline` use the same `using Flowline;` — types now resolve from `Flowline.Core` transitively, no import changes needed anywhere.

## Why This Works

`Flowline.Core` is the lower-level library; `Flowline` is the application that consumes it. Any type a library needs to throw, return, or reference must be defined *in that library or a lower one* — not in the consuming layer. `FlowlineException` and `ExitCode` in `Flowline` violated this: the library depended on the application for its own error contract.

Keeping `namespace Flowline;` in the moved files is the key: C# namespaces are a logical grouping, not tied to the physical assembly. All existing `using Flowline;` statements in `Flowline` continue to resolve these types — served from `Flowline.Core` now, which `Flowline` already references transitively.

## Prevention

- **Ownership rule:** If a type is thrown or returned by a library project (`Flowline.Core`, `Flowline.Attributes`), define it in that library. Application projects (`Flowline`) must not own shared contracts.

- **Isolation test:** `Flowline.Core.Tests` runs without referencing `Flowline`. If those tests can throw and catch `FlowlineException`, the type is correctly placed:

  ```csharp
  // Flowline.Core.Tests — no reference to Flowline project needed
  [Fact]
  public void BuildXrmContextConnectionString_UsernameSemicolon_ThrowsFlowlineException()
  {
      var ex = Assert.Throws<FlowlineException>(() =>
          DataverseConnector.BuildXrmContextConnectionString(
              "https://contoso.crm4.dynamics.com",
              Enumerable.Empty<PacProfile>(),
              username: "user;bad",
              password: "pass"));
      Assert.Equal(ExitCode.NotAuthenticated, ex.ExitCode);
  }
  ```

- **Before adding a new shared exception or enum:** identify which assemblies will use it and define it in the lowest one that needs it.

## Related Issues

- [AI-agent-consumable CLI contract](../architecture-patterns/ai-agent-consumable-cli-contract-2026-06-07.md) — documents the design rationale for `ExitCode` and `FlowlineException` as a stable public API. **Note:** that doc references the old path `src/Flowline/ExitCode.cs`; the canonical location is now `src/Flowline.Core/ExitCode.cs`.
