---
title: "Web resource dependency registration — identity, GUID reuse, and matching semantics"
date: 2026-06-14
last_updated: 2026-07-10
category: docs/solutions/design-patterns
module: web-resources
problem_type: design_pattern
component: dependency-registration
severity: high
applies_when:
  - Implementing read-modify-write for the dependencyxml field with GUID stability
  - Registering web resource dependencies from annotation comments in source files
  - Matching RESX files to their corresponding JS handler files across multi-solution repos
  - Expanding bare RESX references (no LCID suffix) to all matching LCID variants
  - Deduplicating dependency references within a single source file
  - Parsing annotations out of a file that passes through a bundler, transpiler, or minifier before Flowline reads it
symptoms:
  - GUID reuse failing silently when DependencyLibrary equality matches on all three fields
  - Cross-folder RESX matching causing false positives (av_ns1/MyForm.resx linked to av_ns2/MyForm.js)
  - Global orphan dependencyxml not preserved because ColumnSet omitted the field
  - Bare RESX references silently dropped when no LCID variants are found
  - Duplicate annotations in a single file processed multiple times downstream
  - flowline:depends annotations silently dropped for every project built from the default WebResources scaffold, with no error or warning, because a bundler-injected banner comment precedes them
tags:
  - dataverse
  - webresource
  - dependency-registration
  - guid-semantics
  - resx-matching
  - annotation-parsing
  - read-modify-write
  - deduplication
  - bundler
  - minification
  - rollup
---

# Web resource dependency registration — identity, GUID reuse, and matching semantics

## Context

Flowline CLI syncs web resources to Dataverse. Dataverse supports load-order dependency declarations
via the `dependencyxml` field on the `webresource` entity. Before the `feat/webresource-dependency-registration`
feature, Flowline ignored this field — developers had to set it manually in the Maker Portal, which
drifted from source control.

The feature adds annotation-driven management: parse `// flowline:depends` comments from JS files,
enrich with LCID expansion and RESX auto-matching, then synchronize `dependencyxml` to Dataverse on
every push. Several non-obvious semantics caused subtle bugs during implementation that are worth
documenting for future features touching Dataverse GUID-keyed fields or file-based annotation
pipelines.

See also: [Dataverse dependencyxml field format — confirmed empirically](../documentation-gaps/webresource-dependencyxml-field-format-2026-06-14.md) for the raw field format, write behavior, and confirmation that `libraryUniqueId` is not the `webresourceid`.

## Guidance

### 1. DependencyLibrary equality must be Name-only

`DependencyLibrary` is a C# `record` with three fields: `Name`, `DisplayName`, `LibraryUniqueId`. C#
`record` auto-generates equality over all fields. This is wrong for domain equality because Dataverse
may echo back a different GUID or DisplayName than what was written:

```csharp
// WRONG: HashSet lookup fails if Dataverse returns a different GUID or DisplayName
public record DependencyLibrary(string Name, string DisplayName, Guid LibraryUniqueId);
```

Override equality to use Name only:

```csharp
public record DependencyLibrary(string Name, string DisplayName, Guid LibraryUniqueId)
{
    public virtual bool Equals(DependencyLibrary? other) =>
        other is not null && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
    public override int GetHashCode() =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(Name);
}
```

This ensures `HashSet<DependencyLibrary>` membership, GUID lookup, and diff computation are all
keyed on the dependency's logical name — the only stable identity across Dataverse reads.

### 2. Reuse GUIDs by name in read-modify-write

Every `dependencyxml` update must preserve existing `libraryUniqueId` values for unchanged
dependencies. Dataverse uses this GUID for incremental diff tracking — regenerating GUIDs on every
push treats each deploy as a full replacement:

```csharp
var existingByName = existing.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

var merged = desired.Select(dep =>
    existingByName.TryGetValue(dep.Name, out var prior)
        ? dep with { LibraryUniqueId = prior.LibraryUniqueId }
        : dep with { LibraryUniqueId = Guid.NewGuid() });
```

Generate a fresh `Guid.NewGuid()` only for newly added dependencies.

### 3. Include `dependencyxml` in the ColumnSet for global orphan queries

When a web resource exists in Dataverse globally but not in the current solution, Flowline does an
`AddSolutionComponent` + optional update. The GUID reuse pattern above requires reading existing
`dependencyxml` before writing. If the ColumnSet omits the field, it comes back null — GUID reuse
silently breaks and every push assigns new GUIDs:

```csharp
// WRONG — dependencyxml comes back null, breaks GUID reuse
new ColumnSet("name", "content", "webresourcetype")

// CORRECT
new ColumnSet("name", "content", "webresourcetype", "dependencyxml")
```

This applies to every Dataverse query path that feeds into a write: the solution query, the global
orphan query, and any future per-resource refresh.

### 4. Deduplicate annotations before enrichment

`WebResourceAnnotationParser.ParseAnnotations` reads `// flowline:depends` lines (see lesson 8 for
the full set of recognized forms). Files may contain duplicate annotations. Without deduplication,
the same dependency name enters enrichment and ultimately produces duplicate XML entries or
`ArgumentException` in downstream `Dictionary` construction:

```csharp
// Deduplicate while preserving order (first occurrence wins)
List<string>? result = null;
HashSet<string>? seen = null;
foreach (var line in File.ReadLines(filePath))
{
    var match = AnnotationRegex.Match(line.Trim());
    if (!match.Success) continue;

    var name = match.Groups["name"].Value;
    if (!string.IsNullOrEmpty(name) &&
        (seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(name))
        (result ??= []).Add(name);
}
```

A defensive `Distinct(StringComparer.OrdinalIgnoreCase)` in the downstream consumer is also
reasonable as a second guard.

### 5. Use folder-qualified base names for RESX auto-matching

Two-phase enrichment auto-links RESX files to their matching JS handler. A multi-solution repo may
have `av_ns1/MyForm.js` and `av_ns2/MyForm.js`. Using the bare filename (without folder) as the
match key causes cross-folder false positives:

```csharp
// WRONG — strips folder, causes cross-folder false match
static string GetResxBaseName(string name) => Path.GetFileNameWithoutExtension(name);

// CORRECT — folder-qualified base name
static string GetResxBaseName(string logicalName)
{
    var stem = logicalName[..^5]; // strip ".resx"
    var lastSlash = stem.LastIndexOf('/');
    var dotIdx = stem.LastIndexOf('.');
    if (dotIdx >= 0 && dotIdx > lastSlash && stem[(dotIdx + 1)..].All(char.IsDigit))
        stem = stem[..dotIdx]; // strip LCID suffix
    return stem;
}

static string GetJsBaseName(string logicalName) =>
    logicalName.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
        ? logicalName[..^3]
        : logicalName;
```

`av_ns1/MyForm.1033.resx` → base `av_ns1/MyForm` → matches only `av_ns1/MyForm.js`.

### 6. Warn and preserve bare RESX references with no LCID variants

Phase 2a expands bare `.resx` references (no LCID digit suffix) to all matching LCID variants. If no
variants exist yet (e.g., the RESX file hasn't been deployed), silently dropping the reference hides
a misconfiguration. Warn and keep the bare name instead:

```csharp
var expanded = ExpandLcidVariants(dep, allNames).ToList();
if (expanded.Count > 0)
    resolved.AddRange(expanded);
else
{
    output.Warning($"'{name}': bare RESX reference '{dep}' has no LCID variants — kept as-is.");
    resolved.Add(dep);
}
```

### 7. Annotation-referenced resources must be exempted from orphan cleanup

`flowline deploy` runs orphan cleanup — it deletes Dataverse web resources absent from the solution's
`Solution.xml`. Shared libraries referenced via `// flowline:depends` are often owned by another
solution; they appear in Dataverse but not in the current solution's XML. Without an exemption they
would be deleted:

```csharp
// Collect all annotation refs across the project before running cleanup
var annotationRefs = WebResourceAnnotationParser.CollectAllReferences(webresourceRoot);
// Exempt them from deletion even if absent from Solution.xml
```

`CollectAllReferences` scans all `*.js` files under the project root and returns the union set of
all `// flowline:depends` names. Any name in this set is protected from orphan deletion.

### 8. Scan the whole file for annotations — a bundler banner is not a `//` comment

The original parser stopped at the first line that wasn't blank or a `//` line comment, on the
assumption that annotations always sit at the very top of the file. This breaks for any project
built with a bundler that injects a non-`//` banner ahead of user code — confirmed against the
WebResources scaffold's own `rollup.config.mjs`, which prepends a `/** ... */` block-comment file
header to every build:

```javascript
/**
  * This code is generated using Rollup...
  */
(function () {
// flowline:depends av_ext/lib.js   ← never reached: parser already broke on the "/**" line above
...
})();
```

`/**` matches neither the depends prefix nor `"//"`, so the loop's `else break;` fires on line 1 of
every file the default template produces — the annotation is silently ignored regardless of where
the developer puts it. The fix drops the "stop at first non-comment line" restriction entirely and
matches a regex against every line in the file:

```csharp
static readonly Regex AnnotationRegex = new(
    @"^(?://!?|/\*!)\s*flowline:depends\s+(?<name>.+?)\s*(?:\*/)?$",
    RegexOptions.Compiled);
```

This also recognizes `//!` and `/*! ... */` as equivalent prefixes — the same "legal comment"
marker Terser, esbuild, and SWC all preserve by default when minifying (anything starting with
`//!`/`/*!`, or containing `@license`/`@preserve`/`@cc_on`, survives default minify settings; a
bare `//` or `/**` comment does not). The scaffold ships with no minifier today, so this is
forward-hardening rather than a fix for a second observed bug — but it means a project that later
adds one doesn't silently lose dependency registration a second time.

`/*! flowline:depends ... */` (block form) is the recommended form for minified builds — some
minifier configurations only apply the "preserve" heuristic to block comments, not line comments,
so `//! flowline:depends ...` can still be stripped in a few setups where `/*! ... */` survives.
Plain `// flowline:depends ...` keeps working unchanged for builds without a minifier.

**How this surfaced — and a dead end worth recording.** This bug was found while investigating an
unrelated live report: a dependency added manually via the Maker Portal UI in one Dataverse
environment wasn't visible when queried in another. That turned out to be a plain Dev/Prod
environment mixup on the reporter's end, not a Flowline bug — the Maker Portal renders whatever raw
`name`/`displayName` is stored in `dependencyxml` verbatim, with no resolution against real logical
names, so a bare unqualified `name` (e.g. `"example1.js"` instead of `"av_ns/example1.js"`) displays
just fine. An initial hypothesis chased exactly that "unqualified name" theory and produced a working
but ultimately unnecessary fix before the environment mixup was confirmed via direct API queries
(`pac env fetch --xml`) and a same-environment Maker Portal screenshot. Reading
`WebResourceAnnotationParser`'s real source to design that fix — not the mixup itself — is what
surfaced this separate, much higher-impact bug. Lesson: an "I don't see X in the Maker Portal" report
can be a real sync bug or a wrong-environment report with an identical symptom; confirm which
environment is actually being viewed before diagnosing a code-level root cause.

## Why This Matters

**GUID stability prevents silent deploy regressions.** Regenerating GUIDs on every push causes
Dataverse to treat each deploy as a full replacement rather than an incremental update. The impact
is invisible in the Maker Portal but manifests in PAC-managed `.data.xml` diffs that change on
every push with no content change.

**Name-only equality is mandatory for any GUID-keyed Dataverse field.** Dataverse is the authoritative
store for internal GUIDs like `libraryUniqueId`. What Dataverse echoes back may differ from what was
written (field normalization, caching). Always key identity on the stable domain name, never on the
internal GUID.

**Folder-qualified matching is mandatory in multi-solution repos.** Same-named files across publisher
folders (`av_ns1/MyForm.js`, `av_ns2/MyForm.js`) are common. A file-name-only match silently links
the wrong RESX to the wrong JS — no error is raised at runtime.

**Missing `dependencyxml` from ColumnSet is a silent bug.** No exception is thrown; the field just
reads as null. Every call site that reads a resource intending to update its deps must include the
field in its ColumnSet.

## When to Apply

- Implementing read-modify-write for any Dataverse memo field containing GUID-keyed XML (not just `dependencyxml`).
- Adding annotation parsing to any source-file pipeline where duplicates are possible.
- Building file-matching logic across a project with multiple publisher-prefixed folders.
- Implementing orphan cleanup for any resource that may be shared across solutions.
- Using C# `record` types for domain entities where auto-generated equality doesn't match domain identity rules.
- Parsing annotations out of any file that may pass through a bundler, transpiler, or minifier before Flowline reads it — check whether the tool can inject content ahead of the annotation, and whether the annotation's comment style survives that tool's default settings.

## Examples

**Before/after: cross-folder RESX test**

```csharp
// Before — filename-only base name, cross-folder match fires incorrectly
var files = new[] {
    "av_ns1/MyForm.1033.resx",  // different folder
    "av_ns2/MyForm.js"
};
// av_ns1/MyForm matched to av_ns2/MyForm.js — WRONG

// After — folder-qualified base name, cross-folder match correctly blocked
// av_ns1/MyForm != av_ns2/MyForm — no match, warning emitted
```

**GUID reuse on update vs. new dependency**

```
Push 1: av_ns/MyForm.js depends on av_ns/lib.js → assigns libraryUniqueId = {abc-123}
Push 2: no change to annotation → reads {abc-123} from dependencyxml → reuses it → no diff in PAC XML
Push 3: adds av_ns/util.js dependency → {abc-123} reused for lib.js, {def-456} = Guid.NewGuid() for util.js
```

**`// flowline:depends` annotation syntax**

```javascript
// flowline:depends av_ext/shared-library.js
/*! flowline:depends av_ext/strings.resx */   // block bang form — most reliable across minifiers
//! flowline:depends av_ext/another-lib.js    // line bang form — works with most, not all, minifiers

export function onLoad(executionContext) { ... }
```

One dependency per line, anywhere in the file — not just the top. `//`, `//!`, and single-line
`/*! ... */` are all recognized equivalently. For projects with a minification step, prefer
`/*! ... */` — some minifier configs only preserve block comments matching the bang/`@license`
convention, not line comments, so `//!` can still get stripped where `/*! ... */` survives.

## Related

- [Dataverse dependencyxml field format — confirmed empirically](../documentation-gaps/webresource-dependencyxml-field-format-2026-06-14.md) — raw field format, write behavior, libraryUniqueId semantics
- `src/Flowline.Core/Services/WebResourceAnnotationParser.cs` — annotation parsing with deduplication
- `src/Flowline.Core/Services/WebResourceReader.cs` — two-phase enrichment (LCID expansion, RESX auto-match)
- `src/Flowline.Core/Services/WebResourcePlanner.cs` — read-modify-write with GUID reuse
- `src/Flowline.Core/Models/WebResourceModels.cs` — DependencyLibrary with Name-only equality
