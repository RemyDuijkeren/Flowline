using CliWrap;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Utils;
using Spectre.Console;

namespace Flowline.Services;

// The scaffold half of clone, extracted out of CloneCommand so both `clone` and `init` can run it
// without either command calling the other (KTD1, R3, R7). ScaffoldProjectAsync/ScaffoldDocsAsync
// are the entry points; everything else is a step one of them runs.
//
// No ILogger here: none of the scaffold steps logged when they were CloneCommand private methods,
// and an unused parameter would just be a warning.
public class ProjectScaffolder(IAnsiConsole console, SubprocessCapture capture)
{
    readonly MsBuildSolutionWriter _solutionWriter = new();

    /// <summary>The Dataverse solution folder clone creates: <c>Solution/</c> under the project root.</summary>
    /// <remarks>
    /// The folder clone <em>authors</em>, not one it discovers, and the only place in Flowline allowed to
    /// name it. On a first clone there is no solution file and no <c>.cdsproj</c> yet — clone writes both —
    /// so there is nothing to resolve from. Every command that runs afterwards resolves the folder from the
    /// <c>.cdsproj</c> the solution file records (<see cref="Flowline.Core.Services.SolutionFileLayout.DataverseSolutionFolder"/>),
    /// which is what lets a project move its Dataverse solution folder and keep working. Do not "fix" these
    /// call sites into resolver calls: they run before the thing they would resolve exists.
    /// </remarks>
    internal static string ScaffoldedDataverseSolutionFolder(string slnFolder) => Path.Combine(slnFolder, "Solution");

    /// <summary>Why this solution name can't become a plugin namespace, or <c>null</c> if it can.</summary>
    /// <remarks>
    /// A Dataverse <c>uniquename</c> is <c>[A-Za-z0-9_]</c> starting with a letter or underscore, with no
    /// reserved-word list — so <c>event</c>, <c>class</c> and <c>int</c> are all legal solution names, and
    /// C# keywords are a strict subset of what the platform accepts.
    ///
    /// <c>pac plugin init</c> in a directory named <c>event.Plugins</c> reports success and writes
    /// <c>namespace event.Plugins</c> into its generated files, which fails to compile with CS1001. Clone
    /// refuses up front instead: a verbatim identifier (<c>@event</c>) would compile, but applying it means
    /// editing pac's generated source, and leaving pac's output untouched is the whole mechanism. Only
    /// clone checks — an existing project already has its names.
    ///
    /// Case-sensitive on purpose: <c>Event</c> is a perfectly good namespace.
    /// </remarks>
    internal static string? DescribeCSharpKeywordCollision(string solutionName) =>
        SolutionNameValidator.IsCSharpKeyword(solutionName)
            ? $"Solution name '{solutionName}' is a C# keyword, so the plugin namespace '{solutionName}.Plugins' won't compile. Rename the solution in Dataverse, then clone again."
            : null;

    static readonly string[] s_gitignorePatterns =
    [
        "bin/",
        "obj/",
        "dist/",
        "[Aa]rtifacts/",
        "node_modules/",
        "appsettings.local.json",
        "appsettings.*.local.json",
        ".vs/",
        ".vscode/",
        ".idea/",
        "*.binlog",
        "*.user",
        "*.suo",
        ".env*",
        "!.env.example",
    ];

    /// <summary>
    /// Pulls the solution's XML from Dataverse and scaffolds the project around it — everything that
    /// has to exist before the build. Shared verbatim by <c>clone</c> (a solution already in Dataverse)
    /// and <c>init</c> (one it just created there), so the two commands can't drift into scaffolding
    /// different projects.
    /// </summary>
    /// <returns>The solution file name, for <see cref="ScaffoldDocsAsync"/>.</returns>
    internal async Task<string> ScaffoldProjectAsync(ProjectSolution projectSln, string slnFolder, string environmentUrl,
        string? publisherPrefix, CancellationToken cancellationToken)
    {
        var solutionName = projectSln.UniqueName;
        var cdsprojPath = Path.Combine(ScaffoldedDataverseSolutionFolder(slnFolder), $"{solutionName}.cdsproj");
        var slnFilePath = ResolveSolutionFilePath(slnFolder, solutionName);

        await CloneSolutionFromDataverseAsync(projectSln, slnFolder, cdsprojPath, environmentUrl, cancellationToken);
        await CreateSolutionFileAsync(slnFolder, slnFilePath, cdsprojPath, cancellationToken);

        // The .cdsproj entry CreateSolutionFileAsync just wrote makes the solution file loadable, so the
        // scaffold-skip checks below can ask "is a plugin/WebResources project already registered under
        // any name or location" instead of only "does the default folder hold one" — a project whose
        // Plugins/WebResources project was legitimately moved/renamed (project-structure flexibility)
        // resolves here the same way push/sync/deploy already discover it. Loaded once and reused by both
        // setup calls, matching SolutionFileLayout's one-read contract.
        var layout = await SolutionFileLayout.LoadAsync(slnFolder, cancellationToken);
        await SetupPluginsProjectAsync(slnFolder, slnFilePath, solutionName, layout, cancellationToken);
        var webresourcesFolder = await SetupWebResourcesProjectAsync(slnFolder, slnFilePath, solutionName, layout, cancellationToken);
        SeedWebResourceDistFromSrc(slnFolder, webresourcesFolder, publisherPrefix, solutionName);
        ScaffoldRootGitignore(slnFolder);

        return Path.GetFileName(slnFilePath);
    }

    /// <summary>The agent-facing docs — written only after the build succeeded, so a project that never
    /// compiled doesn't get instructions describing it.</summary>
    internal async Task ScaffoldDocsAsync(string slnFolder, string solutionName, string slnFileName, CancellationToken cancellationToken)
    {
        await ScaffoldAgentsFileAsync(slnFolder, solutionName, slnFileName, cancellationToken);
        await ScaffoldClaudeFileAsync(slnFolder, cancellationToken);
        await new DataverseContextGenerator(console).GenerateAsync(
            Path.Combine(ScaffoldedDataverseSolutionFolder(slnFolder), "src"), solutionName, slnFolder, cancellationToken);
    }

    internal void ScaffoldRootGitignore(string slnFolder)
    {
        var gitignorePath = Path.Combine(slnFolder, ".gitignore");
        var existingLines = File.Exists(gitignorePath) ? File.ReadAllLines(gitignorePath) : [];
        var missing = s_gitignorePatterns.Except(existingLines).ToList();
        if (missing.Count > 0)
            File.AppendAllLines(gitignorePath, missing);
    }

    internal static void DeleteScaffoldedGitignore(string folder)
    {
        var path = Path.Combine(folder, ".gitignore");
        if (File.Exists(path))
            File.Delete(path);
    }

    internal async Task ScaffoldAgentsFileAsync(string slnFolder, string solutionName, string slnFileName, CancellationToken cancellationToken)
    {
        var agentsPath = Path.Combine(slnFolder, "AGENTS.md");
        if (File.Exists(agentsPath))
        {
            console.Skip("AGENTS.md already exists — skipping.");
            return;
        }

        var content = BuildAgentsFileContent(solutionName, slnFileName, Path.GetFileName(ScaffoldedDataverseSolutionFolder(slnFolder)));

        await File.WriteAllTextAsync(agentsPath, content, cancellationToken);
        console.Ok("AGENTS.md created.");
    }

    /// <summary>The agent instructions clone writes into the cloned repo.</summary>
    /// <remarks>
    /// Every path is rendered from the names clone just wrote to disk, so the guidance cannot describe a
    /// layout other than the one beside it. These instructions are read by coding agents that act on them:
    /// they name the concrete paths, because "wherever the solution file says" is not something an agent
    /// can open — and add one line saying the solution file outranks the list, so an agent meeting a moved
    /// project follows it instead of moving the project back.
    ///
    /// Pure and separate from the write so the rendered text is testable without a clone.
    /// </remarks>
    internal static string BuildAgentsFileContent(string solutionName, string slnFileName, string dataverseSolutionFolderName)
    {
        // Padded here rather than hand-aligned, because every project path carries the solution name.
        (string Path, string Note)[] structureRows =
        [
            (".flowline", "environment URLs + solution config"),
            (".gitignore", "root gitignore (bin/obj/node_modules/artifacts/dist)"),
            (slnFileName, "solution file — the authoritative list of this project's projects"),
            ($"{dataverseSolutionFolderName}/{solutionName}.cdsproj", "solution package project (PAC-managed, do not edit)"),
            ($"{dataverseSolutionFolderName}/src/", "unpacked solution XML (git-diffable)"),
            ($"Plugins/{PluginsProjectFileName(solutionName)}", "plugin source, decorated with [Step] attributes"),
            ("Plugins/Models/", "early-bound C# types (from flowline generate)"),
            ($"WebResources/{WebResourcesProjectFileName(solutionName)}", "web resource assets"),
            ("WebResources/dist/", "build output synced to Dataverse (gitignored, regenerated by npm run build)"),
            ("artifacts/", "packed solution zips (gitignored, regenerated by pack)"),
            ("CHANGES.md", "version history"),
            ("docs/", "not scaffolded by clone; created on first `flowline sync` (DATAVERSE_CONTEXT.md)"),
            ("tests/", "not scaffolded by clone; recognized if present"),
        ];
        var pathWidth = structureRows.Max(row => row.Path.Length);
        var projectStructure = string.Join(
            Environment.NewLine,
            structureRows.Select(row => $"{row.Path.PadRight(pathWidth)}  ← {row.Note}"));

        return $$"""
            # Flowline — Agent Instructions

            Flowline is the ALM CLI for this Power Platform solution repo.
            Use Flowline commands instead of PAC CLI directly.

            ## Daily dev loop

            ```
            dotnet build                    # build plugin assembly
            flowline push --dry-run         # preview what would be registered (optional safety check)
            flowline push                   # register DLL + web resources in DEV
            flowline sync                   # pull solution state from DEV, bump version, unpack to XML
            git add . && git commit -m "…"  # commit the unpacked XML diff
            flowline deploy test            # promote to TEST
            flowline deploy prod            # promote to PROD
            ```

            ## Generate early-bound types (run after entities or custom APIs change)

            ```
            flowline generate               # regenerate Plugins/Models/ from solution entities
            ```

            ## Rules

            - Never run `pac solution` commands directly — Flowline wraps them correctly.
            - Always run `flowline push` before `flowline sync` when plugin code changed.
            - `flowline sync` requires no uncommitted changes in `{{dataverseSolutionFolderName}}/src/` (exit code 12 if dirty).
            - `flowline deploy` requires no uncommitted changes under the target solution's folder (exit code 12 if dirty).
            - DEV is the source of truth. Sync captures its state; never hand-edit unpacked XML.
            - `clone`, `push`, and `sync` require an unmanaged solution in DEV — they fail on managed environments.
            - Managed/unmanaged mode is set once via `clone --managed`/`sync --managed`; `deploy` always uses the solution's configured mode.
            - This repo holds one solution, at the root. A second solution gets its own repo.

            ## Project structure

            ```
            {{projectStructure}}
            ```

            Flowline locates the three projects — cdsproj, plugins, web resources — through
            `{{slnFileName}}`, not through these folder names. Move one, update the solution file, and every
            command follows. So when this list and the solution file disagree, the solution file is right
            and this list is stale.

            ## Exit codes

            | Code | Meaning | Fix |
            |------|---------|-----|
            | 0 | Success | |
            | 1 | General error | Check error output |
            | 3 | Not found | Verify solution name matches .flowline config |
            | 4 | Not authenticated | Run: `pac auth create --environment <url>` |
            | 10 | Connection failed | Check environment URL in .flowline |
            | 11 | Config invalid | Check .flowline exists and is valid |
            | 12 | Dirty working directory | Commit or stash changes first |
            | 13 | Build failed | Fix `dotnet build` errors in Plugins/ |
            | 14 | Version conflict | Add the --force <specifier> the error names to overwrite |
            | 15 | Validation failed | Check error output for drift, an invalid --force value, or missing dependencies |
            | 16 | Timeout | PAC CLI 60-min limit hit — retry or check environment health |
            | 17 | Force required | Add the --force <specifier> the message names |
            | 130 | Cancelled | Ctrl+C pressed |

            ## Environments

            Defined in `.flowline`. Use `flowline status` to verify connectivity before running commands.

            ## Dataverse schema context
            - [{{solutionName}}](docs/DATAVERSE_CONTEXT.md)

            @docs/DATAVERSE_CONTEXT.md
            """;
    }

    internal async Task ScaffoldClaudeFileAsync(string slnFolder, CancellationToken cancellationToken)
    {
        var claudePath = Path.Combine(slnFolder, "CLAUDE.md");
        if (File.Exists(claudePath))
        {
            console.Skip("CLAUDE.md already exists — skipping.");
            return;
        }

        await File.WriteAllTextAsync(claudePath, "@AGENTS.md\n", cancellationToken);
        console.Ok("CLAUDE.md created.");
    }

    internal void SeedWebResourceDistFromSrc(string slnFolder, string webresourcesFolder, string? publisherPrefix, string solutionName)
    {
        var srcWebResources = Path.Combine(ScaffoldedDataverseSolutionFolder(slnFolder), "src", "WebResources");
        var publicFolder = Path.Combine(webresourcesFolder, "public");

        if (!Directory.Exists(srcWebResources))
        {
            console.Skip("No WebResources in src — skipping public seed");
            return;
        }

        Directory.CreateDirectory(publicFolder);
        if (Directory.EnumerateFiles(publicFolder, "*.*", SearchOption.AllDirectories).Any())
        {
            console.Skip("WebResources/public already populated — skipping");
            return;
        }

        // PAC unpacks web resources under src/WebResources/<publisher_prefix>_<solution>/
        // That subfolder maps to public/ root — strip one level. Everything else copies as-is.
        var publisherFolderName = publisherPrefix != null ? $"{publisherPrefix}_{solutionName}" : null;
        var publisherRoot = publisherFolderName != null
            ? Path.Combine(srcWebResources, publisherFolderName)
            : null;
        if (publisherRoot != null && !Directory.Exists(publisherRoot)) publisherRoot = null;

        foreach (var srcFile in Directory.EnumerateFiles(srcWebResources, "*.*", SearchOption.AllDirectories))
        {
            if (srcFile.EndsWith(".data.xml", StringComparison.OrdinalIgnoreCase)) continue;

            var sourceRoot = publisherRoot != null && srcFile.StartsWith(publisherRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? publisherRoot
                : srcWebResources;

            var relPath = Path.GetRelativePath(sourceRoot, srcFile);
            var destFile = Path.Combine(publicFolder, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(srcFile, destFile, overwrite: false);
        }

        console.Ok("WebResources/public seeded from src");
        console.Verbose(publicFolder);
    }

    // PAC gives packagetype-sensitive components (FormXml, AppModuleSiteMap, AppModule) a second
    // "{name}_managed.xml" file alongside the plain one only when unpacked with --packagetype Both —
    // its presence is a reliable, on-disk signal that the managed layer was already fetched, without
    // needing to track our own "what did we last sync" state (which could go stale if a prior fetch failed).
    internal static bool HasManagedContent(string dataverseSolutionFolder)
    {
        var srcFolder = Path.Combine(dataverseSolutionFolder, "src");
        return Directory.Exists(srcFolder) &&
               Directory.EnumerateFiles(srcFolder, "*_managed.xml", SearchOption.AllDirectories).Any();
    }

    /// <summary>
    /// Pulls a solution's XML source from Dataverse via <c>pac solution clone</c> into
    /// <see cref="ScaffoldedDataverseSolutionFolder"/> — shared by <c>clone</c> (an existing solution)
    /// and <c>init</c>'s create-new path (a solution <see cref="SolutionCreateService"/> just created
    /// empty in Dataverse), so both scaffold from the same pull (KTD1, R3, R7).
    /// </summary>
    /// <remarks>
    /// Moved out of <c>CloneCommand</c> unchanged (U5) — the one behavioral difference from the
    /// original private method is that the unused <c>Settings</c> parameter is gone; nothing in the
    /// body ever read it.
    /// </remarks>
    internal async Task CloneSolutionFromDataverseAsync(ProjectSolution projectSln, string slnFolder, string cdsprojPath, string environmentUrl,
        CancellationToken cancellationToken)
    {
        if (File.Exists(cdsprojPath))
        {
            // Unmanaged content is always present once cloned (Both is a superset), so only a
            // switch to managed can leave the local source stale — and only when it doesn't
            // already have the managed layer (e.g. a previous clone/sync already fetched Both).
            if (projectSln.IncludeManaged && !HasManagedContent(ScaffoldedDataverseSolutionFolder(slnFolder)))
                await PacUtils.SyncSolutionFromDataverseAsync(projectSln.UniqueName, ScaffoldedDataverseSolutionFolder(slnFolder), environmentUrl, projectSln.IncludeManaged, capture, cancellationToken);
            else
                console.Skip("Solution already cloned — skipping");

            return;
        }

        if (Directory.Exists(ScaffoldedDataverseSolutionFolder(slnFolder)))
            throw new FlowlineException(ExitCode.ConfigInvalid,
                DescribeDataverseSolutionFolderWithoutCdsproj(ScaffoldedDataverseSolutionFolder(slnFolder), Path.GetFileName(cdsprojPath)));

        Directory.CreateDirectory(slnFolder);

        var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);
        CommandResult result = await console.Status().FlowlineSpinner().StartAsync(
            $"Cloning solution [bold]{projectSln.UniqueName}[/] from Dataverse...",
            ctx => Cli.Wrap(cmdName)
                      .WithArguments(args =>
                          args.AddIfNotNull(prefixArgs)
                              .Add("solution")
                              .Add("clone")
                              .Add("--name").Add(projectSln.UniqueName)
                              .Add("--environment").Add(environmentUrl)
                              .Add("--packagetype").Add(projectSln.IncludeManaged ? "Both" : "Unmanaged")
                              .Add("--outputDirectory").Add(slnFolder)
                              .Add("--async"))
                      .WithValidation(CommandResultValidation.None)
                      .WithCapture(capture, ctx)
                      .ExecuteAsync(cancellationToken)
                      .Task);

        if (!result.IsSuccess)
            throw new FlowlineException(ExitCode.GeneralError, "Clone failed — check the environment and your PAC login.");

        // pac writes slnFolder/{SolutionName}/{SolutionName}.cdsproj plus src/. Flowline places that folder
        // under the role-based name and leaves the project file exactly as pac wrote it — the folder answers
        // "what kind of thing lives here", the file answers "which solution", and only the latter escapes
        // the repo.
        Directory.Move(Path.Combine(slnFolder, projectSln.UniqueName), ScaffoldedDataverseSolutionFolder(slnFolder));
        DeleteScaffoldedGitignore(ScaffoldedDataverseSolutionFolder(slnFolder)); // superseded by the project-root .gitignore

        // Duplicated rather than calling FlowlineCommand<T>.FormatDuration — this class isn't a command
        // and has no TSettings to close the generic over. Three branches (not PacUtils's two) to match
        // FormatDuration exactly, including its sub-second ms case.
        var duration = result.RunTime.TotalMinutes >= 1 ? $"{(int)result.RunTime.TotalMinutes}m {result.RunTime.Seconds}s"
            : result.RunTime.TotalSeconds >= 1 ? $"{(int)result.RunTime.TotalSeconds}s"
            : $"{(int)result.RunTime.TotalMilliseconds}ms";
        console.Ok($"Solution [bold]{projectSln.UniqueName}[/] cloned in {duration}");
    }

    internal async Task CreateSolutionFileAsync(string slnFolder, string slnFilePath, string cdsprojPath, CancellationToken cancellationToken)
    {
        var (created, added) = await AddDataverseSolutionProjectAsync(_solutionWriter, slnFolder, slnFilePath, cdsprojPath, cancellationToken);

        if (created)
            console.Ok("Solution file created");
        else
            console.Skip("Solution file already there — skipping");

        var cdsprojFileName = Path.GetFileName(cdsprojPath);
        if (added)
        {
            console.Ok($"[bold]{cdsprojFileName}[/] added to solution file");
            console.Verbose(slnFilePath);
        }
        else
        {
            console.Skip($"{cdsprojFileName} already in the solution file — skipping");
        }
    }

    /// <summary>
    /// Writes the Dataverse solution project's entry into the solution file, creating that file when it is absent.
    /// </summary>
    /// <returns>Whether the solution file was created, and whether an entry was written.</returns>
    /// <remarks>
    /// The writer handles the <c>.cdsproj</c> that <c>dotnet sln add</c> refuses
    /// (https://github.com/dotnet/sdk/issues/47638), so nothing renames the project file to fool the SDK.
    ///
    /// Both flags come from the writer rather than a <c>File.Exists</c> here: the writer stats the file
    /// anyway to choose its write path, so asking again would be a duplicate and a TOCTOU window.
    ///
    /// Separate from the console output so the whole create-and-write path is testable without a clone.
    /// </remarks>
    internal static Task<SolutionWriteResult> AddDataverseSolutionProjectAsync(
        MsBuildSolutionWriter writer,
        string slnFolder,
        string slnFilePath,
        string cdsprojPath,
        CancellationToken cancellationToken = default) =>
        writer.AddProjectAsync(slnFilePath, Path.GetRelativePath(slnFolder, cdsprojPath), cancellationToken);

    /// <summary>The solution file name clone gives a project that has none yet.</summary>
    /// <remarks>
    /// <c>.slnx</c> is the .NET 10 default and holds a <c>.cdsproj</c> fine — verified on SDK 10.0.302
    /// against a real <c>pac solution init</c> project: <c>dotnet sln list</c> enumerates the entry and
    /// <c>dotnet build</c> runs SolutionPackager through to the zip. Flowline reads both formats, so an
    /// existing <c>.sln</c> keeps working and is never converted.
    /// </remarks>
    internal static string SolutionFileName(string solutionName) => $"{solutionName}.slnx";

    /// <summary>Picks the solution file clone writes into, reusing one the project already has.</summary>
    /// <remarks>
    /// Clone is safe to re-run, so it must not answer a second run by creating a second solution file.
    /// A project that already has a <c>.sln</c> keeps it; only a project with no solution file at all gets
    /// a new one. Without this, re-cloning would drop a <c>.slnx</c> beside the existing <c>.sln</c> — the
    /// two-formats-in-one-folder state that makes a bare <c>dotnet build</c> fail with MSB1011, produced
    /// by the tool that warns about it.
    /// </remarks>
    internal static string ResolveSolutionFilePath(string slnFolder, string solutionName) =>
        new MsBuildSolutionReader().FindSolutionFile(slnFolder)
        ?? Path.Combine(slnFolder, SolutionFileName(solutionName));

    /// <summary>Explains a <c>Solution/</c> folder that holds no <c>&lt;SolutionName&gt;.cdsproj</c>.</summary>
    /// <remarks>
    /// Clone no longer renames the project file, so the stray case now means a folder holding a different
    /// solution's project — someone else's clone, or a solution renamed in Dataverse. Naming the file that
    /// is there beats telling the user to delete a folder pac just spent minutes filling.
    /// </remarks>
    internal static string DescribeDataverseSolutionFolderWithoutCdsproj(string dataverseSolutionFolder, string cdsprojFileName)
    {
        var stray = Directory.EnumerateFiles(dataverseSolutionFolder, "*.cdsproj", SearchOption.TopDirectoryOnly).FirstOrDefault();

        var folderName = Path.GetFileName(dataverseSolutionFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return stray != null
            ? $"{folderName}/ holds {Path.GetFileName(stray)}, not {cdsprojFileName}. Rename it and run clone again."
            : $"{folderName}/ is here but {cdsprojFileName} isn't. Move {folderName}/ aside and run clone again.";
    }

    /// <summary>The plugin project file clone scaffolds for a solution.</summary>
    /// <remarks>
    /// Solution-identity naming exists for what leaves the repo: this file's name is what
    /// <c>&lt;AssemblyName&gt;</c> falls back to, so it is what Dataverse's assembly list, the plugin package,
    /// and every trace log and stack trace end up saying. Inside the repo, <c>Plugins/</c> was never ambiguous.
    ///
    /// The solution name goes in verbatim — an underscore is kept, not stripped or PascalCased, because
    /// <c>DWE_Base</c> and <c>DWEBase</c> are two distinct legal solutions and collapsing them reintroduces
    /// the anonymous identity the name exists to remove.
    /// </remarks>
    internal static string PluginsProjectFileName(string solutionName) => $"{solutionName}.Plugins.csproj";

    /// <summary>Whether a plugin project already exists for this solution — in the default folder, or
    /// registered anywhere else in the solution file (a moved/renamed plugin project).</summary>
    /// <remarks>
    /// OR, not a replacement: the default-folder check alone missed a moved/renamed project (this method's
    /// reason for existing), but keeping it alongside the solution-file check means a solution-file read
    /// that somehow misses a real default-folder project still can't cause clone to scaffold a duplicate
    /// on top of it.
    /// </remarks>
    internal static bool PluginsProjectAlreadyRegistered(string pluginsFolder, SolutionFileLayout layout) =>
        (Directory.Exists(pluginsFolder) && Directory.EnumerateFiles(pluginsFolder, "*.csproj").Any())
        || layout.PluginProjects.Count > 0;

    internal async Task SetupPluginsProjectAsync(string slnFolder, string slnFilePath, string solutionName, SolutionFileLayout layout, CancellationToken cancellationToken)
    {
        var pluginsFolder = Path.Combine(slnFolder, "Plugins");
        var pluginsCsproj = Path.Combine(pluginsFolder, PluginsProjectFileName(solutionName));

        // Any plugin project already in the folder, or already registered elsewhere in the solution file,
        // means clone has nothing to add: a fresh scaffold, a resumed clone, the pre-rename
        // Plugins/Plugins.csproj layout (§6), or a plugin project legitimately moved/renamed since the
        // last clone all land here. Skip rather than re-scaffold — every other command discovers the
        // project through the solution file, and re-running init would clobber the user's source or
        // register a spurious duplicate. Never tell them to move a folder holding it.
        if (PluginsProjectAlreadyRegistered(pluginsFolder, layout))
        {
            console.Skip("Plugins project already there — skipping");
            return;
        }

        // A Plugins/ folder with no project is an unrelated collision. pac plugin init needs a clean
        // target, so refuse rather than init into it — but the fix is to clear the empty folder, not to
        // move source that isn't there.
        if (Directory.Exists(pluginsFolder))
            throw new FlowlineException(ExitCode.ConfigInvalid,
                "A 'Plugins' folder is here but holds no project — Flowline scaffolds the plugin project there. " +
                "Remove or rename the empty folder, then run clone again.");

        // pac plugin init takes no --name: it reads PackageId and the generated namespaces off its working
        // directory, and writes neither <AssemblyName> nor <RootNamespace>, so both follow the .csproj
        // filename. Init therefore runs in <SolutionName>.Plugins/ and only the *folder* is renamed —
        // renaming the file too would drop the assembly back to "Plugins" while PackageId and the namespaces
        // stayed prefixed, leaving three identities disagreeing with nothing to signal it.
        var initFolder = Path.Combine(slnFolder, $"{solutionName}.Plugins");

        await console.Status().FlowlineSpinner().StartAsync(
            "Setting up Plugins project...", async ctx =>
            {
                Directory.CreateDirectory(initFolder);

                var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);
                await Cli.Wrap(cmdName)
                         .WithArguments(args => args
                                                .AddIfNotNull(prefixArgs)
                                                .Add("plugin")
                                                .Add("init")) // --skip-signing
                         .WithWorkingDirectory(initFolder)
                         .WithCapture(capture)
                         .ExecuteAsync(cancellationToken);
                DeleteScaffoldedGitignore(initFolder); // superseded by the project-root .gitignore

                Directory.Move(initFolder, pluginsFolder);
                console.Verbose($"Moved {Path.GetFileName(initFolder)} to {Path.GetFileName(pluginsFolder)}");

                // Add Flowline.Attributes NuGet package
                await Cli.Wrap("dotnet")
                         .WithArguments(args => args
                                                .Add("add")
                                                .Add(pluginsCsproj)
                                                .Add("package")
                                                .Add("Flowline.Attributes"))
                         .WithWorkingDirectory(pluginsFolder)
                         .WithCapture(capture)
                         .ExecuteAsync(cancellationToken);

                // Add MinVer NuGet package
                await Cli.Wrap("dotnet")
                         .WithArguments(args => args
                                                .Add("add")
                                                .Add(pluginsCsproj)
                                                .Add("package")
                                                .Add("MinVer"))
                         .WithWorkingDirectory(pluginsFolder)
                         .WithCapture(capture)
                         .ExecuteAsync(cancellationToken);

                // Add Plugins.csproj to the solution. Named explicitly rather than left to the working
                // directory: `dotnet sln` picks the folder's one solution file, and a root can now hold a
                // .sln and a .slnx side by side (what `dotnet sln migrate` leaves behind), where that
                // guess fails outright. `dotnet sln add` takes a .csproj into either format — verified.
                await Cli.Wrap("dotnet")
                         .WithArguments(args => args
                                                .Add("sln")
                                                .Add(slnFilePath)
                                                .Add("add")
                                                .Add(pluginsCsproj))
                         .WithWorkingDirectory(slnFolder)
                         .WithCapture(capture)
                         .ExecuteAsync(cancellationToken);
            });

        console.Ok("Plugins project ready");
    }

    /// <summary>The WebResources project file clone scaffolds for a solution.</summary>
    /// <remarks>
    /// The prefix here is symmetry, and nothing more. This project is <c>Microsoft.Build.NoTargets</c> — it
    /// compiles nothing and produces no assembly, so no name escapes the repo the way the plugin assembly's
    /// does. It takes the prefix so the naming rule has no exception, and so a solution-named node is easy
    /// to pick out with several projects open. The template itself is untouched.
    /// </remarks>
    internal static string WebResourcesProjectFileName(string solutionName) => $"{solutionName}.WebResources.csproj";

    /// <summary>The folder holding the already-registered WebResources project — the default folder, or a
    /// moved/renamed one resolved via <paramref name="layout"/> — or <c>null</c> when none is registered.</summary>
    /// <remarks>
    /// OR, not a replacement — see <see cref="PluginsProjectAlreadyRegistered"/>'s remarks for why.
    /// <see cref="SolutionFileLayout.WebResourcesProjectPath"/> throws <see cref="ExitCode.ConfigInvalid"/>
    /// on a genuine tie between two candidates; that's left to propagate here rather than caught, since
    /// scaffolding a third default-named project on top of an unresolved ambiguity would only make it worse.
    ///
    /// Names the resolved folder, not just whether one exists, so a caller that needs to write into the
    /// real WebResources project (e.g. seeding) stops guessing the default path — the gap that let
    /// <c>SeedWebResourceDistFromSrc</c> pollute a stray <c>WebResources/public</c> folder for a project
    /// that had moved elsewhere.
    /// </remarks>
    internal static string? ResolveExistingWebResourcesFolder(string webresourcesFolder, string webresourcesCsproj, SolutionFileLayout layout) =>
        File.Exists(webresourcesCsproj) ? webresourcesFolder
        : layout.WebResourcesProjectPath is { } path ? Path.GetDirectoryName(path)
        : null;

    /// <summary>Whether a WebResources project already exists for this solution — see <see cref="ResolveExistingWebResourcesFolder"/>.</summary>
    internal static bool WebResourcesProjectAlreadyRegistered(string webresourcesCsproj, SolutionFileLayout layout) =>
        ResolveExistingWebResourcesFolder(Path.GetDirectoryName(webresourcesCsproj)!, webresourcesCsproj, layout) is not null;

    /// <summary>Scaffolds the WebResources project if none is registered yet.</summary>
    /// <returns>The folder holding the WebResources project — existing (possibly moved) or freshly scaffolded.</returns>
    internal async Task<string> SetupWebResourcesProjectAsync(string slnFolder, string slnFilePath, string solutionName, SolutionFileLayout layout, CancellationToken cancellationToken)
    {
        // Create WebResources project if it doesn't exist
        var webresourcesFolder = Path.Combine(slnFolder, "WebResources");
        var webresourcesCsprojName = WebResourcesProjectFileName(solutionName);
        var webresourcesCsproj = Path.Combine(webresourcesFolder, webresourcesCsprojName);
        if (ResolveExistingWebResourcesFolder(webresourcesFolder, webresourcesCsproj, layout) is { } existingFolder)
        {
            console.Skip("WebResources project already there — skipping");
            return existingFolder;
        }

        await console.Status().FlowlineSpinner().StartAsync(
            "Setting up WebResources project...", async ctx =>
            {
                await WriteWebResourcesTemplateAsync(webresourcesFolder, webresourcesCsprojName, cancellationToken);

                console.Verbose($"Created {ConsolePath.FormatRelativePath(webresourcesFolder)}");

                await Cli.Wrap("dotnet")
                         .WithArguments(args => args
                                                .Add("sln")
                                                .Add(slnFilePath)
                                                .Add("add")
                                                .Add(webresourcesCsproj))
                         .WithCapture(capture)
                         .ExecuteAsync(cancellationToken);

                console.Verbose($"Added {Path.GetFileName(webresourcesCsproj)} to solution");
            });

        console.Ok("WebResources project ready");
        return webresourcesFolder;
    }

    /// <summary>
    /// Writes the WebResources template files and folders into <paramref name="webresourcesFolder"/> —
    /// no solution file, no <see cref="SolutionFileLayout"/>, no config read. This is the leaf both
    /// <c>clone</c>/<c>init</c> (via <see cref="SetupWebResourcesProjectAsync"/>) and the standalone
    /// <c>scaffold webresources</c> command reach, so the two paths can't drift into writing different
    /// template sets (KTD1).
    /// </summary>
    /// <remarks>
    /// The project file is written <b>last</b> — deliberately out of the template's natural top-to-bottom
    /// order. <see cref="ResolveExistingWebResourcesFolder"/> treats <c>File.Exists(webresourcesCsproj)</c>
    /// as this scaffold's "already there" marker (the skip <see cref="SetupWebResourcesProjectAsync"/>
    /// checks before ever calling here), and there is no overwrite flag (R12). Written first, a scaffold
    /// interrupted by a crash, Ctrl+C, or a full disk would leave that marker on disk with the rest of the
    /// template missing — every later run would then see "already there" and refuse to finish it, with no
    /// escape hatch. Written last, an interrupted run leaves no marker at all, so a retry starts clean and
    /// finishes the job. Write order isn't part of the on-disk result once the call completes, so this
    /// choice is invisible to any test that only checks the finished file set.
    /// </remarks>
    internal static async Task WriteWebResourcesTemplateAsync(string webresourcesFolder, string projectFileName, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(webresourcesFolder);

        foreach (var (logicalName, relativePath) in s_webResourcesRootFiles)
            await TemplateWriter.WriteAsync(logicalName, Path.Combine(webresourcesFolder, relativePath), cancellationToken);

        Directory.CreateDirectory(Path.Combine(webresourcesFolder, "src", "modules"));
        foreach (var (logicalName, relativePath) in s_webResourcesSrcFiles)
            await TemplateWriter.WriteAsync(logicalName, Path.Combine(webresourcesFolder, relativePath), cancellationToken);

        Directory.CreateDirectory(Path.Combine(webresourcesFolder, "public"));
        Directory.CreateDirectory(Path.Combine(webresourcesFolder, "dist"));

        // Written last -- see remarks above.
        await TemplateWriter.WriteAsync(WebResourcesProjectLogicalName, Path.Combine(webresourcesFolder, projectFileName), cancellationToken);
    }

    /// <summary>The template files that land at the root of the WebResources folder, in write order.</summary>
    static readonly (string LogicalName, string RelativePath)[] s_webResourcesRootFiles =
    [
        ("Flowline.Templates.WebResources.package.json", "package.json"),
        ("Flowline.Templates.WebResources.rollup.config.mjs", "rollup.config.mjs"),
        ("Flowline.Templates.WebResources.tsconfig.json", "tsconfig.json"),
        ("Flowline.Templates.WebResources.eslint.config.mjs", "eslint.config.mjs"),
        ("Flowline.Templates.WebResources.README.md", "README.md"),
    ];

    /// <summary>The template files that land under <c>src/</c>, in write order.</summary>
    static readonly (string LogicalName, string RelativePath)[] s_webResourcesSrcFiles =
    [
        ("Flowline.Templates.WebResources.src.example.ts", "src/example.ts"),
        ("Flowline.Templates.WebResources.src.example-js.js", "src/example-js.js"),
    ];

    const string WebResourcesProjectLogicalName = "Flowline.Templates.WebResources.WebResources.csproj";

    /// <summary>
    /// Every path <see cref="WriteWebResourcesTemplateAsync"/> writes, relative to the WebResources folder,
    /// for a project file named <paramref name="projectFileName"/>.
    /// </summary>
    /// <remarks>
    /// Exists so a caller can check for collisions against the same list the writer uses instead of keeping
    /// its own copy. <c>TemplateWriter</c> truncates rather than skipping, so a caller that guesses this list
    /// and guesses it short silently destroys whatever it missed.
    /// </remarks>
    internal static IEnumerable<string> WebResourcesTemplateRelativePaths(string projectFileName) =>
        s_webResourcesRootFiles.Concat(s_webResourcesSrcFiles)
                               .Select(f => f.RelativePath.Replace('/', Path.DirectorySeparatorChar))
                               .Append(projectFileName);
}
