# Feature Request: Web Resource Dependencies

## Background

Dataverse supports defining **dependencies between web resources**. When a web resource is
loaded at runtime (e.g. a JavaScript file on a form), any web resources registered as its
dependencies are automatically loaded by the platform alongside it.

Two dependency scenarios are relevant for `flowline push`:

1. **RESX → JS**: Localisation string files that must be loaded alongside the JS that uses
   them
2. **JS → JS**: Shared libraries (jQuery, utility scripts) that a JS file requires

---

## RESX Web Resources

RESX web resources (type `12`) store localised string key-value pairs in XML format, one
file per language. They are the recommended approach for multilingual model-driven app
development.

**Naming convention:**

```
{prefix}_{solution}/{path}/{name}.{lcid}.resx
```

| Example | Language |
|---|---|
| `av_mysolution/strings/Labels.1033.resx` | English (US) |
| `av_mysolution/strings/Labels.1043.resx` | Dutch |
| `av_mysolution/strings/Labels.1036.resx` | French |
| `av_mysolution/strings/Labels.2052.resx` | Chinese (Simplified) |

**Client-side access:**

```javascript
// No LCID in the call — Dataverse resolves the user's language automatically
const label = Xrm.Utility.getResourceString(
    "av_mysolution/strings/Labels",
    "MyKey"
);
```

Without a registered dependency, `getResourceString` returns `null` at runtime even if the
RESX exists in Dataverse.

---

## Problem

`flowline push` syncs web resources but does not create or maintain dependency relationships
between them. This means:

- RESX files are uploaded correctly but not linked to their parent JS web resource —
  `getResourceString` returns `null` at runtime
- Shared JS libraries are uploaded but not registered as dependencies — load order is not
  guaranteed
- Developers must manually create dependencies in the maker portal after every push, or
  rely on solution import to carry them across (which works but defeats the purpose of
  `flowline push`)

---

## User Story

**As a developer building a multilingual model-driven app**, I want `flowline push` to
automatically register RESX web resources as dependencies of their parent JS web resource,
so that `getResourceString` works correctly without any manual steps.

**As a developer using shared JavaScript libraries**, I want to declare a dependency in my
source file so that `flowline push` registers it in Dataverse and the load order is
guaranteed.

---

## Convention-Based Dependency Detection

Flowline uses **convention over configuration** — no external config files. Dependencies
are inferred from naming patterns and in-source annotations.

### 1. RESX → JS: Base Name Matching

A RESX file is automatically registered as a dependency of a JS web resource when their
base names match, regardless of which subdirectory they live in.

**Rule:** `{anyPath}/{name}.{lcid}.resx` depends on `{anyOtherPath}/{name}.js`

**Example:**

```
webresources/
  scripts/
    MyForm.js              ← parent (matched by base name "MyForm")
  strings/
    MyForm.1033.resx       ← dependency → MyForm.js
    MyForm.1043.resx       ← dependency → MyForm.js
    MyForm.1036.resx       ← dependency → MyForm.js
```

Flowline scans all `.resx` files, strips the LCID segment and extension to get the base
name, then looks for a JS file with that base name anywhere in the web resources tree. If
exactly one match is found, the dependency is registered automatically.

If zero or multiple JS files match the same base name, Flowline logs a warning and skips
auto-registration for that RESX group — ambiguous cases require an in-source annotation
(see below) to resolve.

### 2. JS → JS: In-Source Annotation

Shared library dependencies are declared directly in the JavaScript source file using a
Flowline comment convention:

```javascript
// flowline:depends av_mysolution/lib/jquery.js
// flowline:depends av_mysolution/lib/utils.js

(function () {
    // your code here
})();
```

**Rules:**
- The comment must appear at the top of the file, before any code
- The path must be the full web resource logical name (with publisher prefix)
- Multiple `flowline:depends` lines are allowed
- Flowline registers each named resource as a dependency of the JS file being pushed

This approach keeps the dependency declaration version-controlled alongside the code that
requires it, with no separate config file.

### 3. In-Source Annotation for RESX (Ambiguous Cases)

The same annotation syntax resolves ambiguous RESX matches or overrides auto-detection:

```javascript
// flowline:depends av_mysolution/strings/SharedLabels.resx
```

The `.resx` extension without a LCID tells Flowline to expand to all matching language
variants (`SharedLabels.1033.resx`, `SharedLabels.1043.resx`, etc.). This is distinct from
referencing a specific language file (`SharedLabels.1033.resx`) which would register only
that one variant.

---

## Sync Behaviour

During `flowline push`, after all web resources are created/updated:

1. Compute desired dependency set from:
   - RESX base-name matching (auto-detection)
   - `flowline:depends` annotations parsed from JS file content
2. Query current dependencies from Dataverse
3. Diff: associate missing dependencies, disassociate stale ones
4. Log changes:
   ```
   MyForm.1033.resx → MyForm.js  [dependency registered]
   MyForm.1043.resx → MyForm.js  [dependency registered]
   lib/jquery.js → MyForm.js     [dependency registered]
   lib/old-utils.js → MyForm.js  [dependency removed — no longer declared]
   ```

Dependency sync always runs after web resource sync since both ends of the association must
exist before the link can be created.

---

## SDK Implementation

Dependencies are managed via `AssociateRequest` on the `webresource` entity:

```csharp
var request = new AssociateRequest
{
    Target     = new EntityReference("webresource", parentJsWebResourceId),
    Relationship = new Relationship("webresource_resource_resx"),
    RelatedEntities = new EntityReferenceCollection
    {
        new EntityReference("webresource", dependentWebResourceId)
    }
};
await service.ExecuteAsync(request, cancellationToken);
```

> ⚠️ **Verify before implementing**: The exact relationship name
> (`webresource_resource_resx`) and whether the same relationship is used for both RESX and
> JS-to-JS dependencies needs confirmation. See Microsoft docs:
> [Web resource dependencies](https://learn.microsoft.com/en-us/power-apps/developer/model-driven-apps/web-resource-dependencies)

---

## Scope

| Scenario | Approach |
|---|---|
| RESX → JS (localisation) | Auto-detect by base name matching |
| RESX → JS (ambiguous/override) | `flowline:depends` annotation |
| JS → JS (shared libraries, e.g. jQuery) | `flowline:depends` annotation |
| Remove stale dependencies on push | ✅ |
| `flowline deploy` (pac solution import) | ✅ Already works — dependencies survive in solution XML |
| HTML → JS or other types | ❌ Out of scope |

---

## Out of Scope

- HTML-to-JS or other non-JS dependency types
- Validating that RESX files are well-formed XML
- Translating or generating RESX content (Flowline syncs files as-is)
- LCID validation (Flowline trusts the developer's filenames)
- Resolving transitive dependencies (A → B → C is not walked automatically)

---

## References

- [Web resource dependencies — Microsoft Learn](https://learn.microsoft.com/en-us/power-apps/developer/model-driven-apps/web-resource-dependencies)
- [String (RESX) web resources — Microsoft Learn](https://learn.microsoft.com/en-us/power-apps/developer/model-driven-apps/resx-web-resources)
