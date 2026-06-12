---
title: Web Resource Dependency Registration
date: 2026-06-12
status: idea
origin: docs/Features/FR-webresource-dependencies.md
---

# Web Resource Dependency Registration

## Problem

`flowline push` syncs web resources but does not create or maintain dependency relationships.
Result:

- RESX files uploaded but not linked to parent JS → `getResourceString` returns `null` at runtime
- Shared JS libraries uploaded but not registered as dependencies
- Developers must manually create dependencies in the maker portal after every push

---

## Convention-Based Dependency Detection

No config files. Dependencies inferred from naming patterns and in-source annotations.

### 1. RESX → JS: Base Name Matching (auto-detect)

`{anyPath}/{name}.{lcid}.resx` is automatically registered as a dependency of `{anyPath}/{name}.js`
when base names match.

```
webresources/
  scripts/MyForm.js         ← parent (base name "MyForm")
  strings/MyForm.1033.resx  ← auto-dependency → MyForm.js
  strings/MyForm.1043.resx  ← auto-dependency → MyForm.js
```

If zero or multiple JS files match the same base name → warning, skip auto-registration (use annotation).

### 2. JS → JS: In-Source Annotation

```javascript
// flowline:depends av_mysolution/lib/jquery.js
// flowline:depends av_mysolution/lib/utils.js
```

Comment at top of file, before any code. Full CRM logical name (any namespace — own solution,
shared `av_/`, other solution). Multiple lines allowed.

> **Load order not guaranteed.** Web resources load asynchronously in parallel even with dependencies
> registered. Use module patterns or init guards if execution order matters.

### 3. HTML → JS/CSS: Optional Annotation (solution integrity only)

HTML resources load in an iframe — browser handles CSS/JS fetching directly via relative URLs.
Dependency registration is optional and only prevents accidental deletion in managed contexts:

```html
<!-- flowline:depends av_mysolution/css/styles.css -->
```

Not auto-detected from `src`/`href` attributes (avoids silent unintended additions).

### 4. RESX Ambiguous Override

```javascript
// flowline:depends av_mysolution/strings/SharedLabels.resx
```

`.resx` without LCID expands to all matching language variants.

---

## Sync Behaviour

After all web resources are synced:

1. Compute desired dependency set from base-name matching + annotations
2. For each JS/HTML with a desired set: retrieve current `dependencyxml` from Dataverse
3. Diff: add missing, remove stale
4. If changed: write back via `UpdateRequest`, mark for publish
5. Publish modified resources

---

## Orphan Cleanup Integration

Dependency-referenced non-local files (e.g. shared libs in another namespace) are exempt from orphan
cleanup if they appear in any `// flowline:depends` annotation across all local files.

| CRM record | In dependency set | Action |
|---|---|---|
| Has local file | — | Normal sync |
| No local file | Yes | Preserve — skip orphan handling |
| No local file | No | Normal orphan handling |

---

## SDK Notes

Dependencies stored in `dependencyxml` field on `webresource` entity (Memo, max 5000 chars, "for
internal use only"). Format based on community knowledge — **verify empirically before implementing**
by exporting a solution with manually configured dependencies.

Expected format:
```xml
<Dependencies>
  <Dependency componentType="31">
    <WebResourceDependency name="av_mysolution/strings/Labels.resx" />
  </Dependency>
</Dependencies>
```

Read-modify-write pattern: always read existing `dependencyxml` before writing to avoid overwriting
dependencies set by other tools.

---

## Out of Scope

- Column dependencies (stored in form XML, Flowline doesn't manage forms)
- CSS → anything (no parseable annotation syntax for CSS)
- Transitive dependency walking (A → B → C not walked automatically)
