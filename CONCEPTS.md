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
*managed solution (distinct — a locked distributable solution package. See [[Solution import action]] for how Update vs Upgrade determines whether components get removed)*

### Solution import action
Whether a solution import is treated as an **Update** (adds and modifies components, never removes any) or an **Upgrade** (also removes components no longer present in the imported version). Upgrade only applies to managed solutions, and only when a prior version of the same solution already exists in the target — a first-time install has nothing to upgrade from and is always a plain Update. [[Unmanaged solution]] imports are always additive regardless of which action is chosen.

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

## CLI Force Flag

### Force specifier
The required value passed to `--force`/`-f` (e.g. `--force recreate-assembly`) naming exactly which destructive or overwrite hazard to approve on the invoked command. Each command exposes only the specifier values for the hazards it can hit; `all` approves every hazard the invoked command has, including the shared `config` value where present. Bare `--force` with no value is always a parse error — there is no blanket approval by omission.

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

### Form Library
A form-level reference to a JavaScript web resource, registered so Dataverse loads the file when the
form opens. Matches Dataverse's own Maker Portal terminology (the "Form libraries" panel, and the
"Library" field in the Configure Event dialog) and the `<Library name=... libraryUniqueId=.../>`
element inside a form's `<formLibraries>` section. Flowline derives a Form Library's
`libraryUniqueId` deterministically from its name rather than storing it, so it can recognize its own
prior writes without a separate tracking file.

### Form Event Handler
A function bound to one of a form's `onLoad`/`onSave`/`onChange` events, registered as a
`<Handler functionName=... libraryName=... handlerUniqueId=.../>` element inside the form's
`<events>` section. Named "Form Event Handler" rather than the Maker Portal's own shorter "Event
Handler" label (the "Handlers" list and "+ Event Handler" button under each event in the Configure
Event dialog) because outside that dialog's own form-scoped context, "Event Handler" alone doesn't
convey it's a form-level concept. `onChange` handlers additionally scope to one table attribute (the
`<event name="onchange" attribute="...">` element's `attribute` value) — a single entry covers every
control on the form bound to that attribute, regardless of layout. Every Form Event Handler
references exactly one [[Form Library]] by name — Flowline only ever manages a Form Event Handler
pointing at its own tracked Form Library; handlers pointing elsewhere (added by other solutions or
ISVs) are left untouched. See [[Event annotation]] for how a developer declares one from source.

### Event annotation
A developer-authored declaration in a web resource's source binding one of its functions to a form's
`onLoad`/`onSave`/`onChange` event, read by `flowline push` to register the [[Form Event Handler]] in
the form's `formxml`. Shape: `// flowline:onload <entity> <form> [Function[(params,...)]]` (and
`flowline:onsave`), or, for attribute-level binding, `// flowline:onchange <entity> <form> <attribute>
[Function[(params,...)]]` — same comment styles and whole-file scanning as the dependency annotation.
Function name is optional (defaults to `onLoad`/`onSave`, or `on<Attribute>Change` for `onchange`) and
matched case-insensitively against the file's real exports. Flowline only ever manages a [[Form Event
Handler]] pointing at its own tracked [[Form Library]] — handlers added by other solutions or ISVs are
left untouched. The form name is matched literally; if the form is later renamed, see [[Rename-resilience
signal]] for what happens to the annotation's binding.

### Rename-resilience signal
A confidence-tiered advisory clue offered when an [[Event annotation]]'s form name no longer resolves
to any live form, appended to the push failure message. None of these signals let the push succeed —
a name-lookup miss always fails, so the annotation still must be corrected by hand; the signal only
makes the failure actionable instead of a bare "not found." Three tiers are tried in order of
confidence, strongest first: a **self-tag** match (the annotation's own handler fingerprint, computed
the same way it originally was, is still present on some other live form — proof that form used to be
this one, regardless of its current name), a **rename cache** hit (a prior push's remembered identity
for this exact entity-and-name pair still exists live, just possibly renamed since), and a **sole
survivor** hedge (exactly one form remains on the entity, offered only as a weak "is this what you
meant?" guess). The first tier to produce a candidate wins; lower tiers are never consulted once a
higher one fires.

### Bulk edit form
A Dataverse feature where a user selects multiple grid rows and edits them in one scaled-down "Edit
(N) records" form; a saved change applies to every selected record at once. Form event handlers
(onload, onsave, onchange, business rules) don't fire by default in this mode, since a script written
for one record's context could misbehave applied simultaneously to N records —
`BehaviorInBulkEditForm="Enabled"` is the per-event opt-in for a handler safe to run there anyway.
Dataverse only honors this attribute on a form's `onload` event.

### Form Event Pipeline
Microsoft's term for how Dataverse executes a form event's registered [[Form Event Handler]]s:
sequentially, in the FormXml list order, with a hard cap of 50 handlers per event. Flowline enforces
both: handler write order follows annotation-encounter order by default, with an optional `[order:N]`
annotation modifier for explicit cross-file sequencing; exceeding the 50-handler cap for one event
fails the push before any Dataverse write.
