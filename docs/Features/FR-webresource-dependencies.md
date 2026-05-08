# Feature Request: Web Resource Dependencies

## Background

Dataverse supports defining **dependencies between web resources**. When a web resource is
loaded at runtime (e.g. a JavaScript file on a form), any web resources registered as its
dependencies are automatically loaded by the platform alongside it.

Two dependency scenarios are required for correct runtime behaviour:

1. **RESX → JS**: Localisation string files that must be loaded alongside the JS that uses
   them — without this, `getResourceString` returns `null`
2. **JS → JS**: Shared libraries (jQuery, utility scripts) that a JS file requires — without
   this, the dependent resource is not guaranteed to be fetched alongside the parent
   (note: load *execution* order is still not guaranteed even with dependencies registered —
   all web resources load asynchronously in parallel)

A third scenario is supported for **solution integrity** only (not required for runtime):

3. **HTML → JS/CSS**: An HTML web resource references CSS and JS via relative URLs in
   normal `<script src>` / `<link href>` attributes. Since HTML loads in an iframe, the
   browser resolves and fetches those files directly — no dependency registration needed for
   runtime. Registering the dependency only prevents accidental deletion of the referenced
   resources in a managed solution context.

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
- HTML web resources that reference CSS and JS via `$webresource:` load those files
  correctly via normal browser mechanics — but dependency relationships are not registered
  in Dataverse for solution integrity purposes
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

**As a developer building HTML web resources**, I want `flowline push` to automatically
detect the CSS and JS files my HTML references so I don't have to manually wire up
dependencies in the maker portal.

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

> ⚠️ **Load order is not guaranteed.** Microsoft docs state: "Web resource dependencies
> does not provide any control over the order in which the web resources are loaded. All the
> web resources are loaded asynchronously and in parallel." JS-to-JS dependency registration
> ensures the dependent resource is available in Dataverse and will be fetched alongside the
> parent — it does not guarantee that the dependency has finished executing before the
> dependent script runs. If execution order matters, use module patterns or explicit
> initialisation guards within the scripts themselves.

### 3. HTML → JS/CSS: Optional Annotation (Solution Integrity Only)

HTML web resources load in an iframe — CSS and JS files are referenced using **relative
URLs** in normal `<script src>` and `<link href>` attributes. The browser resolves and
fetches these files directly. No Dataverse dependency registration is required for this to
work at runtime — the platform serves the HTML file and the browser handles the rest.

Microsoft's guidance is explicit: "From another web resource, always use relative URLs to
reference each other." The `$webresource:` directive is for ribbon XML, sitemap XML, and
form XML — not for HTML content.

Registering a dependency is useful only for **solution integrity**: if you want Dataverse
to prevent accidental deletion of a JS or CSS file that an HTML resource references
(relevant in managed solution contexts), you can declare it explicitly:

```html
<!-- flowline:depends av_mysolution/css/styles.css -->
<!-- flowline:depends av_mysolution/lib/jquery.js -->
```

Since this is optional and the benefit is limited to unmanaged-to-managed promotion
scenarios, Flowline will register these if declared but will not auto-detect them from HTML
`src`/`href` attributes (that would silently add dependencies the developer may not intend).

### 4. In-Source Annotation for RESX (Ambiguous Cases)

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
  - RESX base-name matching (auto-detection for RESX → JS)
  - `// flowline:depends` annotations parsed from JS file content (JS → JS and RESX overrides)
  - `<!-- flowline:depends -->` annotations parsed from HTML file content (optional, solution integrity only)
2. For each JS or HTML web resource that has a desired dependency set (or had one previously): retrieve its current `dependencyxml` field from Dataverse
3. Diff: build the new XML adding missing dependencies, removing stale ones
4. If changed: write back via `UpdateRequest` and mark for publish
5. Publish all web resources whose `dependencyxml` was modified
6. Log changes:
   ```
   MyForm.1033.resx → MyForm.js  [dependency registered]
   MyForm.1043.resx → MyForm.js  [dependency registered]
   lib/jquery.js → MyForm.js     [dependency registered]
   lib/old-utils.js → MyForm.js  [dependency removed — no longer declared]
   ```

Dependency sync always runs after web resource sync since both ends of the dependency must
exist in Dataverse before the relationship can be written. Dependencies do not take effect
until the parent web resource is published.

---

## SDK Implementation

### Storage mechanism

Dependencies are stored in the `dependencyxml` field directly on the `webresource` entity.
There is no separate relationship table — the field is a memo (string, max 5000 chars) that
holds an XML blob describing which other web resources must be loaded alongside this one.

| Field | Details |
|---|---|
| Logical name | `dependencyxml` |
| Type | Memo |
| Max length | 5000 chars |
| Note | Marked "For internal use only" in entity metadata — the XML format is not officially documented |

Setting dependencies is an `UpdateRequest` on the parent web resource:

```csharp
var webResource = new Entity("webresource", parentWebResourceId);
webResource["dependencyxml"] = BuildDependencyXml(dependentWebResourceNames);

var request = new UpdateRequest { Target = webResource };
await service.ExecuteAsync(request, cancellationToken);
```

Reading current dependencies requires retrieving the field:

```csharp
var existing = await service.RetrieveAsync(
    "webresource", parentWebResourceId,
    new ColumnSet("dependencyxml"),
    cancellationToken);

var currentXml = existing.GetAttributeValue<string>("dependencyxml");
```

Publishing is required after setting dependencies — they do not take effect until the web
resource is published.

### DependencyXml format

> ⚠️ **Needs empirical verification.** The `dependencyxml` field is marked "For internal
> use only" and the XML schema is not officially documented by Microsoft. The format below
> is based on community observations and may change across platform versions. **Before
> implementing, export a solution containing manually configured web resource dependencies
> and read back the `dependencyxml` field value to confirm the exact schema.**

Based on community knowledge, the expected format is:

```xml
<Dependencies>
  <Dependency componentType="31">
    <WebResourceDependency name="av_mysolution/strings/Labels.resx" />
  </Dependency>
  <Dependency componentType="31">
    <WebResourceDependency name="av_mysolution/lib/jquery.js" />
  </Dependency>
</Dependencies>
```

Where:
- `componentType="31"` is the Dataverse component type for web resources
- `name` is the logical name (full path) of the dependent web resource
- An empty or null `dependencyxml` means no dependencies

The safest implementation approach is:
1. Read the current `dependencyxml` value before writing
2. Parse the existing XML to get current dependencies
3. Compute the desired set (from auto-detection + annotations)
4. Diff: add missing, remove stale
5. Serialise back to XML and write with `UpdateRequest`
6. Publish the web resource

This read-modify-write pattern handles partial syncs and does not blindly overwrite
dependencies set by other tools or the maker portal.

---

## Scope

| Scenario | Approach |
|---|---|
| RESX → JS (localisation) | Auto-detect by base name matching |
| RESX → JS (ambiguous/override) | `flowline:depends` annotation in JS |
| JS → JS (shared libraries, e.g. jQuery) | `flowline:depends` annotation in JS |
| HTML → JS/CSS (solution integrity only) | `<!-- flowline:depends -->` annotation in HTML — optional, not needed for runtime |
| Remove stale dependencies on push | ✅ |
| `flowline deploy` (pac solution import) | ✅ Already works — dependencies survive in solution XML |

---

## Out of Scope

- **Column dependencies** — A related but distinct Dataverse feature where a JavaScript web
  resource declares which table columns it accesses on a form (so Dataverse includes them in
  the data load even if they have no form control). These are stored in **form XML**, not on
  the web resource entity. Flowline does not manage forms, so this belongs in a future form
  management feature if one is ever added.
- CSS → anything (CSS files have no Flowline-parseable annotation syntax)
- Validating that RESX files are well-formed XML
- Translating or generating RESX content (Flowline syncs files as-is)
- LCID validation (Flowline trusts the developer's filenames)
- Resolving transitive dependencies (A → B → C is not walked automatically)

---

## References

- [Web resource dependencies — Microsoft Learn](https://learn.microsoft.com/en-us/power-apps/developer/model-driven-apps/web-resource-dependencies)
- [Web resource dependencies (on-premises, confirms load order behaviour) — Microsoft Learn](https://learn.microsoft.com/en-us/dynamics365/customerengagement/on-premises/developer/web-resource-dependencies?view=op-9-1)
- [String (RESX) web resources — Microsoft Learn](https://learn.microsoft.com/en-us/power-apps/developer/model-driven-apps/resx-web-resources)
- [WebResource entity reference (`dependencyxml` field) — Microsoft Learn](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/reference/entities/webresource)
