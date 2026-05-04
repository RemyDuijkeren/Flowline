# Change Request: Clear Error on Invalid Message or Entity Name

## Problem

In `PlanPluginSteps`, two separate issues cause bad behavior when a `[Step]` attribute
references a message or entity name that does not exist in the target Dataverse environment.

### Issue 1 — Invalid message crashes with `KeyNotFoundException`

```csharp
// Line 109 — unhandled crash:
var messageId = snapshot.SdkMessageIds[asmStep.Message];
```

`SdkMessageIds` is a plain `Dictionary<string, Guid>`. Accessing a non-existent key throws
`KeyNotFoundException` with no context about which plugin or step caused it. The sync aborts
with a cryptic runtime exception — the developer has no idea which step or message is wrong.

### Issue 2 — Invalid entity silently creates a misconfigured step

```csharp
// Line 110 — filterId is null when entity name doesn't exist in Dataverse:
snapshot.FilterIds.TryGetValue((messageId, asmStep.EntityName, asmStep.SecondaryEntity), out var filterId);

// Lines 163-164 — create path: sdkmessagefilterid is simply omitted:
if (filterId.HasValue)
    entity["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filterId.Value);

// Lines 142-143 — update path: same omission:
if (filterId.HasValue)
    dvStep["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filterId.Value);
```

When `EntityName` is a non-empty string (e.g. `"acount"` — typo) and the entity does not
exist in Dataverse, `filterId` is `null`. The step is then created/updated **without**
`sdkmessagefilterid`, which means it fires on **all** entities — the exact opposite of
developer intent. There is no warning, no error, the sync reports success.

Note: `filterId == null` with an empty/null `EntityName` is correct and intentional —
that is a "register on all entities" step. The bug only applies when `EntityName` has a
value that fails the lookup.

### Issue 3 — Image creation throws an unhelpful error for unsupported messages

```csharp
// Line 220-221 in PlanImages:
if (!s_messagePropertyNames.TryGetValue(message, out var propertyName))
    throw new InvalidOperationException($"Message '{message}' does not support step images.");
```

The exception message is reasonable but gives no context about which step or plugin caused it.
Replace with an `InvalidOperationException` that includes the image name, message, and supported message list.

---

## Solution

**Fail fast with a clear, actionable error message.** These are developer mistakes (typos,
wrong message name) that should never reach production silently. The right failure mode is an
immediate, informative exception — not a skip, not a warning, not a cryptic runtime error.

All three fixes are in `PlanPluginSteps` and `PlanImages` in
`src/Flowline.Core/Services/PluginPlanner.cs`.

---

## Changes Required

### `src/Flowline.Core/Services/PluginPlanner.cs`

#### Fix 1 — Replace the `SdkMessageIds` indexer with `TryGetValue` + throw

Replace this in `PlanPluginSteps`:

```csharp
// REMOVE:
var messageId = snapshot.SdkMessageIds[asmStep.Message];
```

With:

```csharp
// ADD:
if (!snapshot.SdkMessageIds.TryGetValue(asmStep.Message, out var messageId))
    throw new InvalidOperationException(
        $"Step '{asmStep.Name}' references message '{asmStep.Message}' which does not exist " +
        $"in this environment. Check the message name on [{nameof(StepAttribute)}] for '{asmPluginType.FullName}'.");
```

#### Fix 2 — Throw when entity name doesn't resolve to a filter

Add this immediately after `FilterIds.TryGetValue`:

```csharp
// ADD: entity name was specified but no matching filter found in Dataverse
var entityRequested = !string.IsNullOrEmpty(asmStep.EntityName) && asmStep.EntityName != "none";
if (entityRequested && !filterId.HasValue)
    throw new InvalidOperationException(
        $"Step '{asmStep.Name}' references entity '{asmStep.EntityName}' which is not supported " +
        $"for message '{asmStep.Message}' in this environment. Check the entity name on " +
        $"[{nameof(StepAttribute)}] for '{asmPluginType.FullName}'.");
```

The `entityRequested` guard preserves the intentional all-entities behavior:
- `EntityName = null` or `""` → all-entities step, no filter expected → OK
- `EntityName = "none"` → explicit all-entities intent → OK
- `EntityName = "account"` and filter found → OK, filter is set
- `EntityName = "acount"` (typo) and filter not found → **throws with clear message**

#### Fix 3 — Replace the generic throw in `PlanImages` with a more informative one

Replace in `PlanImages`:

```csharp
// REMOVE:
if (!s_messagePropertyNames.TryGetValue(message, out var propertyName))
    throw new InvalidOperationException($"Message '{message}' does not support step images.");
```

With:

```csharp
// ADD:
if (!s_messagePropertyNames.TryGetValue(message, out var propertyName))
    throw new InvalidOperationException(
        $"Image '{asmImage.Name}' cannot be registered — message '{message}' does not support " +
        $"step images. Supported messages: {string.Join(", ", s_messagePropertyNames.Keys)}.");
```

Listing the supported messages in the error saves the developer a trip to the docs.

---

## Final shape of `PlanPluginSteps` inner loop (relevant lines only)

```csharp
foreach (var asmStep in asmPluginSteps)
{
    if (!snapshot.SdkMessageIds.TryGetValue(asmStep.Message, out var messageId))
        throw new InvalidOperationException(
            $"Step '{asmStep.Name}' references message '{asmStep.Message}' which does not exist " +
            $"in this environment. Check the message name on [Step] for '{asmPluginType.FullName}'.");

    snapshot.FilterIds.TryGetValue((messageId, asmStep.EntityName, asmStep.SecondaryEntity), out var filterId);

    var entityRequested = !string.IsNullOrEmpty(asmStep.EntityName) && asmStep.EntityName != "none";
    if (entityRequested && !filterId.HasValue)
        throw new InvalidOperationException(
            $"Step '{asmStep.Name}' references entity '{asmStep.EntityName}' which is not supported " +
            $"for message '{asmStep.Message}' in this environment. Check the entity name on [Step] " +
            $"for '{asmPluginType.FullName}'.");

    // ... rest of the method unchanged
```

---

## No changes needed elsewhere

- `LoadSnapshot`, `PluginRegistrationService`, `PluginExecutor` — unchanged
- `PlanCustomApi` — has its own independent lookup logic, no equivalent issue
- `PlanImages` update path — already safe (only iterates existing registered images)
- Obsolete step deletion loop at the end of `PlanPluginSteps` — unchanged

---

## Tests to add

In `PluginPlannerTests.cs`, add tests for:

1. **Invalid message** — `SdkMessageIds` does not contain the step's message → throws `InvalidOperationException` with message name and plugin type in the message
2. **Invalid entity on create** — `FilterIds` returns null for a non-null `EntityName` → throws `InvalidOperationException` with entity name and message name
3. **Invalid entity on update** — existing step in snapshot, local definition has invalid entity → same throw
4. **Valid entity** — `FilterIds` returns a filter ID → step created with `sdkmessagefilterid` set, no exception
5. **All-entities (null EntityName)** — `filterId` null, `EntityName` null → step created without filter, no exception
6. **All-entities ("none" EntityName)** — `filterId` null, `EntityName` "none" → step created without filter, no exception
7. **Unsupported message for image** — `s_messagePropertyNames` does not contain message → throws with supported message list in error text
