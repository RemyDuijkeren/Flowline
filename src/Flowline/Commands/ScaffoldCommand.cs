using System.ComponentModel;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

/// <summary>
/// <c>flowline scaffold &lt;part&gt;</c> — writes a project template into a folder without touching Dataverse.
/// </summary>
/// <remarks>
/// The WebResources template was previously reachable only as a side effect of <c>clone</c> or <c>init</c>,
/// both of which connect to Dataverse to bring a whole solution into a repo. Someone who wants the template
/// itself — working outside project mode, or adding web resources to a plugin-only repo migrated off spkl —
/// had to hand-copy files out of the Flowline repository.
///
/// The command has two modes and says which one it resolved before writing anything. Outside a Flowline
/// project it writes the template and nothing else; inside one it names the project after the configured
/// solution and registers it in the solution file. The two never mix: there is no path that turns a
/// standalone folder into a project, because <c>clone</c> and <c>init</c> own that.
/// </remarks>
public class ScaffoldCommand(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService, ILoggerFactory loggerFactory, SubprocessCapture capture, ProjectScaffolder projectScaffolder, NuGetVersionClient nuGetVersionClient)
    : FlowlineCommand<ScaffoldCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture, nuGetVersionClient)
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "<part>")]
        [Description("What to scaffold. Only 'webresources' today")]
        public string Part { get; set; } = null!;
    }

    /// <summary>Which shape of scaffold a folder gets.</summary>
    internal enum ScaffoldMode
    {
        /// <summary>No Flowline project here — write the template alone, under a generic project name.</summary>
        Standalone,

        /// <summary>A Flowline project — name the project after its solution and register it in the solution file.</summary>
        Project
    }

    /// <summary>The resolved mode and the folder the scaffold lands in.</summary>
    /// <param name="Mode">Which shape to write.</param>
    /// <param name="Folder">
    /// The project root for <see cref="ScaffoldMode.Project"/>, or the working directory for
    /// <see cref="ScaffoldMode.Standalone"/>. Project mode resolves upward, so a run from a subdirectory
    /// writes into the project rather than beside the caller.
    /// </param>
    internal readonly record struct ScaffoldTarget(ScaffoldMode Mode, string Folder);

    /// <summary>The values <c>&lt;part&gt;</c> accepts. One today; the argument shape absorbs more without changing.</summary>
    static readonly string[] s_parts = ["webresources"];

    /// <summary>The project file name standalone mode writes.</summary>
    /// <remarks>
    /// Generic, because standalone has no solution to name it after. Project mode uses
    /// <see cref="ProjectScaffolder.WebResourcesProjectFileName"/> instead — that name is what reaches
    /// Dataverse, and only a project has one.
    ///
    /// Aliases the scaffolder's own constant rather than repeating the literal: that resolver has to
    /// recognise this exact name to stop <c>clone</c>/<c>init</c> overwriting a stand-alone folder, so two
    /// copies drifting apart would silently reopen that hole.
    /// </remarks>
    internal const string StandaloneProjectFileName = ProjectScaffolder.StandaloneWebResourcesProjectFileName;

    /// <summary>Where the WebResources project lives relative to the scaffold target.</summary>
    internal const string WebResourcesFolderName = "WebResources";

    protected override bool RequiresProject => false;

    // Standalone mode runs in a folder that is not a Flowline project at all, so a welcome banner would
    // introduce a tool the user may be meeting for the first time by way of a folder it has not written yet.
    protected override bool ShowWelcome => false;

    /// <summary>Skips the standard git/dotnet/pac probe entirely.</summary>
    /// <remarks>
    /// The base implementation requires a git repository, requires the PAC CLI, and calls NuGet for the
    /// update notice before the command body runs. This command writes local template files and never
    /// reaches Dataverse, so every one of those is a false prerequisite — and the NuGet call would break
    /// the promise that scaffolding works with no network at all. <c>SlnAddCommand</c> skips the same probe
    /// for the same reason.
    /// </remarks>
    protected override Task CheckSetupAsync(Settings settings, CancellationToken cancellationToken) => Task.CompletedTask;

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ValidatePart(settings.Part);

        var target = ResolveTarget(Directory.GetCurrentDirectory());
        AnnounceMode(target);

        return target.Mode == ScaffoldMode.Standalone
            ? await ScaffoldStandaloneAsync(target.Folder, cancellationToken)
            : await ScaffoldIntoProjectAsync(target.Folder, Config?.Solution?.UniqueName, cancellationToken);
    }

    /// <summary>Writes the template under the solution's name and registers it in the solution file.</summary>
    /// <remarks>
    /// Hands the work to <see cref="ProjectScaffolder.SetupWebResourcesProjectAsync"/> — the same call
    /// <c>clone</c> and <c>init</c> make — so a project scaffolded here is indistinguishable from one they
    /// produced. The registration check runs here rather than being left to that method so the command can
    /// tell an already-there run from a fresh one and close with the right line: a finish line after a skip
    /// would claim work that did not happen.
    ///
    /// Takes <paramref name="solutionName"/> rather than reading <c>Config</c>, and is <c>internal</c>, so the
    /// whole project-mode path is exercisable against a temp fixture without running the base command
    /// pipeline — the same reason <see cref="ScaffoldStandaloneAsync"/> takes its folder.
    /// </remarks>
    internal async Task<int> ScaffoldIntoProjectAsync(string projectRoot, string? solutionName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(solutionName))
            throw new FlowlineException(ExitCode.ConfigInvalid,
                $"{ProjectConfig.s_configFileName} is here but names no solution, and the project is named after it. Run 'flowline clone' to finish setting this project up.");

        var layout = await SolutionFileLayout.LoadAsync(projectRoot, cancellationToken);
        var projectFileName = ProjectScaffolder.WebResourcesProjectFileName(solutionName);
        var webresourcesFolder = Path.Combine(projectRoot, WebResourcesFolderName);

        if (ProjectScaffolder.WebResourcesProjectAlreadyRegistered(Path.Combine(webresourcesFolder, projectFileName), layout))
        {
            if (ProjectScaffolder.IsStandaloneScaffold(webresourcesFolder, solutionName))
                Console.Warning(ProjectScaffolder.DescribeStandaloneScaffold(solutionName));
            else
                Console.Skip("WebResources project already there — skipping");

            return (int)ExitCode.Success;
        }

        EnsureNoTemplateCollision(webresourcesFolder, projectFileName);

        await projectScaffolder.SetupWebResourcesProjectAsync(projectRoot, layout.SolutionFilePath, solutionName, layout, cancellationToken);

        Console.Done("Scaffolded! Build it, then 'flowline push' it to Dataverse.");
        return (int)ExitCode.Success;
    }

    /// <summary>Writes the template alone, under a generic project name, and names what to run next.</summary>
    /// <remarks>
    /// Takes the folder rather than reading <c>RootFolder</c> so the whole standalone path is exercisable
    /// against a temp directory without running the base command pipeline — the same reason
    /// <see cref="ResolveTarget"/> and <see cref="ValidatePart"/> are static.
    /// </remarks>
    internal async Task<int> ScaffoldStandaloneAsync(string folder, CancellationToken cancellationToken)
    {
        var webresourcesFolder = Path.Combine(folder, WebResourcesFolderName);

        if (File.Exists(Path.Combine(webresourcesFolder, StandaloneProjectFileName)))
        {
            Console.Skip("WebResources project already there — skipping");
            return (int)ExitCode.Success;
        }

        EnsureNoTemplateCollision(webresourcesFolder, StandaloneProjectFileName);

        await ProjectScaffolder.WriteWebResourcesTemplateAsync(webresourcesFolder, StandaloneProjectFileName, cancellationToken);
        Console.Ok($"{WebResourcesFolderName} project ready");

        PrintNextSteps();
        Console.Done("Scaffolded! Build it, then push it to Dataverse.");
        return (int)ExitCode.Success;
    }

    /// <summary>Refuses to write over a file the template would land on.</summary>
    /// <remarks>
    /// The already-there check above only sees the project file. A folder holding template-named files
    /// <em>without</em> one — someone else's <c>package.json</c>, a half-finished experiment — sails past it,
    /// and <c>TemplateWriter</c> truncates rather than skipping, so writing would destroy whatever was there.
    /// Checked against <see cref="ProjectScaffolder.WebResourcesTemplateRelativePaths"/> rather than a local
    /// copy of the list, and checked before the first write so a refusal leaves nothing half-written.
    ///
    /// There is deliberately no <c>--force</c> to write over it: the command's whole job is to create
    /// something that is not there yet, and a folder with a conflicting file is a different situation the
    /// user should look at rather than overrule.
    /// </remarks>
    internal static void EnsureNoTemplateCollision(string webresourcesFolder, string projectFileName)
    {
        var collision = ProjectScaffolder.WebResourcesTemplateRelativePaths(projectFileName)
                                         .FirstOrDefault(relative => File.Exists(Path.Combine(webresourcesFolder, relative)));

        if (collision is null) return;

        throw new FlowlineException(ExitCode.WriteTargetOccupied,
            $"{Path.Combine(WebResourcesFolderName, collision)} is already here and scaffold won't write over it. " +
            $"If an earlier scaffold was interrupted, delete {WebResourcesFolderName} and run this again — otherwise move the file aside, or scaffold somewhere else.");
    }

    /// <summary>Names the commands that carry a standalone scaffold through to a pushed web resource.</summary>
    /// <remarks>
    /// The push step needs two things this folder does not have and cannot invent: a Dataverse solution to
    /// push into, and a PAC auth profile for the target environment — <c>ProfileResolutionService</c> fails
    /// the run when no profile matches the URL, whatever <c>--dev</c> says. Naming both here is the
    /// difference between a next step that runs and one that stops on its first line.
    /// </remarks>
    void PrintNextSteps()
    {
        Console.Info($"Build it: cd {WebResourcesFolderName} && npm install && npm run build");
        Console.Info("Authenticate, if you haven't: pac auth create --environment <url>");
        Console.Info($"Push it: flowline push <solution> --webresources ./{WebResourcesFolderName}/dist --dev <url>");
    }

    /// <summary>Refuses a part this command cannot write.</summary>
    /// <remarks>
    /// The message lists what is accepted rather than only naming what was rejected, because that error is
    /// how an agent reading a failed run discovers the vocabulary. <see cref="ExitCode.ValidationFailed"/>
    /// rather than <see cref="ExitCode.NotFound"/>: the value is malformed input, not a missing resource.
    /// </remarks>
    internal static void ValidatePart(string part)
    {
        if (s_parts.Contains(part, StringComparer.OrdinalIgnoreCase)) return;

        throw new FlowlineException(ExitCode.ValidationFailed,
            $"'{part}' isn't something scaffold can write — pass one of: {string.Join(", ", s_parts)}.");
    }

    /// <summary>Decides which mode <paramref name="startFolder"/> gets, without writing or reading config.</summary>
    /// <remarks>
    /// Two markers, and they are not symmetric. <c>.flowline</c> is what makes a folder a Flowline project —
    /// it is the marker <see cref="FlowlineCommand{TSettings}.FindProjectRoot"/> already walks upward to
    /// find — so its absence means standalone even when a solution file happens to sit alongside. Its
    /// presence without a solution file is a half-configured project rather than a scaffolding decision:
    /// project mode needs the solution name from the former and a registration target in the latter, so
    /// there is nothing to fall back to. Failing there beats silently writing a generically-named project
    /// into a repo that expected a solution-named one.
    ///
    /// Static and side-effect free so every branch is testable without constructing the command or a console.
    /// </remarks>
    /// <exception cref="FlowlineException">
    /// <see cref="ExitCode.NotFound"/> when a <c>.flowline</c> is found but the project holds no solution file.
    /// </exception>
    internal static ScaffoldTarget ResolveTarget(string startFolder)
    {
        var projectRoot = FindProjectRoot(startFolder);
        if (projectRoot is null)
            return new ScaffoldTarget(ScaffoldMode.Standalone, startFolder);

        if (new MsBuildSolutionReader().FindSolutionFile(projectRoot) is null)
            throw new FlowlineException(ExitCode.NotFound,
                $"{ProjectConfig.s_configFileName} is here but no .sln or .slnx is — scaffold registers the project in the solution file, so it needs one. Run 'flowline clone' to finish setting this project up.");

        return new ScaffoldTarget(ScaffoldMode.Project, projectRoot);
    }

    /// <summary>Says which mode was resolved, before anything is written.</summary>
    /// <remarks>
    /// The two modes produce different project names and only one of them touches the solution file, and the
    /// project marker is found by walking <em>upward</em> — so a run from a subdirectory can land in project
    /// mode when the user was looking at an empty folder. Announcing the resolution is what makes that
    /// visible at the moment it still matters, rather than after the files are on disk.
    /// </remarks>
    void AnnounceMode(ScaffoldTarget target)
    {
        if (target.Mode == ScaffoldMode.Standalone)
        {
            Console.Ok("Standalone — no Flowline project here");
            return;
        }

        Console.Ok($"Flowline project: {ConsolePath.FormatRelativePath(target.Folder)}");
    }
}
