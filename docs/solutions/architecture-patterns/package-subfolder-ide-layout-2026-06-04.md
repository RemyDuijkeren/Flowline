---
title: "Package/ Subfolder: IDE-Conventional Solution Layout for Dataverse Projects"
date: 2026-06-04
category: docs/solutions/architecture-patterns/
module: flowline-cli
problem_type: architecture_pattern
component: development_workflow
severity: medium
applies_when:
  - clone scaffolds a new Dataverse solution project
  - PAC CLI creates output inside a named subfolder
  - .NET developers open the project in Rider or Visual Studio
  - tooling must split PAC-managed paths from Plugins/WebResources paths
tags:
  - pac-solution
  - folder-structure
  - ide-layout
  - clone
  - package-folder
  - artifacts
---

# Package/ Subfolder: IDE-Conventional Solution Layout for Dataverse Projects

## Context

`pac solution clone` creates a named subfolder in its `--outputDirectory`. When Flowline
pointed `--outputDirectory` at `solutions/`, PAC produced:

```
solutions/
└── ContosoSales/
    ├── ContosoSales.cdsproj   ← peer to the .sln, confusing in IDE
    ├── src/                   ← root-level folder, not a project
    ├── Plugins/
    └── WebResources/
```

In Rider and Visual Studio Solution Explorer this looks non-standard: `src/` is not a
project, and `ContosoSales.cdsproj` is a peer to the `.sln` file rather than nested like
the other projects. .NET developers expect to see peer project nodes under the solution.

## Guidance

Pre-create `solutions/<SolutionName>/` before calling PAC, then set `--outputDirectory`
to that folder. PAC creates `solutions/<SolutionName>/<SolutionName>/`. Rename it to
`Package/` and rename `<SolutionName>.cdsproj` to `Package.cdsproj`.

```csharp
// Pre-create the solution folder before PAC
Directory.CreateDirectory(slnFolder);

// PAC creates slnFolder/<SolutionName>/ — rename it
Directory.Move(Path.Combine(slnFolder, projectSln.Name), PackageFolder(slnFolder));
File.Move(
    Path.Combine(PackageFolder(slnFolder), $"{projectSln.Name}.cdsproj"),
    cdsprojPath); // cdsprojPath = Package/Package.cdsproj
```

All commands that reference PAC output paths use a `PackageFolder()` helper:

```csharp
protected const string PackageName = "Package";
public static string PackageFolder(string slnFolder) => Path.Combine(slnFolder, PackageName);
```

Artifact zips (from `pac solution pack`) go to `artifacts/`, not `bin/`, to avoid
confusion with .NET build output.

## Why This Matters

The resulting layout matches the mental model .NET developers bring from day one:
three peer projects under one solution, each with a clear role:

```
solutions/ContosoSales/
├── ContosoSales.sln
├── Package/               ← PAC-managed (cdsproj + unpacked XML in src/)
│   ├── Package.cdsproj
│   └── src/
├── Plugins/               ← server-side logic
│   └── Plugins.csproj
├── WebResources/          ← web assets
│   └── WebResources.csproj
└── artifacts/             ← deployment zips
```

PAC scans directories for `*.cdsproj` — it does not require the filename to match the
solution name — so renaming `ContosoSales.cdsproj` → `Package.cdsproj` is safe.

The `Directory.Move` + `File.Move` rename is atomic on the same Windows drive, so a
crash between the two leaves a recoverable state: the `Package/` folder exists but
`Package.cdsproj` is missing, which Flowline detects and reports as a clear error.

## When to Apply

- Any command that creates the solution structure (`clone`)
- Any command that reads PAC output paths (`sync`, `deploy`, `push`)
- Any utility that builds or validates the solution (`DriftChecker`, `PacUtils`)

Keep `slnFolder` (solution root) and `PackageFolder(slnFolder)` (PAC working dir)
as separate variables. Artifact paths (`Plugins/bin/Release`, `WebResources/dist`)
remain rooted at `slnFolder`, not `packageFolder`.

## Examples

**Before — confusing IDE view:**
```
ContosoSales/
├── ContosoSales.sln
├── ContosoSales.cdsproj   ← not obviously a project
├── src/                   ← orphan folder
├── Plugins/
└── WebResources/
```

**After — three clean peer projects:**
```
ContosoSales/
├── ContosoSales.sln
├── Package/
│   ├── Package.cdsproj
│   └── src/
├── Plugins/
├── WebResources/
└── artifacts/
```

## Related

- [`docs/folder-structure.md`](../../folder-structure.md) — canonical folder spec
- [`src/Flowline/Commands/FlowlineCommand.cs`](../../../src/Flowline/Commands/FlowlineCommand.cs) — `PackageName` and `PackageFolder()`
- [`src/Flowline/Commands/CloneCommand.cs`](../../../src/Flowline/Commands/CloneCommand.cs) — rename logic
