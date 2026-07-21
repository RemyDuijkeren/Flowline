using System.ComponentModel;
using CliWrap;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using Flowline.Utils;
using Spectre.Console;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class CloneCommand(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService, ILoggerFactory loggerFactory, SubprocessCapture capture) :
    FlowlineCommand<CloneCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture)
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "<solution>")]
        [Description("Solution to clone into this repo")]
        public string? Solution { get; set; }

        [CommandOption("--prod <URL>")]
        [Description("Production environment URL to clone solution from")]
        public string? ProdUrl { get; set; }

        [CommandOption("--uat <URL>")]
        [Description("UAT environment URL to clone solution from")]
        public string? UatUrl { get; set; }

        [CommandOption("--test <URL>")]
        [Description("Test environment URL to clone solution from")]
        public string? TestUrl { get; set; }

        [CommandOption("--dev <URL>")]
        [Description("Development environment URL to clone solution from")]
        public string? DevUrl { get; set; }

        [CommandOption("--managed [false]")]
        [Description("Include managed artifacts (--managed false resets to default)")]
        [DefaultValue(true)]
        public FlagValue<bool> IncludeManaged { get; set; } = null!;
    }

    readonly MsBuildSolutionWriter _solutionWriter = new();

    protected override bool RequiresProject => false;
    protected override string[] ValidForceSpecifiers => FlowlineSettings.ConfigOnlyValidSpecifiers;

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Save all provided URLs to config first (no API calls, just config update + prompt on conflict)
        Config!.GetOrUpdateProdUrl(settings.ProdUrl, settings);
        Config!.GetOrUpdateUatUrl(settings.UatUrl, settings);
        Config!.GetOrUpdateTestUrl(settings.TestUrl, settings);
        Config!.GetOrUpdateDevUrl(settings.DevUrl, settings);

        var (sourceEnv, projectSln, solutionInfo) = await FindUnmanagedSourceAsync(settings, cancellationToken);
        Logger.LogInformation("source={EnvironmentUrl} solution={SolutionName}", sourceEnv.EnvironmentUrl, projectSln.UniqueName);

        // Before anything is written: the solution name becomes a C# namespace in the scaffolded plugin
        // project, and a keyword there produces source that doesn't compile.
        if (DescribeCSharpKeywordCollision(projectSln.UniqueName) is { } keywordCollision)
            throw new FlowlineException(ExitCode.ValidationFailed, keywordCollision);

        Config.Save();
        Console.Verbose($"Project configuration saved to {ProjectConfig.s_configFileName}");

        var slnFolder = RootFolder;
        var solutionName = projectSln.UniqueName;

        var cdsprojPath = Path.Combine(ScaffoldedPackageFolder(slnFolder), $"{solutionName}.cdsproj");
        var slnFilePath = ResolveSolutionFilePath(slnFolder, solutionName);
        var slnFileName = Path.GetFileName(slnFilePath);

        await CloneSolutionFromDataverseAsync(projectSln, slnFolder, cdsprojPath, sourceEnv.EnvironmentUrl!, settings, cancellationToken);
        await CreateSolutionFileAsync(slnFolder, slnFilePath, cdsprojPath, cancellationToken);
        await SetupPluginsProjectAsync(slnFolder, slnFilePath, solutionName, settings, cancellationToken);
        await SetupWebResourcesProjectAsync(slnFolder, slnFilePath, solutionName, settings, cancellationToken);
        SeedWebResourceDistFromSrc(slnFolder, solutionInfo.PublisherPrefix, projectSln.UniqueName, settings);

        ScaffoldRootGitignore();

        // Pack the solution in pac to validate it
        Logger.LogInformation("Validating pack: {SolutionName}", projectSln.UniqueName);
        var artifactsFolder = Path.Combine(slnFolder, "artifacts");
        Directory.CreateDirectory(artifactsFolder);
        if (await PacUtils.PackSolutionAsync(projectSln, ScaffoldedPackageFolder(slnFolder), artifactsFolder, false, _capture, cancellationToken) != 0) return (int)ExitCode.BuildFailed;
        if (projectSln.IncludeManaged &&
            await PacUtils.PackSolutionAsync(projectSln, ScaffoldedPackageFolder(slnFolder), artifactsFolder, true, _capture, cancellationToken) != 0)
        {
            return (int)ExitCode.BuildFailed;
        }

        // Build the solution in dotnet to validate it (Debug = unmanaged, Release = managed!)
        Logger.LogInformation("Validating build: {SlnFolder}", slnFolder);
        if (await DotNetUtils.BuildSolutionAsync(slnFolder, DotnetBuild.Debug, _capture, cancellationToken) != 0) return (int)ExitCode.BuildFailed;
        if (projectSln.IncludeManaged &&
            await DotNetUtils.BuildSolutionAsync(slnFolder, DotnetBuild.Release, _capture, cancellationToken) != 0)
        {
            return (int)ExitCode.BuildFailed;
        }

        await ScaffoldAgentsFileAsync(projectSln.UniqueName, slnFileName, cancellationToken);
        await ScaffoldClaudeFileAsync(cancellationToken);
        await new DataverseContextGenerator(Console).GenerateAsync(
            Path.Combine(ScaffoldedPackageFolder(slnFolder), "src"), projectSln.UniqueName, RootFolder, cancellationToken);

        Console.Done("Cloned! Use 'push' and 'sync' to keep it in flow. ヽ(•‿•)ノ");
        return 0;
    }

    /// <summary>The package folder clone creates: <c>Solution/</c> under the project root.</summary>
    /// <remarks>
    /// The folder clone <em>authors</em>, not one it discovers, and the only place in Flowline allowed to
    /// name it. On a first clone there is no solution file and no <c>.cdsproj</c> yet — clone writes both —
    /// so there is nothing to resolve from. Every command that runs afterwards resolves the folder from the
    /// <c>.cdsproj</c> the solution file records (<see cref="Flowline.Core.Services.SolutionFileLayout.DataverseSolutionFolder"/>),
    /// which is what lets a project move its package folder and keep working. Do not "fix" these call sites
    /// into resolver calls: they run before the thing they would resolve exists.
    /// </remarks>
    static string ScaffoldedPackageFolder(string slnFolder) => Path.Combine(slnFolder, "Solution");

    /// <summary>The C# reserved keywords, which cannot appear unescaped in a namespace declaration.</summary>
    private static readonly HashSet<string> s_csharpKeywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class",
        "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event",
        "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if",
        "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null",
        "object", "operator", "out", "override", "params", "private", "protected", "public", "readonly",
        "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct",
        "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe",
        "ushort", "using", "virtual", "void", "volatile", "while",
    ];

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
        s_csharpKeywords.Contains(solutionName)
            ? $"Solution name '{solutionName}' is a C# keyword, so the plugin namespace '{solutionName}.Plugins' won't compile. Rename the solution in Dataverse, then clone again."
            : null;

    private static readonly string[] s_gitignorePatterns =
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

    private void ScaffoldRootGitignore()
    {
        var gitignorePath = Path.Combine(RootFolder, ".gitignore");
        var existingLines = File.Exists(gitignorePath) ? File.ReadAllLines(gitignorePath) : [];
        var missing = s_gitignorePatterns.Except(existingLines).ToList();
        if (missing.Count > 0)
            File.AppendAllLines(gitignorePath, missing);
    }

    private static void DeleteScaffoldedGitignore(string folder)
    {
        var path = Path.Combine(folder, ".gitignore");
        if (File.Exists(path))
            File.Delete(path);
    }

    private async Task ScaffoldAgentsFileAsync(string solutionName, string slnFileName, CancellationToken cancellationToken)
    {
        var agentsPath = Path.Combine(RootFolder, "AGENTS.md");
        if (File.Exists(agentsPath))
        {
            Console.Skip("AGENTS.md already exists — skipping.");
            return;
        }

        var content = BuildAgentsFileContent(solutionName, slnFileName, Path.GetFileName(ScaffoldedPackageFolder(RootFolder)));

        await File.WriteAllTextAsync(agentsPath, content, cancellationToken);
        Console.Ok("AGENTS.md created.");
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
    internal static string BuildAgentsFileContent(string solutionName, string slnFileName, string packageFolderName)
    {
        // Padded here rather than hand-aligned, because every project path carries the solution name.
        (string Path, string Note)[] structureRows =
        [
            (".flowline", "environment URLs + solution config"),
            (".gitignore", "root gitignore (bin/obj/node_modules/artifacts/dist)"),
            (slnFileName, "solution file — the authoritative list of this project's projects"),
            ($"{packageFolderName}/{solutionName}.cdsproj", "solution package project (PAC-managed, do not edit)"),
            ($"{packageFolderName}/src/", "unpacked solution XML (git-diffable)"),
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
            - `flowline sync` requires no uncommitted changes in `{{packageFolderName}}/src/` (exit code 12 if dirty).
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

    private async Task ScaffoldClaudeFileAsync(CancellationToken cancellationToken)
    {
        var claudePath = Path.Combine(RootFolder, "CLAUDE.md");
        if (File.Exists(claudePath))
        {
            Console.Skip("CLAUDE.md already exists — skipping.");
            return;
        }

        await File.WriteAllTextAsync(claudePath, "@AGENTS.md\n", cancellationToken);
        Console.Ok("CLAUDE.md created.");
    }

    private async Task<(EnvironmentInfo sourceEnv, ProjectSolution projectSolution, SolutionInfo solutionInfo)> FindUnmanagedSourceAsync(Settings settings,
        CancellationToken cancellationToken)
    {
        foreach (var role in new[] { EnvironmentRole.Prod, EnvironmentRole.Uat, EnvironmentRole.Test, EnvironmentRole.Dev })
        {
            var configUrl = role switch
            {
                EnvironmentRole.Prod => Config!.ProdUrl,
                EnvironmentRole.Uat  => Config!.UatUrl,
                EnvironmentRole.Test => Config!.TestUrl,
                EnvironmentRole.Dev  => Config!.DevUrl,
                _ => null
            };
            if (string.IsNullOrEmpty(configUrl)) continue;

            var (env, _) = await GetAndCheckEnvironmentInfoAsync(role, null, settings, cancellationToken);
            var (sln, info) = await GetAndCheckSolutionAsync(
                settings.Solution, env.EnvironmentUrl!, settings.IncludeManaged.IsSet ? settings.IncludeManaged.Value : (bool?)null, settings, cancellationToken);

            if (info.IsManaged)
            {
                var label = role switch { EnvironmentRole.Prod => "Prod", EnvironmentRole.Uat => "UAT", EnvironmentRole.Test => "Test", _ => "Dev" };
                Console.MarkupLine($"[dim]{label} solution is managed — skipping[/]");
                continue;
            }

            return (env, sln, info);
        }

        throw new FlowlineException(ExitCode.NotFound, "No unmanaged environment found — provide a --dev, --test, --uat, or --prod URL with an unmanaged solution.");
    }

    private void SeedWebResourceDistFromSrc(string slnFolder, string? publisherPrefix, string solutionName, Settings settings)
    {
        var srcWebResources = Path.Combine(ScaffoldedPackageFolder(slnFolder), "src", "WebResources");
        var publicFolder = Path.Combine(slnFolder, "WebResources", "public");

        if (!Directory.Exists(srcWebResources))
        {
            Console.Skip("No WebResources in src — skipping public seed");
            return;
        }

        Directory.CreateDirectory(publicFolder);
        if (Directory.EnumerateFiles(publicFolder, "*.*", SearchOption.AllDirectories).Any())
        {
            Console.Skip("WebResources/public already populated — skipping");
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

        Console.Ok("WebResources/public seeded from src");
        Console.Verbose(publicFolder);
    }

    private async Task CloneSolutionFromDataverseAsync(ProjectSolution projectSln, string slnFolder, string cdsprojPath, string environmentUrl,
        Settings settings, CancellationToken cancellationToken)
    {
        if (File.Exists(cdsprojPath))
        {
            // Unmanaged content is always present once cloned (Both is a superset), so only a
            // switch to managed can leave the local source stale — and only when it doesn't
            // already have the managed layer (e.g. a previous clone/sync already fetched Both).
            if (projectSln.IncludeManaged && !HasManagedContent(ScaffoldedPackageFolder(slnFolder)))
                await PacUtils.SyncSolutionFromDataverseAsync(projectSln.UniqueName, ScaffoldedPackageFolder(slnFolder), environmentUrl, projectSln.IncludeManaged, _capture, cancellationToken);
            else
                Console.Skip("Solution already cloned — skipping");

            return;
        }

        if (Directory.Exists(ScaffoldedPackageFolder(slnFolder)))
            throw new FlowlineException(ExitCode.ConfigInvalid,
                DescribePackageFolderWithoutCdsproj(ScaffoldedPackageFolder(slnFolder), Path.GetFileName(cdsprojPath)));

        Directory.CreateDirectory(slnFolder);

        var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);
        CommandResult result = await Console.Status().FlowlineSpinner().StartAsync(
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
                      .WithCapture(_capture, ctx)
                      .ExecuteAsync(cancellationToken)
                      .Task);

        if (!result.IsSuccess)
            throw new FlowlineException(ExitCode.GeneralError, "Clone failed — check the environment and your PAC login.");

        // pac writes slnFolder/{SolutionName}/{SolutionName}.cdsproj plus src/. Flowline places that folder
        // under the role-based name and leaves the project file exactly as pac wrote it — the folder answers
        // "what kind of thing lives here", the file answers "which solution", and only the latter escapes
        // the repo.
        Directory.Move(Path.Combine(slnFolder, projectSln.UniqueName), ScaffoldedPackageFolder(slnFolder));
        DeleteScaffoldedGitignore(ScaffoldedPackageFolder(slnFolder)); // superseded by the project-root .gitignore

        Console.Ok($"Solution [bold]{projectSln.UniqueName}[/] cloned in {FormatDuration(result.RunTime)}");
    }

    // PAC gives packagetype-sensitive components (FormXml, AppModuleSiteMap, AppModule) a second
    // "{name}_managed.xml" file alongside the plain one only when unpacked with --packagetype Both —
    // its presence is a reliable, on-disk signal that the managed layer was already fetched, without
    // needing to track our own "what did we last sync" state (which could go stale if a prior fetch failed).
    internal static bool HasManagedContent(string packageFolder)
    {
        var srcFolder = Path.Combine(packageFolder, "src");
        return Directory.Exists(srcFolder) &&
               Directory.EnumerateFiles(srcFolder, "*_managed.xml", SearchOption.AllDirectories).Any();
    }

    private async Task CreateSolutionFileAsync(string slnFolder, string slnFilePath, string cdsprojPath, CancellationToken cancellationToken)
    {
        var (created, added) = await AddPackageProjectAsync(_solutionWriter, slnFolder, slnFilePath, cdsprojPath, cancellationToken);

        if (created)
            Console.Ok("Solution file created");
        else
            Console.Skip("Solution file already there — skipping");

        var cdsprojFileName = Path.GetFileName(cdsprojPath);
        if (added)
        {
            Console.Ok($"[bold]{cdsprojFileName}[/] added to solution file");
            Console.Verbose(slnFilePath);
        }
        else
        {
            Console.Skip($"{cdsprojFileName} already in the solution file — skipping");
        }
    }

    /// <summary>
    /// Writes the package project's entry into the solution file, creating that file when it is absent.
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
    internal static Task<SolutionWriteResult> AddPackageProjectAsync(
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
    internal static string DescribePackageFolderWithoutCdsproj(string packageFolder, string cdsprojFileName)
    {
        var stray = Directory.EnumerateFiles(packageFolder, "*.cdsproj", SearchOption.TopDirectoryOnly).FirstOrDefault();

        var folderName = Path.GetFileName(packageFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

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

    private async Task SetupPluginsProjectAsync(string slnFolder, string slnFilePath, string solutionName, Settings settings, CancellationToken cancellationToken)
    {
        var pluginsFolder = Path.Combine(slnFolder, "Plugins");
        var pluginsCsproj = Path.Combine(pluginsFolder, PluginsProjectFileName(solutionName));

        // Any plugin project already in the folder means clone has nothing to add: a fresh scaffold, a
        // resumed clone, or the pre-rename Plugins/Plugins.csproj layout (§6) all land here. Skip rather
        // than re-scaffold — every other command discovers the project through the solution file, and
        // re-running init would clobber the user's source. Never tell them to move a folder holding it.
        if (Directory.Exists(pluginsFolder) && Directory.EnumerateFiles(pluginsFolder, "*.csproj").Any())
        {
            Console.Skip("Plugins project already there — skipping");
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

        await Console.Status().FlowlineSpinner().StartAsync(
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
                         .WithCapture(_capture)
                         .ExecuteAsync(cancellationToken);
                DeleteScaffoldedGitignore(initFolder); // superseded by the project-root .gitignore

                Directory.Move(initFolder, pluginsFolder);
                Console.Verbose($"Moved {Path.GetFileName(initFolder)} to {Path.GetFileName(pluginsFolder)}");

                // Add Flowline.Attributes NuGet package
                await Cli.Wrap("dotnet")
                         .WithArguments(args => args
                                                .Add("add")
                                                .Add(pluginsCsproj)
                                                .Add("package")
                                                .Add("Flowline.Attributes"))
                         .WithWorkingDirectory(pluginsFolder)
                         .WithCapture(_capture)
                         .ExecuteAsync(cancellationToken);

                // Add MinVer NuGet package
                await Cli.Wrap("dotnet")
                         .WithArguments(args => args
                                                .Add("add")
                                                .Add(pluginsCsproj)
                                                .Add("package")
                                                .Add("MinVer"))
                         .WithWorkingDirectory(pluginsFolder)
                         .WithCapture(_capture)
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
                         .WithCapture(_capture)
                         .ExecuteAsync(cancellationToken);
            });

        Console.Ok("Plugins project ready");
    }

    /// <summary>The WebResources project file clone scaffolds for a solution.</summary>
    /// <remarks>
    /// The prefix here is symmetry, and nothing more. This project is <c>Microsoft.Build.NoTargets</c> — it
    /// compiles nothing and produces no assembly, so no name escapes the repo the way the plugin assembly's
    /// does. It takes the prefix so the naming rule has no exception, and so a solution-named node is easy
    /// to pick out with several projects open. The template itself is untouched.
    /// </remarks>
    internal static string WebResourcesProjectFileName(string solutionName) => $"{solutionName}.WebResources.csproj";

    private async Task SetupWebResourcesProjectAsync(string slnFolder, string slnFilePath, string solutionName, Settings settings, CancellationToken cancellationToken)
    {
        // Create WebResources project if it doesn't exist
        var webresourcesFolder = Path.Combine(slnFolder, "WebResources");
        var webresourcesCsproj = Path.Combine(webresourcesFolder, WebResourcesProjectFileName(solutionName));
        if (File.Exists(webresourcesCsproj))
        {
            Console.Skip("WebResources project already there — skipping");
            return;
        }

        await Console.Status().FlowlineSpinner().StartAsync(
            "Setting up WebResources project...", async ctx =>
            {
                Directory.CreateDirectory(webresourcesFolder);

                await TemplateWriter.WriteAsync("Flowline.Templates.WebResources.WebResources.csproj", webresourcesCsproj, cancellationToken);
                await TemplateWriter.WriteAsync("Flowline.Templates.WebResources.package.json", Path.Combine(webresourcesFolder, "package.json"), cancellationToken);
                await TemplateWriter.WriteAsync("Flowline.Templates.WebResources.rollup.config.mjs", Path.Combine(webresourcesFolder, "rollup.config.mjs"), cancellationToken);
                await TemplateWriter.WriteAsync("Flowline.Templates.WebResources.tsconfig.json", Path.Combine(webresourcesFolder, "tsconfig.json"), cancellationToken);
                await TemplateWriter.WriteAsync("Flowline.Templates.WebResources.eslint.config.mjs", Path.Combine(webresourcesFolder, "eslint.config.mjs"), cancellationToken);
                await TemplateWriter.WriteAsync("Flowline.Templates.WebResources.README.md", Path.Combine(webresourcesFolder, "README.md"), cancellationToken);

                Directory.CreateDirectory(Path.Combine(webresourcesFolder, "src", "modules"));
                await TemplateWriter.WriteAsync("Flowline.Templates.WebResources.src.example.ts", Path.Combine(webresourcesFolder, "src", "example.ts"), cancellationToken);
                Directory.CreateDirectory(Path.Combine(webresourcesFolder, "public"));
                Directory.CreateDirectory(Path.Combine(webresourcesFolder, "dist"));

                Console.Verbose($"Created {ConsolePath.FormatRelativePath(webresourcesFolder)}");

                await Cli.Wrap("dotnet")
                         .WithArguments(args => args
                                                .Add("sln")
                                                .Add(slnFilePath)
                                                .Add("add")
                                                .Add(webresourcesCsproj))
                         .WithCapture(_capture)
                         .ExecuteAsync(cancellationToken);

                Console.Verbose($"Added {Path.GetFileName(webresourcesCsproj)} to solution");
            });

        Console.Ok("WebResources project ready");
    }
}
