---
title: Plugin NuGet Package Support (Dependent Assemblies)
date: 2026-06-12
status: idea
origin: docs/Features/FEATURE_PLUGIN_NUGET_PACKAGES.md
---

# Plugin NuGet Package Support (Dependent Assemblies)

## Problem

Classic plugin DLLs with external NuGet dependencies must merge everything with ILMerge/ILRepack
before upload. ILMerge is unmaintained; ILRepack adds build complexity. Dataverse's Dependent
Assemblies feature (`.nupkg` path via `pluginpackage` table) eliminates this entirely.

---

## How Dependent Assemblies Works

1. `pac plugin init` project emits both `Plugins.dll` and `Plugins.nupkg` on build
2. `.nupkg` uploaded to `pluginpackage` Dataverse table
3. Dataverse auto-creates a linked `pluginassembly` record (read-only — reflects the DLL inside)
4. Steps registered on `pluginassembly` exactly as today — step API unchanged
5. Runtime: Dataverse extracts package contents, resolves dependencies in sandbox

---

## Detection

During `flowline push`, check build output:
- `.nupkg` present alongside the assembly name → use NuGet package path
- `.dll` only → use classic path (no change to existing behaviour)

Consider an explicit `--packages` flag as a safer initial approach to avoid ambiguity when both
`.dll` and `.nupkg` exist.

---

## Upload: `pluginpackage` Table

1. Query `pluginpackage` by unique name — check if exists
2. **Create:** POST new record with `.nupkg` content (base64-encoded in `content` attribute)
3. **Update:** compare SHA-256 of local `.nupkg` against stored hash in `description`; skip if unchanged
4. After upload: read auto-created `pluginassembly` via `pluginpackage_pluginassembly` relationship → get assembly ID for step registration

Store hash in `pluginpackage.description` (same pattern as `pluginassembly.description` for classic path).

---

## Assembly Reflection

Build output still produces `.dll` alongside `.nupkg` — use the `.dll` for `MetadataLoadContext`
reflection as today. No need to extract from the package.

---

## `Mapping.xml` Implications

Classic: `<FileToPackage path="PluginAssemblies\**\Plugins.dll" packageType="Both" />`
Package path: `<FileToPackage path="PluginPackages\**\Plugins.nupkg" packageType="Both" />`

Open question: does SolutionPackager / `pac solution pack` support `pluginpackage` entries? If not,
Flowline deploys the `.nupkg` out-of-solution (same as it deploys the `.dll` today).

---

## Constraints

| Constraint | Detail |
|---|---|
| No workflow activities | `CodeActivity` cannot use the package path — must stay on classic `.dll`. Flowline should detect and error. |
| Size limit | Max 16 MB or 50 assemblies per package |
| Cloud only | Not supported on-premises |
| Signing | Package path does not require signing. If any assembly inside is signed, all its dependencies must also be signed. |
| Immutable `pluginassembly` | Auto-created by Dataverse. Update the parent `pluginpackage`, never the assembly directly. |
| Immutable name/version | Once created, package name and version cannot be changed via API. |

---

## Open Questions

1. Does `pac solution pack` handle `pluginpackage` in solution XML, or must Flowline treat package
   upload as out-of-solution?
2. Correct attribute name for package content on `pluginpackage` — verify with SDK metadata.
3. Migration from existing `pluginassembly` + steps: automatic when `.nupkg` detected, or require
   `--migrate` flag to avoid accidental data loss?
4. Environment-level feature flag requirements for Dependent Assemblies?
5. Does current push validate assembly signing before upload? If not, add upfront check independently.
