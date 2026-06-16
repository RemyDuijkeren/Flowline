# Concepts

Shared domain vocabulary for this project — entities, named processes, and status concepts with project-specific meaning. Seeded with core domain vocabulary, then accretes as ce-compound and ce-compound-refresh process learnings; direct edits are fine. Glossary only, not a spec or catch-all.

## Dataverse Solutions

### Solution component
A membership record that tracks which objects (plugin assemblies, web resources, workflows, custom APIs, and similar) belong to a solution. Each component has an object identifier and a component type that classify what kind of object it references.

### Orphan component
A solution component present in the Dataverse environment that is absent from the local solution source. Orphans accumulate because unmanaged solution imports are additive — Dataverse never removes components during import.

### Unmanaged solution
A Dataverse solution whose components can be created, modified, and deleted individually in the target environment. Flowline prefers to targets unmanaged solutions, but can also work with managed solutions. Because unmanaged imports are additive, deployed components are never automatically removed — orphan cleanup must run explicitly to remove components deleted from source.
*managed solution (distinct — a locked distributable solution package whose imports overwrite existing layers and do remove components)*

## Sync

### Sync change summary
The structured record of all Dataverse solution component changes detected between the working tree and the last git commit after a `flowline sync`. Surfaced in the terminal as a labelled tree and written in full to `CHANGES.md` in the solution folder. Groups components by type (Entities, OptionSets, etc.) and annotates each with an added/removed/modified status.

### Sub-change
A change *within* a component — for example, an attribute added to an entity, a column removed from a view, or an option label changed on an option set. Sub-changes are the child entries under their parent component in both the terminal tree and `CHANGES.md`. The terminal caps the number of named sub-changes shown per component; `CHANGES.md` always contains the full list.

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
