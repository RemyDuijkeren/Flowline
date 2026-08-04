---
name: flowline-webresources
description: Authoring Dataverse web resources for Flowline — how a file under `dist/` becomes a Dataverse web resource name, the IIFE global that a form event handler is registered against, and the `// flowline:onload` / `onsave` / `onchange` / `tabstatechange` / `onreadystatecomplete` / `depends` annotations that wire forms and dependencies without a Maker Portal visit. Use when writing or reviewing a JavaScript/TypeScript form script, image, or HTML page in a Flowline project's `WebResources/` folder, when a handler is not firing on a form, or when `flowline push` reports a malformed annotation, a form that cannot be found, or a name collision.
---

# Flowline — authoring web resources

Registration intent lives in the source. There is no Maker Portal event-handler dialog and no web
resource form to click through — `flowline push` reads your built `dist/` files and makes Dataverse
match them.

## The two things you cannot guess

**1. The path under `dist/` becomes the Dataverse name.** Push syncs `dist/` and derives each web
resource name from the file's path relative to it. Two modes:

| `dist/` path            | Dataverse name                | Why                                              |
|-------------------------|-------------------------------|--------------------------------------------------|
| `dwe_salesorder.js`     | `dwe_salesorder.js`           | first segment already looks prefixed → verbatim   |
| `dwe_ext/lib.js`        | `dwe_ext/lib.js`              | same, via the folder name                         |
| `account.js`            | `dwe_DWE_Base/account.js`     | not prefixed → `{publisherPrefix}_{solutionName}/`|
| `images/logo.png`       | `dwe_DWE_Base/images/logo.png`| same                                              |

"Looks prefixed" means the first path segment matches `^[a-z][a-z0-9]*_` — lowercase publisher
prefix followed by an underscore. That rule is what lets a pre-existing flat Dataverse name
round-trip unchanged instead of getting double-prefixed. Two local files that resolve to the same
name is a hard error, not a merge.

**2. The handler you register is `{PascalCaseFilename}.{exportedFunction}`.** Dataverse loads a JS
library as a global-scope script, so Rollup bundles each entry point into an IIFE exposing a global
named after the file: `account.ts` → `Account`, `account-ribbon.ts` → `AccountRibbon`. Only
`export`ed functions are reachable. A function without `export` is invisible to Dataverse no matter
what the annotation says.

## Form event annotations

A single-line comment above the handler — or anywhere in the file — replaces the Configure Event
dialog. `push` adds, updates, and removes the Form Event Handler and Form Library entries to match.

```javascript
// flowline:onload account "Account Main"
// flowline:onsave account "Account Main"
// flowline:onchange account "Account Main" creditlimit
// flowline:tabstatechange account "Account Main" Summary
// flowline:onreadystatecomplete account "Account Main" myFrame

export function onLoad(executionContext) { ... }
```

Grammar:

```
// flowline:onload|onsave                              <entity> <form> [modifiers] [Function[(params)]]
// flowline:onchange|tabstatechange|onreadystatecomplete <entity> <form> <scope> [modifiers] [Function[(params)]]
```

- `<entity>` — table logical name, lowercase.
- `<form>` — form display name exactly as the form editor shows it. Quote it when it contains
  spaces; single or double quotes, not mixed. **Main and Quick Create forms only.**
- `<scope>` — the attribute logical name (`onchange`), the tab's control Name (`tabstatechange`), or
  the IFRAME control Name (`onreadystatecomplete`, with or without the `IFRAME_` prefix).
- `Function` — optional; omit it and the default applies.
- Modifiers, between the mandatory tokens and the function name, in any order: `[order:N]` (lower N
  runs first) and `[bulkEdit]` (`onload` only — using it elsewhere is an error).
- `//! flowline:…` and `/*! flowline:… */` are also recognized. The block form is the one that
  reliably survives minification.

Default function names when you omit one:

| Directive               | Default                                                       |
|-------------------------|---------------------------------------------------------------|
| `onload` / `onsave`     | `onLoad` / `onSave`                                           |
| `onchange`              | `on` + PascalCase attribute **with publisher prefix stripped** + `Change` — `new_credit_limit` → `onCreditLimitChange` |
| `tabstatechange`        | `on` + PascalCase tab name + `TabStateChange`                  |
| `onreadystatecomplete`  | `on` + PascalCase control id + `ReadyStateComplete`            |

The prefix strip applies to `onchange` only — tab and IFRAME names are maker-assigned form-design
names, not schema names, so nothing is stripped there.

A line that starts like an annotation but fails the grammar is **warned about and ignored**, not
fatal — check the warning output rather than assuming a silent handler registered. Renaming a form
in the Maker Portal does not update your annotation; the next push fails with
`form '<form>' not found for entity '<entity>'`.

## Dependencies

```javascript
// flowline:depends dwe_ext/shared-library.js
```

One logical name per line, anywhere in the file, deduplicated across files. Push registers the
Dataverse web resource dependency; `deploy` also treats anything named in a `depends` annotation as
protected from orphan cleanup.

RESX files sharing a base name with a script are wired up automatically —
`dist/av/AccountForm.1033.resx` attaches to `dist/av/AccountForm.js`, at any folder depth. Zero
matches or multiple matches produce a warning and no registration; name the target explicitly with
`depends` in that case. A bare `strings.resx` (no LCID) expands to every `*.NNNN.resx` variant
present locally or in Dataverse.

## Project layout

```
WebResources/
├── src/            ← every .ts/.js directly here is an entry point → one dist/ bundle each
│   └── modules/    ← shared code, imported only; not bundled standalone
├── public/         ← copied to dist/ verbatim (images, HTML, hand-written classic .js)
└── dist/           ← build output, gitignored — the only folder push reads
```

Folder and project names are yours to change; `push` resolves the WebResources project from the
solution file, not from the name. Plain classic JavaScript with no modules belongs in `public/` —
it lands in `dist/` untouched and skips Rollup entirely.

## Verifying

1. **Build.** `npm run build`, or `dotnet build` — the WebResources project runs `npm run build` as
   part of it. Push builds too unless you pass `--no-build`.
2. **`flowline push --scope webresources --dry-run`** prints the plan without writing: which
   resources are added, updated, deleted, and which form handlers and dependencies get wired.
   Read the delete list — push makes Dataverse match `dist/` exactly.
3. `--scope webresources` implies form-event registration. **`--scope formevents` alone** re-runs
   only the registration against an already-pushed `dist/` — the right scope after editing nothing
   but an annotation.
4. Push refuses to run on a missing or empty `dist/`, because that would delete every web resource
   in the solution. If you hit that error, the build didn't produce output — fix that, don't work
   around it.
5. Exit 0 means the resource and its registration landed, not that the script behaves. Hard-refresh
   the form and exercise it.

### Outside a Flowline project

`flowline push <solution> --webresources <path> --dev <url>` pushes a loose folder with no
`.flowline` and no repo. The solution name and `--dev` are required, nothing is built, and
`--scope webresources`/`--scope formevents` *require* `--webresources`. Inside a project folder the
flag is rejected (exit 15) rather than guessed at — `cd` elsewhere or use project mode.

## Full reference

Push mechanics, file-type detection for extensionless legacy files, and the full annotation
reference: **[05 — Push WebResources](https://github.com/RemyDuijkeren/Flowline/wiki/05-Push-WebResources)**.
Project scaffold, Rollup config, TypeScript and ESLint setup, namespace prefixes, and swapping the
build tool: **[08 — WebResources Project](https://github.com/RemyDuijkeren/Flowline/wiki/08-WebResources-Project)**.
