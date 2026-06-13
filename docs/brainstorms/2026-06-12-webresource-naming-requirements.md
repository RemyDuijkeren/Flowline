---
title: Web Resource Naming — Verbatim Mode and Shared Namespace
date: 2026-06-12
status: idea
origin: docs/Features/FR-webresource-naming.md
partially-implemented: auto-prefix mode is live (WebResourceReader.cs)
---

# Web Resource Naming — Verbatim Mode and Shared Namespace

## What's Already Implemented

**Auto-prefix** (default): files not starting with any `{shortcode}_` pattern get prefixed automatically with `{publisher_prefix}_{solution}/`:

```
{root}/js/app.js  →  av_MySolution/js/app.js
```

Implemented in `WebResourceReader.cs`. This covers the majority of projects.

---

## What's Missing

### Verbatim Mode

Files whose top-level folder **starts with any publisher prefix** (`{shortcode}_`) use the folder
path as the CRM name verbatim — no additional prefix or solution name prepended. The prefix doesn't
have to match the current project's publisher — any publisher prefix triggers verbatim mode.

```
{root}/av_MySolution/js/app.js  →  av_MySolution/js/app.js   (project publisher)
{root}/av_/lib/jquery.js        →  av_/lib/jquery.js          (shared namespace)
{root}/new_MySolution/js/app.js →  new_MySolution/js/app.js  (different publisher)
{root}/dh_/lib/util.js          →  dh_/lib/util.js            (different publisher shared)
```

Detection rule (no publisher prefix lookup needed):
- First path segment matches `^[a-z][a-z0-9]*_` → verbatim mode for that folder
- Otherwise → auto-prefix mode

### Shared Namespace: `{prefix}_/`

The `{prefix}_/` top-level folder (publisher prefix + `_/`, no solution segment) is a shared
namespace scoped to that publisher. Files here can be referenced by any solution using the same prefix.

```
dist/av_/lib/jquery.js   →  av_/lib/jquery.js   (no solution name in CRM path)
```

Multi-solution ownership: the existing `solutioncomponent` cross-solution check handles this
correctly already — no new ownership mechanism needed. The description stamp from FR orphan-cleanup
serves as the ownership indicator for shared files (since the filename convention alone doesn't
identify the owning solution).

### Collision Detection

If auto-prefix and verbatim paths in the same root resolve to the same CRM name, error at plan time
before any Dataverse calls:

```
Error: two local files resolve to the same CRM name 'av_MySolution/js/app.js':
  js/app.js              (auto-prefix)
  av_MySolution/js/app.js (verbatim)
```

---

## Out of Scope

- Dataverse's default naming (`{prefix}_filename`, flat) — Flowline does not adopt this
- Configuration-based name overrides — all naming from folder structure
- Cross-publisher shared resources — `{prefix}_/` is scoped to that publisher
- Patch solutions — intentionally unsupported
