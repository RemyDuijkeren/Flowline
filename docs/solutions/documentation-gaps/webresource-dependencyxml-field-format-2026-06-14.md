---
title: "Dataverse webresource.dependencyxml Field Format — Confirmed Empirically"
date: 2026-06-14
category: docs/solutions/documentation-gaps
module: web-resources
problem_type: documentation_gap
component: dependency-registration
severity: high
applies_when:
  - Implementing web resource dependency registration via the dependencyxml field
  - Writing or reading the dependencyxml Memo field on the webresource entity
  - Building a DependencyXmlSerializer for flowline push
tags:
  - dataverse
  - webresource
  - dependencyxml
  - dependency-registration
  - sdk
  - xml-format
---

# Dataverse webresource.dependencyxml Field Format — Confirmed Empirically

## Context

The `dependencyxml` field on the `webresource` entity is documented only as "for internal use only" in the Dataverse SDK. Community sources suggested a format, but that format was not verified. This document records the confirmed format obtained by manually configuring a web resource dependency via the Maker Portal, running `flowline sync`, and reading the PAC-unpacked `.data.xml` output.

Test setup: `example3.js` was given two JavaScript library dependencies (`example1.js`, `example2.js`) via the Maker Portal dependency editor.

Format confirmed via two sources: (1) PAC-unpacked `.data.xml` file (PAC reads `dependencyxml` from the SDK and HTML-encodes it for the XML wrapper — decoding yields the exact raw field value), and (2) direct Dataverse Web API query (`GET /api/data/v9.2/webresourceset?$select=name,dependencyxml`).

## Confirmed Field Format

**Empty / no dependencies:** The `<DependencyXml>` element is absent from the `.data.xml` file entirely. The raw field value in Dataverse is null or empty string. Example: `example4.js.data.xml` has no `<DependencyXml>` element.

**With dependencies (raw field value, HTML-decoded from PAC output):**

```xml
<Dependencies>
  <Dependency componentType="WebResource">
    <Library name="av_Cr07982/example1.js"
             displayName="example1.js"
             languagecode=""
             description=""
             libraryUniqueId="{0e58647c-5eb8-e4cc-b94d-19e6acb09469}"/>
    <Library name="av_Cr07982/example2.js"
             displayName="example2.js"
             languagecode=""
             description=""
             libraryUniqueId="{0189e308-1bd6-e674-d7ee-db73a97a896e}"/>
  </Dependency>
</Dependencies>
```

**Structure:**
- Root: `<Dependencies>`
- Single `<Dependency componentType="WebResource">` child (one per resource, not one per Library)
- One `<Library>` element per dependency
- All `<Library>` siblings live under the single `<Dependency>` node

**`<Library>` attributes:**

| Attribute | Value | Notes |
|---|---|---|
| `name` | `av_Cr07982/example1.js` | Full logical name — this is the dependency resolution key |
| `displayName` | `example1.js` | Human-readable name — the `displayname` field of the dependency resource |
| `languagecode` | `""` | Always empty for JS library dependencies |
| `description` | `""` | Always empty when set via Maker Portal |
| `libraryUniqueId` | `{0e58647c-...}` | GUID; see note below |

## Key Deviations From Brainstorm Expected Format

The brainstorm (`docs/brainstorms/2026-06-12-webresource-dependencies-requirements.md`) expected:

```xml
<Dependencies>
  <Dependency componentType="31">
    <WebResourceDependency name="..." />
  </Dependency>
</Dependencies>
```

**Actual differs in three ways:**
1. `componentType="WebResource"` — not `"31"`
2. `<Library>` element — not `<WebResourceDependency>`
3. Five attributes on `<Library>` — not just `name`

## Write Behavior Confirmed

`dependencyxml` is **writable via `PATCH /api/data/v9.2/webresourceset({id})`** (confirmed empirically):

| Test | Input | Result |
|---|---|---|
| Valid XML, 1 Library, fresh GUID | `<Dependencies>...<Library .../></Dependency></Dependencies>` | HTTP 204 — field updated, Maker Portal shows 1 dependency |
| null | `{"dependencyxml": null}` | HTTP 200 — field cleared, Maker Portal shows "no dependencies" |
| Invalid string | `"HELLO_WORLD"` | HTTP 400 — rejected, field unchanged |
| Restore original | original 2-Library XML | HTTP 204 — field updated |

**Fresh GUIDs for `libraryUniqueId` are accepted.** Dataverse does not validate `libraryUniqueId` against any entity table — any valid GUID works. The Maker Portal displays the dependency by `name`, not by GUID.

**`return=representation` returns stale data.** When `Prefer: return=representation` is included, the response body shows the pre-update value, not the new value. Do not rely on it to verify the write. Use a separate GET to confirm.

**Dataverse validates XML structure.** Invalid XML returns HTTP 400. The XML must be well-formed; structure validation (element names, attributes) is also enforced since "HELLO_WORLD" was rejected.

## libraryUniqueId Is Not the webresourceid

Cross-referencing the two `.data.xml` files:

| Resource | WebResourceId | libraryUniqueId in example3's deps |
|---|---|---|
| example1.js | `{1f27c895-8167-f111-ab0d-3833c5c10c9e}` | `{0e58647c-5eb8-e4cc-b94d-19e6acb09469}` |
| example2.js | `{47dada9b-8167-f111-ab0d-3833c5c10c9e}` | `{0189e308-1bd6-e674-d7ee-db73a97a896e}` |

They are different. `libraryUniqueId` is not the `webresourceid`. It appears to be a GUID generated by the Maker Portal at the time the dependency is created — a stable local identifier for this Library entry within the XML. Dataverse stores and returns it as-is (it does not validate or enforce it against any entity table).

**Implementation implication:** When creating a new dependency entry, generate a new `Guid.NewGuid()`. For the read-modify-write pattern: deserialize to extract existing `name → libraryUniqueId` pairs, reuse existing GUIDs for unchanged dependencies, generate new GUIDs only for newly added dependencies. This preserves stability in PAC-managed `.data.xml` diffs.

## LCID Pattern Confirmation

The investigation used JavaScript dependencies only. RESX `.{lcid}.resx` files were not tested in this spike. The four-digit LCID suffix pattern assumption in the plan remains unconfirmed for this specific environment but is consistent with Dataverse conventions.

## Design Implications for DependencyXmlSerializer (U2)

The serializer cannot work with `IReadOnlySet<string>` alone. Required type:

```csharp
// New record in WebResourceModels.cs
public record DependencyLibrary(string Name, string DisplayName, Guid LibraryUniqueId);
```

- **Deserialize:** `string? → IReadOnlySet<DependencyLibrary>` — extracts all three values from `<Library>` attributes
- **Serialize:** `IReadOnlySet<DependencyLibrary> → string?` — produces the confirmed XML; returns null for empty set

The Planner (U4) resolves the full `DependencyLibrary` set before calling Serialize:
- For existing dependencies (unchanged): look up `DependencyLibrary` from the deserialized current set → reuse `LibraryUniqueId`
- For new dependencies: look up `DisplayName` from the snapshot (local or Dataverse resource), generate `Guid.NewGuid()` for `LibraryUniqueId`

## Source Files

- `C:\Users\RemyvanDuijkeren\Code\TryOut\MyFlowTest\solutions\Cr07982\Package\src\WebResources\av_Cr07982\example3.js.data.xml` — resource with two dependencies
- `C:\Users\RemyvanDuijkeren\Code\TryOut\MyFlowTest\solutions\Cr07982\Package\src\WebResources\av_Cr07982\example1.js.data.xml` — first dependency (no deps)
- `C:\Users\RemyvanDuijkeren\Code\TryOut\MyFlowTest\solutions\Cr07982\Package\src\WebResources\av_Cr07982\example2.js.data.xml` — second dependency (no deps)
- `C:\Users\RemyvanDuijkeren\Code\TryOut\MyFlowTest\solutions\Cr07982\Package\src\WebResources\av_Cr07982\example4.js.data.xml` — resource with no dependencies (no DependencyXml element)

## Related

- `docs/brainstorms/2026-06-12-webresource-dependencies-requirements.md` — original brainstorm (format assumption now corrected)
- `docs/plans/2026-06-13-002-feat-webresource-dependency-registration-plan.md` — implementation plan (U2 serializer design must be updated per above)
