---
title: Web Resource Naming — Verbatim Mode and Shared Namespace
date: 2026-06-12
status: idea
origin: docs/Features/FR-webresource-naming.md
partially-implemented: auto-prefix mode is live (WebResourceReader.cs)
---

# Web Resource Naming — Verbatim Mode and Shared Namespace

## What's Already Implemented

**Auto-prefix** (default): files not starting with `{publisher_prefix}_` get prefixed automatically:

```
{root}/js/app.js  →  av_MySolution/js/app.js
```

Implemented in `WebResourceReader.cs`. This covers the majority of projects.

---

## What's Missing

### Verbatim Mode

Files whose top-level folder **starts with** `{publisher_prefix}_` use the folder path as the CRM
name verbatim — no additional prefix or solution name prepended.

```
{root}/av_MySolution/js/app.js  →  av_MySolution/js/app.js   (same as auto-prefix result)
{root}/av_/lib/jquery.js        →  av_/lib/jquery.js          (shared namespace)
```

Detection rule (requires knowing the publisher prefix at push time):
- First path segment starts with `{prefix}_` → verbatim mode for that folder
- Otherwise → auto-prefix mode

### Shared Namespace: `{prefix}_/`

The `{prefix}_/` top-level folder (prefix + `/`, no solution segment) is the publisher-scoped
shared namespace. Files here can be referenced by any solution from the same publisher.

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
- Cross-publisher shared resources — `{prefix}_/` is publisher-scoped
- Patch solutions — intentionally unsupported
