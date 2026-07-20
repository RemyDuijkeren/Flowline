---
title: "feat: Add verbatim mode for web resource naming"
type: feat
status: complete
date: 2026-06-13
origin: docs/brainstorms/2026-06-12-webresource-naming-requirements.md
---

# feat: Add verbatim mode for web resource naming

## Summary

`GetLocalWebResources` currently prepends `{publisherPrefix}_{solutionName}/` to every local file path unconditionally. Files whose top-level folder already carries a publisher prefix (any `shortcode_` pattern) should use their path verbatim — no extra prefix. This enables shared-namespace folders (`{prefix}_/`) and co-located cross-publisher resources.

---

## Requirements

**Verbatim detection**

- R1. A local file whose first path segment matches `^[a-z][a-z0-9]*_` gets its relative path used as the CRM name with no prefix prepended.
- R2. A local file whose first path segment does not match that pattern continues to get auto-prefixed with `{publisherPrefix}_{solutionName}/`.
- R3. Verbatim detection fires on any publisher prefix — not just the current solution's publisher. Files under `new_MySolution/`, `dh_/`, `av_MySolution/` all trigger verbatim mode.

**Shared namespace**

- R4. A folder named `{shortcode}_/` (shortcode + underscore, no solution segment) is a valid verbatim root. Files under it resolve to `{shortcode}_/{rest}` with no solution segment inserted.

**Collision detection**

- R5. If an auto-prefixed file and a verbatim file in the same root resolve to the same CRM name, an error is raised before any Dataverse calls, naming both source paths.
- R6. If two verbatim files in the same root resolve to the same CRM name (duplicate file under the same verbatim folder), an error is raised.

---

## Key Technical Decisions

- **Verbatim detection in `GetLocalWebResources`, not `WebResourcePlanner`:** naming is the reader's responsibility; the planner receives a complete name-keyed dictionary. Moving detection to the planner would require threading collision metadata through `WebResourceSyncSnapshot`, adding accidental complexity to a data structure with no other collision concept.

- **Collision detection also in `GetLocalWebResources`:** the reader builds a `Dictionary<string, LocalWebResource>` keyed on CRM name. Once a duplicate key is inserted the original entry is gone — the planner cannot recover which files collided. Detection must happen at key insertion time. Throws `InvalidOperationException` (consistent with other `Flowline.Core` precondition failures — `FlowlineException` is defined in the `Flowline` CLI layer, not available in `Flowline.Core`).

- **No config — regex only:** detection is `^[a-z][a-z0-9]*_` on the first path segment. No publisher prefix lookup at push time. This matches the brainstorm decision and keeps `GetLocalWebResources` self-contained.

- **Shared namespace is a free consequence of R1:** a folder named `av_/` has a first segment `av_` which matches the pattern, so verbatim mode applies automatically. No separate code path is needed.

---

## High-Level Technical Design

Current flow vs. new flow in `GetLocalWebResources`:

```mermaid
flowchart TB
  A[Enumerate files under root] --> B{First segment\nmatches ^[a-z][a-z0-9]*_?}
  B -->|yes| C[name = relativePath]
  B -->|no| D["name = {prefix}/{relativePath}"]
  C --> E{name already in dict?}
  D --> E
  E -->|yes| F[throw InvalidOperationException\nwith both file paths]
  E -->|no| G[dict\[name\] = LocalWebResource]
```

---

## Scope Boundaries

### Deferred to Follow-Up Work

- ~~Download direction: `DownloadWebResourcesAsync` strips the auto-prefix when writing files to disk. Verbatim resources downloaded from Dataverse currently land under `{prefix}/…` — whether they should strip to their verbatim path is not addressed in the brainstorm and left for a follow-up.~~ **Moot (2026-07-20).** This note was written against a method that already had no callers: `CloneCommand.CloneWebResourcesFromDataverseAsync` was removed on 2026-05-17 by `2026-05-17-001-refactor-remove-mapping-replace-dotnet-build-plan.md` U3/R6, four weeks earlier. `DownloadWebResourcesAsync` has since been deleted. Clone now seeds from `Package/src/WebResources/` (already unpacked by PAC) rather than fetching from Dataverse, so there is no download direction to define. Should one ever be added, it would target `dist/` and this question would need answering fresh.

### Outside this product's identity

- Configuration-based name overrides — all naming derives from folder structure only.
- Cross-publisher shared resources accessed by a different publisher's solution.

---

## Implementation Units

### U1. Verbatim mode detection in `GetLocalWebResources`

**Goal:** Replace the unconditional `$"{prefix}/{relativePath}"` with a branch that checks the first path segment against `^[a-z][a-z0-9]*_`.

**Requirements:** R1, R2, R3, R4

**Dependencies:** none

**Files:**
- `src/Flowline.Core/Services/WebResourceReader.cs`

**Approach:** Extract or inline the first-segment check. `relativePath` already uses `/` as separator (line 143 normalises `\` to `/`). Guard: only apply verbatim detection when `relativePath.Contains('/')` — a root-level file (no subfolder) always uses auto-prefix regardless of its name. When a separator exists, split on `/`, take index 0, test the regex. A compiled static `Regex` field (like `ValidFilePathRegex` in the planner) avoids per-call compilation.

**Patterns to follow:** `WebResourcePlanner.ValidFilePathRegex` — static compiled `Regex` field.

**Test scenarios:**
- File at `js/app.js` with prefix `av_MySolution` → CRM name is `av_MySolution/js/app.js` (auto-prefix).
- File at `av_MySolution/js/app.js` → CRM name is `av_MySolution/js/app.js` (verbatim, same result).
- File at `new_Other/util.js` → CRM name is `new_Other/util.js` (verbatim, different publisher).
- File at `dh_/lib/jquery.js` → CRM name is `dh_/lib/jquery.js` (shared namespace, verbatim).
- File at `av_helper.js` (root level, no subfolder) with prefix `av_MySolution` → CRM name is `av_MySolution/av_helper.js` (no subfolder, auto-prefix regardless of filename).
- File at `Util/helper.js` with prefix `av_MySolution` → CRM name is `av_MySolution/Util/helper.js` (uppercase first segment does not match lowercase-only regex, auto-prefixed).
- File at `av_/shared.js` → CRM name is `av_/shared.js` (shared namespace, single file).

**Verification:** `GetLocalWebResources` returns names matching expected CRM paths for each case above, exercised through `WebResourceServiceTests` by placing files at the corresponding relative paths.

---

### U2. Collision detection in `GetLocalWebResources`

**Goal:** Throw `InvalidOperationException` before inserting a duplicate CRM name, showing both conflicting file paths.

**Requirements:** R5, R6

**Dependencies:** U1 (verbatim names must be resolved before collision can be detected)

**Files:**
- `src/Flowline.Core/Services/WebResourceReader.cs`

**Approach:** Before `result[name] = ...`, check `result.ContainsKey(name)`. If true, throw `InvalidOperationException` with a message that names both the incoming `relativePath` and `result[name].RelativePath`, plus the conflicting CRM name. Message should mirror the error format shown in the brainstorm.

**Test scenarios:**
- `av_MySolution/js/app.js` (verbatim) and `js/app.js` (auto-prefix → `av_MySolution/js/app.js`) in same root → throws `InvalidOperationException` containing both file paths and the CRM name.
- Two files at different paths resolving to the same verbatim name → throws.
- No collision (auto-prefix only, verbatim only, mixed without overlap) → no throw.

**Verification:** `Assert.Throws<InvalidOperationException>` for collision cases; existing sync tests still pass.

---

### U3. Tests

**Goal:** Cover all verbatim and collision scenarios through the existing integration-style test fixture.

**Requirements:** R1–R6

**Dependencies:** U1, U2

**Files:**
- `tests/Flowline.Core.Tests/WebResourceServiceTests.cs`

**Approach:** Add test methods to the existing `WebResourceServiceTests` class. The fixture already creates a temp directory (`_webresourceRoot`) and mocks Dataverse. Create subdirectories under that root to simulate verbatim and auto-prefix layouts, then call `SyncSolutionAsync` and assert the CRM names passed to `ExecuteAsync(CreateRequest)`. For collision tests use `Assert.ThrowsAsync<InvalidOperationException>`.

**Patterns to follow:** `SyncSolutionAsync_CreateNewWebResource_ShouldCreateAndPublishTargeted` — creates a file, calls sync, asserts `CreateRequest.Target["name"]`.

**Test scenarios:**

Auto-prefix (regression):
- Existing file at root level still gets `{publisher}_{solution}/` prefix — no change to current behaviour.

Verbatim mode:
- File under `av_MySolution/js/app.js` syncs with CRM name `av_MySolution/js/app.js`.
- File under `new_Other/util.js` syncs with CRM name `new_Other/util.js`.
- File under `dh_/lib/jquery.js` syncs with CRM name `dh_/lib/jquery.js` (shared namespace).
- Mixed root: one auto-prefix file and one verbatim file in same root each resolve to distinct correct names.

Collision detection:
- Verbatim `av_MySolution/js/app.js` + auto-prefix `js/app.js` (same prefix as solution) → `SyncSolutionAsync` throws `InvalidOperationException`.

**Verification:** All new tests pass; all existing `WebResourceServiceTests` tests continue to pass.

---

## Risks & Dependencies

- `GetLocalWebResources` is `static` and `private`. Tests exercise it indirectly through `WebResourceService.SyncSolutionAsync`. If direct unit testing becomes needed, making it `internal` with `InternalsVisibleTo` is straightforward but not required for this plan.
- The regex `^[a-z][a-z0-9]*_` intentionally excludes uppercase first segments (e.g., `Av_MySolution`). Dataverse publisher prefixes are always lowercase, so this is correct — but worth a comment if a future reader might question it.
