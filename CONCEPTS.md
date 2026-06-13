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
