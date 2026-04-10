### Flowline CLI Folder Structure

To support a scalable and developer-friendly environment for Dataverse development, Flowline CLI uses the following folder structure. This layout ensures that multiple Dataverse solutions can coexist in the same project root while maintaining a clear separation between solution artifacts, custom logic (Extensions), and front-end assets (WebResources).

#### 1. Folder Hierarchy Overview

The structure is organized under a root `solutions/` directory. Each Dataverse solution has its own dedicated folder containing a `.sln` file that serves as the entry point for developers.

```text
ProjectRoot/
├── .flowline (Project configuration)
├── .gitignore
└── solutions/
    ├── SolutionName_A/
    │   ├── SolutionName_A.sln           <-- Root solution file
    │   ├── SolutionPackage/             <-- .cdsproj & Solution Source
    │   │   ├── SolutionPackage.cdsproj
    │   │   └── src/
    │   ├── Extensions/                  <-- .csproj (Plugins, Workflows, Custom APIs)
    │   │   └── Extensions.csproj
    │   └── WebResources/                <-- .csproj & Web assets (JS, CSS, HTML)
    │       ├── WebResources.csproj
    │       ├── src/                     <-- Source files (e.g. TypeScript, SCSS)
    │       ├── public/                  <-- Static assets
    │       └── dist/                    <-- Build output (to be synced to Dataverse)
    └── SolutionName_B/                  <-- Second solution
        ├── SolutionName_B.sln
        ├── SolutionPackage/
        ├── Extensions/
        └── WebResources/
```

#### 2. Component Breakdown

- **Root `.sln` File**: Located at `solutions/<SolutionName>/<SolutionName>.sln`. This allows developers to open a single file in Visual Studio or JetBrains Rider to manage the `SolutionPackage`, `Extensions`, and `WebResources` projects simultaneously.
- **`SolutionPackage/`**: Contains the `SolutionPackage.cdsproj` file and the unpacked XML source files (from `pac solution clone`). This folder acts as the "orchestrator" that packages the metadata and the output of the other projects into the final Dataverse solution `.zip`.
- **`Extensions/`**: The home for all server-side logic (`Extensions.csproj`), including Plugins, Workflow Activities, Custom Actions, and Custom APIs. Using a single project for these components simplifies dependency management and deployment.
- **`WebResources/`**: A dedicated folder for web assets and its corresponding `.NET` project (`WebResources.csproj`). This allows web assets to be part of the solution and easily managed through the build pipeline.
    - **`src/`**: Contains the source files for web development (e.g., TypeScript, SCSS, ES6 JavaScript).
    - **`public/`**: Stores static assets that don't need processing, like images or legacy scripts.
    - **`dist/`**: The target folder for build processes (e.g., `npm run build`). This folder contains the final artifacts that will be synchronized with Dataverse.

#### 3. Design Principles

- **Multi-Solution Support**: The `solutions/` prefix allows Flowline CLI to discover and manage multiple solutions within a single repository.
- **Standardized Developer Experience**: Using `.sln` files mirrors industry best practices and provides a familiar environment for .NET developers.
- **Future-Proof**: The layout is easily extendable; new project types (e.g., Unit Tests) can be added as subfolders under the solution directory and included in the `.sln`.
- **CLI Compatibility**: Flowline CLI can predictably locate the `.cdsproj` and other assets, making commands like `push`, `sync`, and `deploy` robust and consistent.
