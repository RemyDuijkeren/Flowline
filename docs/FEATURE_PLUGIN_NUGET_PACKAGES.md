# Feature: Plugin NuGet Package Support (Dependent Assemblies)

**Status:** Not started — requirements only  
**Priority:** Medium  
**Relates to:** `flowline push`, `Flowline.Attributes`, `Mapping.xml`

---

## Background

Dataverse's **Dependent Assemblies** feature lets you upload a `.nupkg` instead of a plain `.dll` as your plugin assembly. Dataverse stores the package in the `pluginpackage` table, automatically creates the corresponding `pluginassembly` record, and extracts the package contents into the sandbox at runtime.

The manual workflow today is: build → use "Register New Package" in Plugin Registration Tool → register steps on top. Flowline currently only supports the classic `.dll` path (`pluginassembly` table). This feature adds support for the `.nupkg` path.

Reference:  
- [Dataverse Dependent Assemblies — Rajeev Pentyala (2022)](https://rajeevpentyala.com/2022/08/30/step-by-step-dataverse-dependent-assemblies/)  
- [Microsoft docs — Create a plug-in package](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/dependent-assembly-plugins)

---

## Motivation

### The ILMerge problem

When a classic plugin DLL depends on external NuGet packages, those package DLLs cannot be deployed separately — Dataverse only accepts a single assembly. The only workaround is to merge all dependencies into the DLL using ILMerge or ILRepack before upload.

Both tools are problematic:
- **ILMerge** is no longer actively maintained by Microsoft. The Microsoft docs state explicitly: *"Microsoft doesn't support ILMerge."*
- **ILRepack** ([github.com/gluck/il-repack](https://github.com/gluck/il-repack)) is a community fork that works but adds build complexity and occasional merge conflicts with signed assemblies.
- Both require explicit MSBuild configuration and can silently produce broken assemblies when dependency versions change.

Dependent Assemblies eliminates this entirely. The `.nupkg` bundles all dependencies exactly as NuGet resolves them — no extra tooling, no merge step. Microsoft describes it as offering *"the same functionality as ILMerge and more."*

### What you get

- Include any NuGet package as a plugin dependency without ILMerge/ILRepack.
- Assemblies inside the package are resolved by Dataverse at runtime, not at build time.
- The project type (`pac plugin init`) is already what Flowline's `clone` command scaffolds — the `.nupkg` artifact is produced on every build automatically. No project changes needed for projects that want to use this path.

---

## How Dependent Assemblies works in Dataverse

1. `pac plugin init` creates a .NET Framework class library project. On `dotnet build` (or MSBuild), this project emits both `Extensions.dll` and `Extensions.nupkg` in the output folder.
2. The `.nupkg` is uploaded to the `pluginpackage` Dataverse table.
3. Dataverse automatically creates a `pluginassembly` record linked to the package. This record is read-only — it reflects the DLL inside the package.
4. Plugin steps are registered on top of the `pluginassembly` record exactly as they are today. The step registration API does not change.
5. At runtime, Dataverse extracts the full package contents into the plugin sandbox and resolves dependencies from there.

---

## What Flowline needs to change

### Detection: `.nupkg` vs `.dll`

During `flowline push <solution>`, Flowline must determine which deployment path to use for each assembly:

- If the build output folder contains a `.nupkg` matching the assembly name → use the NuGet package path.
- If it only contains a `.dll` → use the classic path (no change to existing behaviour).

This keeps backward compatibility. Projects that have not adopted `pac plugin init` continue to work exactly as before.

### Upload: `pluginpackage` table

When uploading via the package path:

1. Query `pluginpackage` by unique name to check whether the package already exists.
2. **Create:** `POST` a new `pluginpackage` record with the `.nupkg` content (base64-encoded in the `content` attribute).
3. **Update:** If the package exists, compare a hash of the local `.nupkg` against a stored value and update the `content` attribute only when the file has changed (see hash strategy below).
4. After upload, read the auto-created `pluginassembly` record (linked via `pluginpackage_pluginassembly` relationship) to get the assembly ID needed for step registration.

Step registration (via Flowline.Attributes) does not change — it operates on `pluginassembly` as today.

### Hash strategy for update detection

Classic plugin push stores a SHA-256 hash of the DLL in `pluginassembly.description`. The equivalent for packages:

- Store a SHA-256 hash of the `.nupkg` file in `pluginpackage.description` (or a custom field if description is used for other purposes — confirm during implementation).
- On each push, compute the hash locally and compare. Skip the upload if hashes match.

### `Mapping.xml` implications

`clone` currently adds an entry like:

```xml
<FileToPackage path="PluginAssemblies\**\Extensions.dll" packageType="Both" />
```

When switching to Dependent Assemblies, the package file should be tracked instead:

```xml
<FileToPackage path="PluginPackages\**\Extensions.nupkg" packageType="Both" />
```

**Open question:** Does SolutionPackager / `pac solution pack` support `pluginpackage` files? If not, the `.nupkg` may need to live outside the solution folder and be deployed separately by Flowline — similar to how Flowline currently deploys the DLL independently of the solution import.

### `flowline push` flag (proposed)

Consider an opt-in flag rather than pure auto-detection, in case both a `.dll` and `.nupkg` exist in the output folder:

```bash
flowline push MySolution --packages   # use NuGet package path
flowline push MySolution              # use classic DLL path (default)
```

Auto-detection can be a follow-up once the basic path is working.

---

## Limitations

| Constraint | Detail |
|---|---|
| **No workflow activities** | `CodeActivity` classes are **not supported** in plugin packages. If the assembly contains workflow activities, it cannot use the package path — it must stay on the classic `.dll` path. Flowline should detect this and either warn or error. |
| **Size limit** | Maximum 16 MB or 50 assemblies per package. |
| **Cloud only** | Dependent Assemblies is not supported on-premises. Flowline should check (or document) that this feature requires an online environment. |
| **Signing** | Classic plugin assemblies (without Dependent Assemblies) **must** be signed before registration. Plugin packages **do not** require signing — assemblies inside a package are loaded via a different mechanism. However, if you choose to sign any assembly in the package, all assemblies it depends on must also be signed (mixing signed/unsigned causes a load error). See [Signed assemblies aren't required](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/build-and-package#signed-assemblies-are-not-required). |
| **Immutable `pluginassembly`** | The auto-created `pluginassembly` record is managed by Dataverse. Do not attempt to update it directly — update the parent `pluginpackage` record. |
| **Immutable package name/version** | Once a `pluginpackage` record is created, its name and version cannot be changed via API — Dataverse returns an error. Plan naming carefully. |
| **Large package import time** | Packages with hundreds to thousands of `IPlugin` classes can take up to 15 minutes to import. Unlikely to hit in practice (Flowline's one-class-per-step convention keeps counts low), but worth knowing if a project ever consolidates many steps. |

---

## Implementation notes

- The Dataverse SDK (`Microsoft.PowerPlatform.Dataverse.Client`) supports `pluginpackage` via standard entity CRUD — no special API needed.
- The `pluginpackage.content` attribute holds the raw package bytes. On retrieval it is base64-encoded; on create/update, pass the bytes directly or base64-encoded depending on the SDK method used — verify during implementation.
- `MetadataLoadContext` (already used by Flowline for assembly inspection) works on the DLL extracted from the `.nupkg`. To inspect the assembly without extracting it manually, either: (a) build output still produces the `.dll` alongside the `.nupkg` — use the `.dll` for reflection as today; or (b) extract the `.dll` from the `.nupkg` in memory using `System.IO.Compression.ZipArchive`.
- The `pluginassembly` linked to a `pluginpackage` has `isolationmode` set automatically. Do not set it manually.

---

## Open questions

1. Does `pac solution pack` / SolutionPackager handle `pluginpackage` entries in the solution XML, or does Flowline need to treat the package upload as an out-of-solution operation (same as it currently treats DLL upload)?
2. What is the correct attribute name on `pluginpackage` for the package content? Confirm with Dataverse SDK metadata or docs before implementation.
3. Should Flowline migrate an existing `pluginassembly` + steps to a `pluginpackage` automatically when it detects a `.nupkg`, or require a manual `--migrate` flag to avoid accidental data loss?
4. Are there environment-level permissions or settings that must be enabled for Dependent Assemblies (similar to how some features require a feature flag in Power Platform admin)?
5. Classic assemblies must be signed before registration — does Flowline's current `push` command validate this and give a clear error, or does the user get a raw Dataverse SDK exception? If the latter, adding an upfront check (detect missing `.snk` / unsigned assembly) would improve the error experience independently of this feature.
