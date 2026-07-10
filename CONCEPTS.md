# Concepts

Shared domain vocabulary for this project — entities, named processes, and status concepts with project-specific meaning. Seeded with core domain vocabulary, then accretes as ce-compound and ce-compound-refresh process learnings; direct edits are fine. Glossary only, not a spec or catch-all.

## Dataverse Solutions

### Solution component
A membership record that tracks which objects (plugin assemblies, web resources, workflows, custom APIs, and similar) belong to a solution. Each component has an object identifier and a component type that classify what kind of object it references.

### Component dependency
A platform record that tracks when one solution component references another — for example, when a plugin step references a plugin type. Distinct from a [[Solution component]] (which records membership in a solution): removing a component from a solution does not clear its dependency records. Dataverse enforces these records during deletion — the dependency check fires before any cascade runs — so the required component cannot be deleted while any dependent component still holds a reference to it.

### Orphan component
A solution component present in the Dataverse environment that is absent from the local solution source. Orphans accumulate because unmanaged solution imports are additive — Dataverse never removes components during import.

### Unmanaged solution
A Dataverse solution whose components can be created, modified, and deleted individually in the target environment. Flowline prefers to targets unmanaged solutions, but can also work with managed solutions. Because unmanaged imports are additive, deployed components are never automatically removed — orphan cleanup must run explicitly to remove components deleted from source.
*managed solution (distinct — a locked distributable solution package whose imports overwrite existing layers and do remove components)*

### Local-source identity shape
The pattern by which a solution component's identity is recorded in unpacked solution source, used by [[Orphan component]] detection to check whether a live component is still declared locally. Three shapes recur across component types: an id embedded directly in the component's own file and mirrored by `id` in Solution.xml (e.g. Role); a schemaname/uniquename-keyed folder with no GUID anywhere locally (e.g. CustomApi, Bot); and a declaration inline within Customizations.xml's own named section, also with no GUID (e.g. ConnectionReference). A component type is only trusted for removal recommendations once its shape has a verified local-source check with test coverage for both directions — still-declared components suppressed, genuinely-removed components reported.

## Orphan Cleanup

### Orphan handler
A self-contained unit that owns matching, live-querying, and classifying [[Orphan component]] candidates for one family of related component types (e.g. the CustomApi family, or the PluginAssembly/PluginType/Step/StepImage family), grouped where detection logic already overlaps rather than one handler per componenttype. Replaces a single monolithic per-type lookup table approach with independently-addable units.

### Handler status
A maturity level a handler declares about itself, hardcoded in its own code rather than external config. **Active** handlers produce real findings included in the actionable report, with a real [[Orphan priority]] verdict, eligible for auto-delete when the type is Auto. **Preview** handlers run full detection and print verbose findings, but are excluded from the actionable report and never act — this lets a handler ship and be field-tested with no action risk before promotion to Active. A component type with no handler at all falls back to the generic search-all identifier-harvest signal (verbose "possible match" only, never prioritized, never acted on).

### Orphan priority
A per-instance risk classification for an [[Orphan component]], independent of the existing Auto/Manual axis. **Prio1** blocks deployment outright (e.g. a plugin assembly update failing because orphaned plugin types still reference it). **Prio2** doesn't block anything but keeps silently executing logic source no longer has (e.g. an Activated workflow or Enabled plugin step still triggering on deleted business logic). **Prio3** is harmless but should eventually be cleaned up. Unlike Auto/Manual (a static property of the component type), priority is decided per instance inside the [[Orphan handler]] that owns the type — a type is only ever capable of a given priority, not fixed to it.

## Sync

### Sync change summary
The structured record of all Dataverse solution component changes detected between the working tree and the last git commit after a `flowline sync`. Surfaced in the terminal as a labelled tree and written in full to `CHANGES.md` in the solution folder. Groups components by type (Entities, OptionSets, etc.) and annotates each with an added/removed/modified status.

### Sub-change
A change *within* a component — for example, an attribute added to an entity, a column removed from a view, or an option label changed on an option set. Sub-changes are the child entries under their parent component in both the terminal tree and `CHANGES.md`. The terminal caps the number of named sub-changes shown per component; `CHANGES.md` always contains the full list.

## Auth

### UNIVERSAL profile
A PAC auth profile created via browser/user login (`pac auth create` with no `--applicationId`). Stores the user's identity and an MSAL token cache entry. Can connect to any Dataverse environment the user has access to in their tenant. Multiple UNIVERSAL profiles can coexist — typically one per named environment (`Dev`, `Prod`, etc.) — each pointing to a different URL but using the same underlying user account. Contrast with a ServicePrincipal profile, which stores an application ID and tenant for headless/CI use.

## Code Generation

### Generator
The code generation backend used by `flowline generate` to produce C# model files from Dataverse entity metadata. Three backends are supported: **pac** (Microsoft's modelbuilder via PAC CLI, default), **xrmcontext** (XrmContext v4 dotnet tool, `DefaultAzureCredential` auth, cross-platform), and **xrmcontext3** (Delegate.XrmContext v3 F# exe, connection-string auth, Windows only, bridge). The generator determines the auth flow, output format, and toolchain invoked. Stored in `.flowline` config so subsequent runs don't require re-specifying it.

### Generator-owned file
A file in the generate output folder that carries the standard C# `<auto-generated>` comment block emitted by every generator (PAC and XrmContext). Flowline treats generator-owned files as safe to delete and replace on each generate run; files without the marker are user-owned and are preserved across runs.

## Plugin Registration

### Step identity
The tuple of Dataverse field values that is the *sole* lookup key for an existing plugin step, independent of its generated display name: `plugintypeid`, `sdkmessageid`, `sdkmessagefilterid`, `stage`, and `mode`. Stage alone is insufficient because `PostOperation` (value 40) is shared by both synchronous (mode=0) and asynchronous (mode=1) steps — all five fields participate to avoid ambiguous matches. A step whose display name changes (e.g. after a multi-`[Handles]` migration) is still found by this tuple and updated in place rather than deleted and recreated; the name is written on match, never read for identity. Because these same fields disambiguate multiple `[Handles]` registrations on one class, changing the message, table filter, stage, or mode of an already-registered step is treated as a different registration — it recreates the step rather than updating it in place. If the tuple matches more than one existing row (only possible for pre-Flowline history), Flowline checks whether either colliding row has a linked [[Secure Configuration]] — if so it fails immediately, since deleting the wrong one is irrecoverable; otherwise it falls back to a name-based tiebreak, and fails if that is still ambiguous.

### Secure Configuration
A Dataverse record linked to a plugin step that stores secret values passed to the plugin at runtime, never returned on read and excluded from solution export — once deleted, its content cannot be recovered. Flowline never authors or edits Secure Configuration content; it only checks whether a step has one linked as a signal to gate destructive step operations (obsolete-step deletion, ambiguous [[Step identity]] collisions) that would otherwise silently and irreversibly destroy it.

## Deploy Pipeline

### Post-deploy service
A pluggable behavior that runs during a deploy's pre-import and post-import phases — for example, orphan-component cleanup. Every registered post-deploy service executes on every deploy (fan-out), unlike a [[Generator]], where exactly one is selected for a run. A post-deploy service may hold its own state between its pre-import and post-import calls; state does not cross the shared interface between services.

### Package folder
The packed, committed mirror of a solution's Dataverse content that `deploy` always packs and imports from — the single artifact of record for what a given deploy actually ships, at every pipeline stage from a first TEST promotion through PROD. Distinct from a project's own local build output (e.g. a web-resources build's output folder): the two are expected to hold equivalent content after a sync, but only the Package folder is guaranteed to exist and be current on a build agent that never runs the local build step. A [[Post-deploy service]] that needs to inspect "what's actually being deployed" must read from the Package folder, never from a separate build artifact.

## Web Resources

### Logical name
The Dataverse-assigned global identifier for a web resource, composed of a publisher prefix and a
forward-slash-separated path (e.g., `av_ext/forms/account.js`). Logical names are globally unique
within the environment. Flowline auto-prefixes local filenames with the solution's publisher prefix
unless the file is inside a folder whose name already starts with a publisher prefix (verbatim mode).

### LCID suffix
The four-digit locale code embedded in RESX filenames (e.g., `.1033.resx` for English, `.1041.resx`
for Japanese). Dataverse loads the correct RESX variant at runtime based on the user's language
setting. A bare RESX reference without an LCID suffix (e.g., `av_ext/strings.resx`) is a shorthand
that Flowline expands to all matching LCID variants during dependency enrichment.

### Dependency annotation
A developer-authored declaration in a web resource's source that it depends on another web
resource, read by `flowline push` to register the load-order dependency in Dataverse. Recognized
anywhere in the file — not only before other code — because a build tool may inject unrelated
content ahead of it. A RESX file sharing a base name with a script is linked automatically and
needs no annotation; annotations are for everything else (shared libraries, cross-solution
references, explicit overrides when auto-linking is ambiguous).
