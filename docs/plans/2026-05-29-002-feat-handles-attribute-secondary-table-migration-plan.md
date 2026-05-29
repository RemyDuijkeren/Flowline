---
title: "feat: Add HandlesAttribute and migrate SecondaryTable to StepAttribute property"
type: feat
status: active
date: 2026-05-29
---

# feat: Add HandlesAttribute and migrate SecondaryTable to StepAttribute property

## Summary

Introduces `[Handles]` as a naming-convention escape hatch for brownfield plugin classes, adds `Message` and `Stage` enums to `Flowline.Attributes`, promotes `SecondaryTable` from a separate attribute to a named property on `[Step]`, and removes `SecondaryTableAttribute`. `PluginAssemblyReader` is updated to read the new structures; the stale design-pattern doc is corrected.

---

## Problem Frame

Plugin developers with existing class names (`AccountPlugin`, `ValidationHandler`) cannot adopt Flowline without renaming their classes. `SecondaryTableAttribute` is structurally inconsistent: it is a field on `sdkmessageprocessingstep`, not a separate entity, so it belongs on `[Step]` alongside other step-level properties.

---

## Requirements

- R1. `SecondaryTable` is a named property on `StepAttribute`; `SecondaryTableAttribute` is removed from `Flowline.Attributes`.
- R2. `[Handles(on: Message, stage: Stage)]` overrides naming-convention parsing when present on a `[Step]`-decorated class.
- R3. `[Handles(on: string, stage: Stage)]` overload supports Custom API message names.
- R4. `Stage` enum includes `PostOperationAsync`, folding async execution mode into the stage value.
- R5. `Message` enum covers all built-in Dataverse messages, mirroring the existing internal `MessageName` set.
- R6. `PluginAssemblyReader` reads `SecondaryTable` from `[Step]` named args and reads message/stage from `[Handles]` when present, skipping class-name parsing.
- R7. When `[Handles]` is present but the class name would also parse successfully, a warning is emitted suggesting the developer rename the class.
- R8. All existing tests pass with updated fixtures; new tests cover all `[Handles]` scenarios.

---

## Scope Boundaries

- Renaming `Config` to `UnsecureConfig` on `StepAttribute` — not in this plan.
- SecureConfig support — excluded by design.
- Changes to any attribute outside `StepAttribute`, `SecondaryTableAttribute`, and `HandlesAttribute`.
- `Flowline.Attributes.Tests` project — empty; adding tests there is deferred.

### Deferred to Follow-Up Work

- Updating `docs/solutions/design-patterns/attribute-per-dataverse-registration-2026-05-29.md` to correct the SecondaryTable reasoning (separate unit U7, can ship same PR).

---

## Context & Research

### Relevant Code and Patterns

- `src/Flowline.Attributes/StepAttribute.cs` — pattern for named properties on attributes.
- `src/Flowline.Attributes/AllowedStepType.cs` — only existing enum in `Flowline.Attributes`; C# 7.3 style to follow.
- `src/Flowline.Attributes/PreImageAttribute.cs` — constructor params + named property pattern.
- `src/Flowline.Core/Models/MessageName.cs` — 90+ member internal enum; `Message` enum member names must mirror these exactly.
- `src/Flowline.Core/Models/ProcessingStage.cs`, `ProcessingMode.cs` — internal stage/mode ints that `PluginStepMetadata` uses; `Stage` enum maps to these via explicit switch in the reader.
- `src/Flowline.Core/Services/PluginAssemblyReader.cs` — `TryBuildStep` (lines ~305–431), `ReadSecondaryTableAttribute` (lines ~483–492), `TryParseClassName` (lines ~384–431).
- Reader pattern for enum constructor args: `Convert.ToInt32(arg.TypedValue.Value)` (see `TryBuildCustomApi`, `AllowedStepType` case).
- Reader pattern for named args: `foreach (var arg in stepAttr.NamedArguments)` with `arg.MemberName` string matching.
- `tests/Flowline.Core.Tests/PluginAssemblyReaderTests.cs` — mock plugin classes decorated with real attributes; `Analyze()` loads the test assembly itself.

### Institutional Learnings

- `docs/solutions/design-patterns/attribute-per-dataverse-registration-2026-05-29.md` — **stale**: the SecondaryTable section explicitly argues against the merge now being implemented. U7 corrects it.
- `docs/solutions/logic-errors/retrieve-multiple-async-silent-truncation-2026-05-29.md` — `PluginAssemblyReader` must not call bare `RetrieveMultipleAsync`; verify the paging extension is in use before shipping U5.

---

## Key Technical Decisions

- **`Message` enum in `Flowline.Attributes`, not promoted from `MessageName`**: `MessageName` is internal to `Flowline.Core`; consumers annotate with `Message`. Member names must match `MessageName` exactly so `arg.TypedValue.ArgumentType.GetEnumName(value)` returns a usable string at read time. This avoids requiring int-identity between the two enums.
- **`Stage` values (0–3) do not match `ProcessingStage` ints (10/20/40)**: The reader maps via explicit `switch`; no direct cast. `PostOperationAsync=3` decomposes to `ProcessingStage.PostOperation (40)` + `ProcessingMode.Asynchronous (1)`.
- **`HandlesAttribute` two constructors, not one `object` param**: Two constructors (`Message on, Stage stage` and `string on, Stage stage`) are distinguishable at reflection time by `ConstructorArguments[0].ArgumentType.Name` (`Int32` vs `String`). Type safety on the common path, flexibility for Custom API.
- **`[Handles]` skips `ParseStepClassNameOrThrow` entirely**: The reader branches before the name-parser call. No interleaving — either `[Handles]` drives the result or the class name does.
- **Three-state `SecondaryTable` presence check removed**: The `(hasAttribute, table)` tuple and `MockNoEntitySecondaryPreAssociatePlugin` are deleted. `SecondaryTable == null` means absent; populated means present. The "attribute present but no arg" warning case is eliminated by the migration.
- **Warning when `[Handles]` present with parseable class name (R7)**: `TryParseClassName` is called silently after `[Handles]` resolution; if it succeeds, emit a warning advising rename. Does not block registration.

---

## Open Questions

### Resolved During Planning

- *Does `Message` enum need to be a subset or full set?* Full set — user explicitly requires all built-in Dataverse messages.
- *Can plugin steps fire on Custom API messages?* Yes — string overload handles user-defined message names.
- *Should `[Handles]` and a matching class name coexist?* Yes, with a warning nudging toward convention (R7).

### Deferred to Implementation

- Exact `Message` enum member list: mirror all members from `src/Flowline.Core/Models/MessageName.cs` at implementation time; do not pre-enumerate in the plan.
- Whether `ValidateSecondaryTable`'s signature needs adjustment after the three-state removal — resolvable on first read of the call sites.

---

## High-Level Technical Design

> *This illustrates the intended approach and is directional guidance for review, not implementation specification.*

**Stage enum → internal model mapping:**

| Stage value | ProcessingStage | ProcessingMode |
|---|---|---|
| `PreValidation = 0` | 10 | Synchronous (0) |
| `PreOperation = 1` | 20 | Synchronous (0) |
| `PostOperation = 2` | 40 | Synchronous (0) |
| `PostOperationAsync = 3` | 40 | Asynchronous (1) |

**Reader branch in `TryBuildStep` (conceptual):**

```
if HandlesAttribute found on type:
    if ConstructorArguments[0] is string → message = that string
    if ConstructorArguments[0] is int    → message = GetEnumName(argumentType, value)
    stage, mode = map Stage enum value via switch
    if TryParseClassName(type.Name) also succeeds → emit "consider renaming" warning
else:
    ParseStepClassNameOrThrow(type.Name) → message, stage, mode
```

---

## Implementation Units

### U1. Create `Message` enum

**Goal:** Public enum in `Flowline.Attributes` covering all built-in Dataverse messages; used as the `on` parameter type in `HandlesAttribute`.

**Requirements:** R5

**Dependencies:** None

**Files:**
- Create: `src/Flowline.Attributes/Message.cs`

**Approach:**
- Mirror all member names from `src/Flowline.Core/Models/MessageName.cs` at implementation time.
- Auto-increment values (0, 1, 2, …) — reader recovers the name string via `GetEnumName`, not int cast.
- C# 7.3: no file-scoped namespace, no nullable annotations, XML doc on each member.

**Patterns to follow:**
- `src/Flowline.Attributes/AllowedStepType.cs` — enum structure, namespace, XML doc style.

**Test scenarios:**
- Test expectation: none — pure data declaration, no logic. Coverage via U6 integration tests.

**Verification:**
- `Flowline.Attributes` project builds cleanly targeting netstandard2.0 with LangVersion 7.3.
- Member names match `MessageName` exactly (visual cross-check at implementation time).

---

### U2. Create `Stage` enum

**Goal:** Public enum in `Flowline.Attributes` with `PreValidation`, `PreOperation`, `PostOperation`, `PostOperationAsync`; folds async execution mode into the stage value.

**Requirements:** R4

**Dependencies:** None

**Files:**
- Create: `src/Flowline.Attributes/Stage.cs`

**Approach:**
- Values: `PreValidation=0, PreOperation=1, PostOperation=2, PostOperationAsync=3`.
- Int values intentionally do not match `ProcessingStage` (10/20/40); mapping is explicit in the reader.
- C# 7.3: same style constraints as U1.

**Patterns to follow:**
- `src/Flowline.Attributes/AllowedStepType.cs`

**Test scenarios:**
- Test expectation: none — pure data declaration. Coverage via U6 integration tests.

**Verification:**
- `Flowline.Attributes` builds cleanly.

---

### U3. Migrate SecondaryTable to `StepAttribute`; delete `SecondaryTableAttribute`

**Goal:** `SecondaryTable` becomes a named property on `StepAttribute`; `SecondaryTableAttribute` is removed.

**Requirements:** R1

**Dependencies:** None

**Files:**
- Modify: `src/Flowline.Attributes/StepAttribute.cs`
- Delete: `src/Flowline.Attributes/SecondaryTableAttribute.cs`

**Approach:**
- Add `public string SecondaryTable { get; set; }` to `StepAttribute`. Default is `null` (absent).
- Remove `SecondaryTableAttribute.cs` from the project file and disk.
- The `"none"` sentinel and warning behaviour (warn on null, suppress warning on `"none"`) previously on `SecondaryTableAttribute` should be preserved in the new property's XML doc and reader validation (U5).

**Patterns to follow:**
- `RunAs`, `Config`, `DeleteJobOnSuccess` on `src/Flowline.Attributes/StepAttribute.cs` — named property style.

**Test scenarios:**
- Test expectation: none at attribute level. Reader behaviour covered in U6.

**Verification:**
- `Flowline.Attributes` builds cleanly; `SecondaryTableAttribute.cs` file no longer exists.

---

### U4. Create `HandlesAttribute`

**Goal:** Public attribute that explicitly declares message and stage, overriding the naming convention. Accepts a `Message` enum (built-in) or `string` (Custom API) plus a `Stage` value.

**Requirements:** R2, R3

**Dependencies:** U1, U2

**Files:**
- Create: `src/Flowline.Attributes/HandlesAttribute.cs`

**Approach:**
- `AttributeUsage(AttributeTargets.Class)`, `sealed`.
- Two constructors — C# 7.3 does not allow optional parameters with enum defaults, so explicit overloads:
  - `HandlesAttribute(Message on, Stage stage)`
  - `HandlesAttribute(string on, Stage stage)`
- Expose `On` (string, covers both cases), `Stage` (Stage), and `IsCustomMessage` (bool) as read-only properties.
- `IsCustomMessage` allows the reader to skip the int-to-name conversion path for the string overload.
- XML doc explains: "Use when class name does not follow Flowline naming convention. Prefer renaming the class."

**Patterns to follow:**
- `src/Flowline.Attributes/PreImageAttribute.cs` — constructor + named property layout.
- `src/Flowline.Attributes/StepAttribute.cs` — XML doc style.

**Test scenarios:**
- Test expectation: none at attribute level. Coverage via U6 integration tests.

**Verification:**
- `Flowline.Attributes` builds cleanly with both constructor overloads.

---

### U5. Update `PluginAssemblyReader`

**Goal:** Reader handles `SecondaryTable` as named property on `[Step]`, reads `[Handles]` to bypass class-name parsing, and maps `Stage` enum to `ProcessingStage`/`ProcessingMode` pairs.

**Requirements:** R6, R7

**Dependencies:** U3, U4

**Files:**
- Modify: `src/Flowline.Core/Services/PluginAssemblyReader.cs`

**Approach:**
- Remove `ReadSecondaryTableAttribute` method and its `(hasAttribute, table)` tuple call sites.
- Add `"SecondaryTable"` case to the existing named-args loop in `TryBuildStep`; `null` means absent.
- Before `ParseStepClassNameOrThrow`, detect `HandlesAttribute` via `FullName == "Flowline.Attributes.HandlesAttribute"`:
  - String overload: `ConstructorArguments[0].ArgumentType.Name == "String"` → use value directly as message.
  - Enum overload: recover name via `ConstructorArguments[0].TypedValue.ArgumentType.GetEnumName(value)`.
  - Map `(int)ConstructorArguments[1].TypedValue.Value` to `ProcessingStage`/`ProcessingMode` via explicit switch (see High-Level Technical Design table).
- After extracting from `[Handles]`, call `TryParseClassName` silently; if it returns `true`, add a warning: "Class name follows convention — `[Handles]` is redundant. Consider renaming to remove it."
- Verify `PluginAssemblyReader` uses the team's paging extension (not bare `RetrieveMultipleAsync`) per the truncation learning before shipping.

**Patterns to follow:**
- `AllowedStepType` case in `TryBuildCustomApi` for enum-arg int extraction.
- Existing named-arg loop in `TryBuildStep` for property reading style.

**Test scenarios:**
- (Integration) Happy path: `[Step("account")] [Handles(Message.Update, Stage.PreOperation)]` on `class AccountPlugin` → step registered with message=Update, stage=PreOperation, mode=Synchronous.
- (Integration) Happy path: `[Step("account")] [Handles(Message.Create, Stage.PostOperationAsync)]` → stage=PostOperation (40), mode=Asynchronous (1).
- (Integration) Happy path: `[Step("account")] [Handles("mynamespace_MyAction", Stage.PostOperation)]` → message="mynamespace_MyAction", stage=PostOperation, mode=Synchronous.
- (Integration) `SecondaryTable` on `[Step]`: `[Step("contact", SecondaryTable = "account")]` → `PluginStepMetadata.SecondaryTable == "account"`.
- (Integration) Warning path: `[Step("account")] [Handles(Message.Update, Stage.PreOperation)]` on `class AccountPreUpdatePlugin` → step registered correctly AND warning emitted about redundant `[Handles]`.
- (Integration) Warning path: `[Step("account")] [Handles(Message.Update, Stage.PreOperation)]` on any class + `SecondaryTable = null` → no secondary table on the step.
- (Edge case) `[Handles]` present without `[Step]` → treated as no step (same as any undecorated class).
- (Edge case) `Stage.PreValidation` in `[Handles]` → maps to `ProcessingStage 10`, mode Synchronous.

**Verification:**
- `dotnet test tests/Flowline.Core.Tests` passes.
- Paging extension confirmed in use (not bare `RetrieveMultipleAsync`).

---

### U6. Update tests

**Goal:** Remove fixtures and tests for the deleted three-state `SecondaryTable` presence, update existing associate-step fixtures, and add comprehensive `[Handles]` test coverage.

**Requirements:** R8

**Dependencies:** U5

**Files:**
- Modify: `tests/Flowline.Core.Tests/PluginAssemblyReaderTests.cs`

**Approach:**
- Update `MockPreAssociatePlugin`: remove `[SecondaryTable("account")]`, add `SecondaryTable = "account"` to its `[Step]` declaration.
- Delete `MockNoEntitySecondaryPreAssociatePlugin` and its test (`[SecondaryTable]` no-arg warning case no longer exists).
- Update string assertions that reference `"[SecondaryTable]"` to match new warning wording.
- Add mock classes (at the bottom section of the file, per established pattern):
  - `MockHandlesUpdatePrePlugin` — `[Step("account")] [Handles(Message.Update, Stage.PreOperation)]` on a class that does NOT follow convention.
  - `MockHandlesAsyncPostCreatePlugin` — `[Step("account")] [Handles(Message.Create, Stage.PostOperationAsync)]`.
  - `MockHandlesCustomApiPlugin` — `[Step("account")] [Handles("mynamespace_MyAction", Stage.PostOperation)]`.
  - `MockHandlesRedundantPlugin` — `AccountPreUpdatePlugin` with `[Handles(Message.Update, Stage.PreOperation)]` to trigger the redundancy warning.

**Test scenarios:**
- (Happy path) `MockHandlesUpdatePrePlugin` → step.Message == "Update", step.Stage == PreOperation(20), step.Mode == Synchronous(0).
- (Happy path) `MockHandlesAsyncPostCreatePlugin` → step.Stage == PostOperation(40), step.Mode == Asynchronous(1).
- (Happy path) `MockHandlesCustomApiPlugin` → step.Message == "mynamespace_MyAction".
- (Happy path) `MockPreAssociatePlugin` (updated) → step.SecondaryTable == "account".
- (Warning) `MockHandlesRedundantPlugin` → step registered AND warnings contain "redundant".
- (Edge case) Class with `[Handles]` but no `[Step]` → not included in result.
- (Error path) `[Handles(Message.Update, Stage.PreValidation)]` on PostOperation-only message that Dataverse rejects — deferred to implementation (validation scope unclear).

**Verification:**
- `dotnet test tests/Flowline.Core.Tests` passes with no skipped tests.
- No remaining references to `SecondaryTableAttribute` in test file.

---

### U7. Correct stale solution doc

**Goal:** Update `attribute-per-dataverse-registration-2026-05-29.md` to reflect the final decision: `SecondaryTable` is a named property on `[Step]`, not a separate attribute.

**Requirements:** — (doc correctness, not a code requirement)

**Dependencies:** None

**Files:**
- Modify: `docs/solutions/design-patterns/attribute-per-dataverse-registration-2026-05-29.md`

**Approach:**
- In the Guidance section: remove `SecondaryTableAttribute` from the "must be separate" examples.
- In the Examples section: change the CORRECT/WRONG examples for SecondaryTable to show `[Step("account", SecondaryTable = "contact")]` as correct.
- Update the Why This Matters table to remove the `[SecondaryTable]` row.
- Add `last_updated: 2026-05-29` to frontmatter.
- The governing rule ("separate attribute = separate Dataverse registration") still holds — `[PreImage]`/`[PostImage]` remain the canonical examples.

**Test scenarios:**
- Test expectation: none — documentation change.

**Verification:**
- `python3 scripts/validate-frontmatter.py` exits 0 (if script present).
- No remaining references in the doc to `[SecondaryTable]` as a separate attribute.

---

## System-Wide Impact

- **Breaking change for consumers**: `SecondaryTableAttribute` is removed from `Flowline.Attributes`. Any plugin project referencing `[SecondaryTable]` will fail to compile after updating the NuGet package. This is a deliberate breaking change — consumers must migrate to `[Step(SecondaryTable = "...")]`.
- **Unchanged invariants**: `[Step]`, `[PreImage]`, `[PostImage]`, `[CustomApi]` attribute signatures are unchanged. Class-name parsing continues to function for all classes without `[Handles]`.
- **`MessageName` internal enum**: unchanged; `Message` in `Flowline.Attributes` mirrors its names but is a separate type.
- **Integration coverage**: `PluginAssemblyReader` tests load the test assembly itself — mock class changes in U6 are immediately exercised by the existing `Analyze()` infrastructure.

---

## Risks & Dependencies

| Risk | Mitigation |
|------|------------|
| `Message` member names diverge from `MessageName` — reader returns wrong message string | Visual cross-check during U1 implementation; name-mismatch test in U6 for a spot-check member |
| `Flowline.Attributes` C# 7.3 constraint violated by new files | Match existing file structure exactly; no modern syntax |
| Consumer projects broken by `SecondaryTableAttribute` removal | Expected breaking change; note in changelog/release notes |
| Paging bug in `PluginAssemblyReader` surfaced by refactor | Verify paging extension in use before U5 ships (per institutional learning) |

---

## Sources & References

- `src/Flowline.Attributes/StepAttribute.cs`
- `src/Flowline.Attributes/AllowedStepType.cs`
- `src/Flowline.Core/Models/MessageName.cs`
- `src/Flowline.Core/Services/PluginAssemblyReader.cs`
- `tests/Flowline.Core.Tests/PluginAssemblyReaderTests.cs`
- `docs/solutions/design-patterns/attribute-per-dataverse-registration-2026-05-29.md` (stale — corrected in U7)
- `docs/solutions/logic-errors/retrieve-multiple-async-silent-truncation-2026-05-29.md`
