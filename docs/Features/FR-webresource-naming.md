# Feature Request: Web Resource Naming and Shared Web Resources

## Background

Every web resource pushed to Dataverse must have a unique logical name within the
organisation. That name determines how the resource is referenced in form XML, ribbon XML,
and client-side code (`Xrm.Utility.getResourceString`, `$webresource:`). Getting it wrong —
a collision, a missing prefix, a flat name with no path information — causes silent runtime
failures or cross-solution interference.

Dataverse's own upload UI defaults to stripping the file extension and flattening the path
into a bare `{prefix}_filename` string. This loses structural information and makes the name
unrecognisable in the web resource list. Flowline does not adopt this default.

This document covers:
- How Flowline derives CRM names from local file paths (auto-prefix and verbatim)
- The shared namespace (`{prefix}_/`) for publisher-scoped resources used across solutions
- Collision detection
- How the naming convention affects orphan cleanup ownership detection

For dependency registration between web resources, and for the orphan cleanup exemption that
applies to dependency-referenced non-local files, see
[FR-webresource-dependencies.md](FR-webresource-dependencies.md).

---

## Web Resource Root

The web resource root is the folder Flowline syncs to Dataverse. All naming rules apply
relative to this root — the root folder name itself never appears in the CRM name.

| Mode | Default root | Configured by |
|---|---|---|
| Project mode | `dist/` | Convention — standard web frontend build output |
| Standalone mode | Any folder | `.flowline` config or CLI argument |

---

## Naming Convention

### Default: Auto-prefix

Files whose top-level folder does **not** start with `{publisher_prefix}_` are automatically
prefixed with the publisher prefix and solution name:

```
CRM name = {publisher_prefix}_{solution_name}/{relative_path}
```

Examples (publisher prefix `av`, solution `MySolution`):

```
{root}/js/app.js              →  av_MySolution/js/app.js
{root}/images/logo.png        →  av_MySolution/images/logo.png
{root}/strings/Labels.resx    →  av_MySolution/strings/Labels.resx
```

This is the default path. Developers add files to the root and get correctly scoped CRM
names with no knowledge of Dataverse naming conventions required. It is safe for beginners
and for the majority of projects where one solution owns its web resources entirely.

### Opt-in: Verbatim

Files whose top-level folder **starts with** `{publisher_prefix}_` use the folder path as
the CRM name verbatim — no prefix or solution name is prepended.

```
CRM name = {top-level-folder}/{relative_path}
```

Examples:

```
{root}/av_MySolution/js/app.js   →  av_MySolution/js/app.js   (same result as auto-prefix)
{root}/av_/lib/jquery.js         →  av_/lib/jquery.js          (shared — see below)
```

The verbatim path for a solution's own files produces the same CRM name as auto-prefix —
it just makes the name explicit in the folder structure. This is useful when the build step
or another tool produces output with the full CRM path already encoded, or for teams who
want zero ambiguity about what ends up in Dataverse.

### Detection rule

At push time, Flowline reads the publisher prefix from Dataverse. For each file in the web
resource root, it checks the first path segment:

- Starts with `{publisher_prefix}_` → **verbatim** mode for everything under that folder
- Does not start with `{publisher_prefix}_` → **auto-prefix** mode

The check is deterministic and requires no configuration. A developer would not accidentally
create a top-level folder starting with the publisher prefix for any other reason.

---

## Shared Namespace: `{prefix}_/`

The `{prefix}_/` folder — publisher prefix followed immediately by `/`, with no solution
name segment — is the **publisher-scoped shared namespace**.

```
{root}/av_/lib/jquery.js        →  av_/lib/jquery.js
{root}/av_/images/shared.png    →  av_/images/shared.png
{root}/av_/css/theme.css        →  av_/css/theme.css
```

Files in this namespace have no solution name in their CRM path. They can be referenced by
any solution from the same publisher. This works for all file types — JS, CSS, images, RESX,
HTML — because the naming is derived from folder structure, not from file content or
annotations.

### When to use shared web resources

Shared web resources are **rare** in Dataverse projects. For most use cases, having a
per-solution copy of a file is the correct trade-off: simpler, independently versioned, no
cross-solution coupling. Prefer npm workspaces or build-tool bundling to include shared
source in each solution's own dist output under its own namespace.

Use the shared namespace only when the resource genuinely needs to be **the same CRM
record** across multiple solutions — typically when:

- Multiple solutions declare a Dataverse runtime dependency on the same file (see
  [FR-webresource-dependencies.md](FR-webresource-dependencies.md))
- A publisher-level asset must stay in sync as a single record and cannot be duplicated

### File placement

The developer is responsible for populating the `{prefix}_/` folder via build or npm
scripts. Flowline syncs what is present in the web resource root — it does not wire up
shared file placement or cross-project references. This keeps the build pipeline in control
of what ends up in `dist/`, as it already is for solution-scoped files.

```
dist/                           ← web resource root (project mode)
├── js/app.js                  →  av_MySolution/js/app.js     (auto-prefix)
├── images/logo.png            →  av_MySolution/images/logo.png
└── av_/                       →  verbatim, shared namespace
    ├── lib/jquery.js          →  av_/lib/jquery.js
    └── images/shared.png      →  av_/images/shared.png
```

### Multi-solution ownership

The existing ownership-aware orphan logic applies to shared files without modification:

| Scenario | Flowline action |
|---|---|
| Another solution also references `av_/lib/jquery.js` in Dataverse | Update content — warn that other solutions will receive the change |
| File removed from current solution's root, still present in another solution | Remove from current solution's components — keep the record in Dataverse |
| File removed from all solutions' roots | Delete the web resource |

No new ownership mechanism is required — the `solutioncomponent` query that already drives
orphan decisions works identically for shared-namespace files.

### Impact on ownership stamp (FR-orphan-cleanup.md)

`FR-orphan-cleanup.md` uses the filename convention `{prefix}_{solution}/{path}` as an
ownership indicator: a web resource whose name matches this pattern is treated as
Flowline-managed. Shared-namespace files (`{prefix}_/{path}`) do not match this pattern.

For shared files, the description stamp (`[flowline:solution=MySolution]`) written by the
solution that first pushed the file is the ownership indicator. Subsequent pushes from other
solutions that add the file to their components do not overwrite the stamp — the original
solution's stamp is preserved.

---

## Collision Detection

If auto-prefix and verbatim paths in the same web resource root resolve to the same CRM
name, Flowline errors at plan time before any Dataverse calls are made:

```
Error: two local files resolve to the same CRM name 'av_MySolution/js/app.js':
  js/app.js          (auto-prefix)
  av_MySolution/js/app.js  (verbatim)
Remove one or ensure they have distinct CRM names.
```

---

## Scope

| Scenario | Approach |
|---|---|
| Default file naming | Auto-prefix: `{prefix}_{solution}/{path}` |
| Explicit or shared file naming | Verbatim: top-level folder starts with `{prefix}_` |
| Publisher-shared namespace | `{prefix}_/` top-level folder — no solution segment |
| Multi-solution ownership for shared files | Existing `solutioncomponent` ownership logic |
| Collision detection | Error at plan time — before any Dataverse calls |
| Orphan exemption for dependency-referenced non-local files | See [FR-webresource-dependencies.md](FR-webresource-dependencies.md) |
| Ownership stamp for orphan cleanup | Filename pattern for solution-scoped; description stamp for shared-namespace |

---

## Out of Scope

- Dataverse's default naming (`{prefix}_filename`, no extension) — Flowline does not adopt
  this convention
- Configuration-based name overrides — all naming is derived from folder structure
- Cross-publisher shared resources — the `{prefix}_/` namespace is publisher-scoped;
  sharing across publishers requires a separate dedicated solution
- Patch solution support — intentionally unsupported in Flowline