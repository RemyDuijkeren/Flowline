---
title: PAC Solution XML Diff Pattern
date: 2026-06-16
category: docs/solutions/design-patterns/
module: SolutionChangeSummary
problem_type: design_pattern
component: tooling
severity: medium
applies_when:
  - Adding a new component type to SolutionChangeSummary sub-change diffing
  - Parsing PAC-unpacked solution XML in any context
  - Implementing stable XML comparison for Dataverse tooling
tags:
  - pac-solution
  - xml-diff
  - dataverse
  - change-summary
  - git-diff
---

# PAC Solution XML Diff Pattern

## Context

`SolutionChangeSummary` needed to show *what* changed inside a Dataverse solution component (attributes added, view columns changed, options modified) rather than just flagging that a file changed. This required comparing the previous committed XML (via `git show HEAD:<path>`) against the working-tree XML after `pac solution sync`, then diffing specific XML elements by identity key.

Several non-obvious format details in PAC-exported XML caused silent failures during implementation: UTF-8 BOM on every file, lowercase element/attribute names in OptionSet XML, and structural false-positives when comparing FetchXML after column changes.

## Guidance

### Core pattern: ParseXmlElements + DiffElements

Use a shared helper that parses a component XML string into a dictionary keyed by element identity, then diff old vs new dictionaries.

```csharp
static Dictionary<string, XElement>? ParseXmlElements(
    string? xml, string elementName, Func<XElement, string?> keySelector)
{
    if (xml == null) return null;
    try {
        return XDocument.Parse(xml.TrimStart('﻿'))   // strip UTF-8 BOM
            .Descendants(elementName)
            .Select(el => (key: keySelector(el), el))
            .Where(t => !string.IsNullOrEmpty(t.key))
            .GroupBy(t => t.key!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().el, StringComparer.OrdinalIgnoreCase);
    }
    catch (XmlException) { return null; }
    catch (InvalidOperationException) { return null; }
}
```

Null input → null output. A null old dictionary means new file (skip sub-detail). A null new dictionary means deleted file (all old items become Deleted).

### Retrieving old XML

```csharp
var result = await Cli.Wrap("git")
    .WithArguments(["show", $"HEAD:{relPath.Replace('\\', '/')}"])
    .ExecuteBufferedAsync(ct);
return result.IsSuccess ? result.StandardOutput : null;
```

Backslash-to-forward-slash conversion is required — git paths always use `/` even on Windows.

### PAC XML format facts (critical)

| Component | Element name | Key field |
|---|---|---|
| Entity attribute | `attribute` (lowercase) | `<LogicalName>` child element |
| OptionSet option | `option` (lowercase) | `value` attribute (lowercase) |
| View column | `cell` | `name` attribute |
| Form field | `cell` | `datafieldname` attribute |

**All PAC-exported XML files include a UTF-8 BOM** (`﻿`). `XDocument.Parse` throws `XmlException` on it. Always strip with `.TrimStart('﻿')` before parsing.

**OptionSet names and attributes are all lowercase**: `<option value="100000000">` — not `<Option Value="...">`. Using PascalCase here produces zero matches with no error, not an exception.

### Avoiding false filter-change detection in views

When a column is added to a view, `<attribute>` elements inside `<fetchxml>` change, which naively triggers a "filter changed" signal. Strip both `<attribute>` and `<order>` elements before comparing filter content:

```csharp
static string? StripColumnsAndOrders(string? xml)
{
    if (xml == null) return null;
    var doc = XDocument.Parse(xml.TrimStart('﻿'));
    doc.Descendants("attribute").Remove();
    doc.Descendants("order").Remove();
    return doc.ToString(SaveOptions.DisableFormatting);
}
```

Use `SaveOptions.DisableFormatting` everywhere you serialize for comparison — whitespace differences in PAC output produce false positives otherwise.

### Markdown symbols in generated files

When writing `+`, `-`, `~` as status prefixes in a markdown file, wrap them in backticks. A bare `- +` on a bullet line is parsed by most renderers as a nested list:

```csharp
// Wrong — renders as nested list:
sb.AppendLine($"- + {description}");

// Correct — renders as inline code:
sb.AppendLine($"- `+` {description}");
```

### Exception handling in file read helpers

When wrapping file reads in `catch {}` to handle missing files, always rethrow `OperationCanceledException` first. Bare catches absorb cancellation silently:

```csharp
// Wrong:
try { return await File.ReadAllTextAsync(path, ct); }
catch { return null; }

// Correct:
try { return await File.ReadAllTextAsync(path, ct); }
catch (OperationCanceledException) { throw; }
catch { return null; }
```

## Why This Matters

Silent failures are the main risk in this area. `XmlException` on a BOM produces null from `ParseXmlElements`, which cascades to an empty sub-change list — the summary shows no detail, no error. Lowercase element name mismatches do the same. Without knowing these PAC format specifics, every new component type added to the diff will silently produce empty output until the exact element name is verified against actual PAC output.

## When to Apply

- Adding sub-change detail for a new Dataverse component type (e.g., Workflows, Roles, AppModules)
- Debugging why sub-changes show empty for a component that should have changes
- Comparing any PAC-unpacked XML across git commits

## Examples

### Entity attribute diff

```csharp
static IReadOnlyList<SubChange> DiffEntityAttributes(string? oldXml, string? newXml)
{
    var old = ParseXmlElements(oldXml, "attribute", e => (string?)e.Element("LogicalName")) ?? [];
    var @new = ParseXmlElements(newXml, "attribute", e => (string?)e.Element("LogicalName")) ?? [];
    return DiffElements(old, @new,
        el => $"{el.Element("LogicalName")?.Value} ({el.Element("Type")?.Value ?? "?"})");
}
```

### OptionSet option diff (note lowercase names)

```csharp
static IReadOnlyList<SubChange> DiffOptionSet(string? oldXml, string? newXml)
{
    var old = ParseXmlElements(oldXml, "option", e => (string?)e.Attribute("value")) ?? [];
    var @new = ParseXmlElements(newXml, "option", e => (string?)e.Attribute("value")) ?? [];
    return DiffElements(old, @new, el => {
        var label = el.Descendants("label")
            .FirstOrDefault(l => (string?)l.Attribute("languagecode") == "1033")
            ?.Attribute("description")?.Value ?? el.Attribute("value")?.Value ?? "?";
        return $"{label} ({el.Attribute("value")?.Value})";
    });
}
```

### Deleted file — all items as Deleted

When `git show HEAD:<path>` returns content but the working-tree file is gone, pass `(oldXml, null)`:

```csharp
// newXml = null → newDict = [] → all old keys become Deleted
var result = DiffEntityAttributes(oldXml, null);
```

`ParseXmlElements(null, ...)` returns null, which `?? []` coerces to an empty dictionary. `DiffElements` then marks every old key as `ChangeStatus.Deleted`.

## Related

- `docs/solutions/logic-errors/sync-overwrites-uncommitted-src-without-warning-2026-05-15.md` — SyncCommand dirty-tree guard, also operates on `Package/src/`
- `docs/solutions/architecture-patterns/sync-first-remove-mapping-replace-dotnet-build-2026-05-17.md` — why `Package/src/` is canonical truth after pac sync
