---
title: WebResources project scaffolding via embedded resource templates
date: 2026-06-04
category: docs/solutions/tooling-decisions
module: clone
problem_type: tooling_decision
component: tooling
severity: medium
applies_when:
  - Adding a new project type to flowline clone scaffolding
  - Scaffolding non-C# MSBuild projects from a .NET CLI tool
  - Integrating a front-end build pipeline (npm/rollup) into dotnet build
tags:
  - embedded-resources
  - msbuild
  - scaffolding
  - webresources
  - typescript
  - rollup
  - microsoft-build-notargets
  - clone
---

# WebResources project scaffolding via embedded resource templates

## Context

`flowline clone` created the WebResources project by running `dotnet new classlib`, which generated a `Microsoft.NET.Sdk` project that tries to compile C#. That is the wrong SDK for a project containing only TypeScript/JavaScript. No front-end tooling (package.json, rollup.config.mjs, tsconfig.json, eslint.config.mjs) was generated, leaving developers with an empty shell requiring manual setup before any web resources could build or sync.

Three template storage options were evaluated: GitHub template repo (network dependency, version drift), `dotnet new` template NuGet package (overkill, separate publish pipeline), and embedded resources in the Flowline assembly. Embedded resources were chosen.

## Guidance

### Template storage: embedded resources with explicit `LogicalName`

Store each template file as an `<EmbeddedResource>` in `Flowline.csproj` with an explicit `<LogicalName>`. Templates version with the CLI, require no external dependencies at runtime, and produce readable git diffs.

```xml
<ItemGroup>
  <EmbeddedResource Include="Templates\WebResources\WebResources.csproj">
    <LogicalName>Flowline.Templates.WebResources.WebResources.csproj</LogicalName>
  </EmbeddedResource>
  <EmbeddedResource Include="Templates\WebResources\package.json">
    <LogicalName>Flowline.Templates.WebResources.package.json</LogicalName>
  </EmbeddedResource>
  <!-- ... repeat for rollup.config.mjs, tsconfig.json, eslint.config.mjs, README.md, src/example.ts -->
</ItemGroup>
```

Use explicit `<LogicalName>` — the default manifest name embeds path separators which differ between Windows (`\`) and Linux (`/`), making names unpredictable on cross-platform builds.

### TemplateWriter utility

A single static class loads a resource by logical name and writes to disk:

```csharp
public static class TemplateWriter
{
    public static async Task WriteAsync(string logicalName, string targetPath, CancellationToken cancellationToken = default)
    {
        var stream = typeof(TemplateWriter).Assembly.GetManifestResourceStream(logicalName);
        if (stream is null)
            throw new FlowlineException($"Template '{logicalName}' not found in assembly manifest.");
        await using (stream)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using var file = File.Create(targetPath);
            await stream.CopyToAsync(file, cancellationToken);
        }
    }
}
```

Throw `FlowlineException` on missing resource — a missing resource is a code bug, not a user error.

### WebResources csproj: `Microsoft.Build.NoTargets` + split npm targets

Use `Sdk="Microsoft.Build.NoTargets/3.7.134"` for projects that participate in `dotnet build` but compile nothing. Two targets handle the npm pipeline:

```xml
<Project Sdk="Microsoft.Build.NoTargets/3.7.134">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>

  <ItemGroup>
    <NpmInputFiles Include="src\**\*.ts;public\**\*;rollup.config.mjs;eslint.config.mjs;package.json;tsconfig.json" />
  </ItemGroup>

  <!-- Incremental: only re-runs when package.json changes -->
  <Target Name="NpmInstall"
          Inputs="package.json"
          Outputs="$(BaseIntermediateOutputPath)npm-install.stamp">
    <MakeDir Directories="$(BaseIntermediateOutputPath)" />
    <Exec Command="npm install" />
    <Touch Files="$(BaseIntermediateOutputPath)npm-install.stamp" AlwaysCreate="true" />
  </Target>

  <!-- Incremental: only re-runs when source files change -->
  <Target Name="NpmBuild" DependsOnTargets="NpmInstall" BeforeTargets="Build"
          Inputs="@(NpmInputFiles)"
          Outputs="$(BaseIntermediateOutputPath)npm-build.stamp">
    <MakeDir Directories="$(BaseIntermediateOutputPath)" />
    <Exec Command="npm run build" />
    <Touch Files="$(BaseIntermediateOutputPath)npm-build.stamp" AlwaysCreate="true" />
  </Target>
</Project>
```

**Critical**: `NpmInstall` must use the literal path `Inputs="package.json"` rather than an ItemGroup. An ItemGroup causes Rider/VS to show `package.json` twice in the solution explorer (once per group membership).

**Critical**: `NpmInstall` must run before `NpmBuild`. Without it, `rollup` is not in PATH because `node_modules/.bin` doesn't exist yet, causing an MSB3073 build error on first clone.

### Rollup config: include a starter entry point

`rollup.config.mjs` auto-discovers `src/*.ts` files. If `src/` is empty, rollup exports an empty array which it rejects with `Config file must export an options object`. Always scaffold at least one starter `.ts` file in `src/`:

```typescript
// src/example.ts — rename to match your Dataverse table (e.g. account.ts)
export function onLoad(executionContext: Xrm.Events.EventContext): void {
    const formContext = executionContext.getFormContext();
}
```

### tsconfig: explicit `"types": ["xrm"]`

With `"module": "ES2022"`, TypeScript does not auto-discover `@types/xrm` global declarations. Add explicitly:

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "ES2022",
    "lib": ["ES2022", "DOM"],
    "types": ["xrm"],
    "noEmit": true,
    "strict": true
  }
}
```

Without this, every use of `Xrm.*` emits `TS2503: Cannot find namespace 'Xrm'` as a build warning.

### public/ vs dist/ seeding

PAC unpacks existing web resources into `src/WebResources/<publisher>_<solution>/`. On clone, seed these into `WebResources/public/` — not `dist/`. `dist/` is build output generated by rollup; seeding into it would be overwritten on next `npm run build`. `public/` is the source input for static files.

## Why This Matters

- **Wrong SDK** (`Microsoft.NET.Sdk` on a TypeScript project) shows as a broken C# project in IDE and wastes a build step trying to compile nothing.
- **Missing npm targets** cause `dotnet build` to fail with `'rollup' is not recognized` because packages were never installed.
- **Templates version with the CLI** — embedded resources ensure every `flowline clone` produces exactly the same scaffolding as the CLI version ships. External repos or NuGet templates can drift.

## When to Apply

- Adding a new project type scaffold to `flowline clone` (e.g., a future Canvas Apps project)
- Any `Microsoft.Build.NoTargets` project that wraps a non-.NET build tool
- Any CLI tool that scaffolds project files that need exact content control

## Examples

**Before** (in `SetupWebResourcesProjectAsync`):
```csharp
await Cli.Wrap("dotnet")
         .WithArguments(args => args.Add("new").Add("classlib").Add("--name").Add(WebResourcesName))
         .WithWorkingDirectory(slnFolder)
         .ExecuteAsync(cancellationToken);
File.Delete(Path.Combine(webresourcesFolder, "Class1.cs"));
```

**After**:
```csharp
Directory.CreateDirectory(webresourcesFolder);
await TemplateWriter.WriteAsync("Flowline.Templates.WebResources.WebResources.csproj", webresourcesCsproj, cancellationToken);
await TemplateWriter.WriteAsync("Flowline.Templates.WebResources.package.json", Path.Combine(webresourcesFolder, "package.json"), cancellationToken);
// ... repeat for rollup.config.mjs, tsconfig.json, eslint.config.mjs, README.md
await TemplateWriter.WriteAsync("Flowline.Templates.WebResources.src.example.ts", Path.Combine(webresourcesFolder, "src", "example.ts"), cancellationToken);
```

## Related

- `src/Flowline/Templates/WebResources/` — template files
- `src/Flowline/Utils/TemplateWriter.cs` — template writer utility
- `src/Flowline/Commands/CloneCommand.cs` — `SetupWebResourcesProjectAsync()`
- `docs/plans/2026-06-04-001-feat-webresources-embedded-templates-plan.md` — implementation plan
