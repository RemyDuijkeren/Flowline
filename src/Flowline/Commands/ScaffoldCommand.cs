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
/// <b>One path, not two modes.</b> This command used to branch on <c>.flowline</c>: a project got a
/// solution-named project file, anything else got a generic one. That branch bought a filename and nothing
/// else — the WebResources project is <c>Microsoft.Build.NoTargets</c>, so no name escapes the repo, and
/// <see cref="WebResourcesProjectResolver"/> identifies it by content signals rather than by name. So the
/// command now asks one question instead: is there a solution file here? If there is, the project is named
/// after it and added to it; if there isn't, the template lands alone. Config is never read for the
/// decision.
/// </remarks>
public class ScaffoldCommand(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService, ILoggerFactory loggerFactory, SubprocessCapture capture, ProjectScaffolder projectScaffolder, NuGetVersionClient nuGetVersionClient)
    : FlowlineCommand<ScaffoldCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture, nuGetVersionClient)
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "<part>")]
        [Description("What to scaffold. Only 'webresources' today")]
        public string Part { get; set; } = null!;

        [CommandOption("-o|--output <PATH>")]
        [Description("Scaffold into this folder instead of the current one — the solution file is looked up there too. Created when missing.")]
        public string? Output { get; set; }

        [CommandOption("--name <NAME>")]
        [Description("Name the project folder and its .csproj (default: 'WebResources', with the project file named after the solution file when there is one)")]
        public string? Name { get; set; }
    }

    /// <summary>The values <c>&lt;part&gt;</c> accepts. One today; the argument shape absorbs more without changing.</summary>
    static readonly string[] s_parts = ["webresources"];

    /// <summary>The project file name written when no solution file names it.</summary>
    /// <remarks>
    /// Aliases the scaffolder's own constant rather than repeating the literal: that resolver has to
    /// recognise this exact name to stop <c>clone</c>/<c>init</c> overwriting a folder scaffolded without a
    /// solution file, so two copies drifting apart would silently reopen that hole.
    /// </remarks>
    internal const string StandaloneProjectFileName = ProjectScaffolder.StandaloneWebResourcesProjectFileName;

    /// <summary>The folder the project lands in when <c>--name</c> does not name another.</summary>
    internal const string WebResourcesFolderName = "WebResources";

    protected override bool RequiresFlowlineProject => false;

    // The scaffold target is not necessarily a Flowline project, so a welcome banner would introduce a tool
    // the user may be meeting for the first time by way of a folder it has not written yet.
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

    protected override Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ValidatePart(settings.Part);
        ValidateName(settings.Name);

        // --output is just "the folder I am standing in", so it needs no special case: the project is
        // written there either way, and the solution-file search starts there either way.
        return ScaffoldWebResourcesAsync(ResolveTarget(ResolveRoot(settings.Output)), settings.Name, cancellationToken);
    }

    /// <summary>The folder the scaffold lands in, and the solution file it is added to if there is one.</summary>
    internal readonly record struct ScaffoldTarget(string Folder, string? SolutionFilePath);

    /// <summary>Writes the template into <paramref name="target"/>, adding it to the solution file when it has one.</summary>
    /// <remarks>
    /// Takes the resolved target and the name rather than reading <c>Settings</c> or <c>Config</c>, and is
    /// <c>internal</c>, so the whole path is exercisable against a temp fixture without running the base
    /// command pipeline.
    /// </remarks>
    internal async Task<int> ScaffoldWebResourcesAsync(ScaffoldTarget target, string? name, CancellationToken cancellationToken)
    {
        var (root, solutionFilePath) = target;
        var (folderName, projectFileName) = ResolveNames(name, solutionFilePath);

        var webresourcesFolder = Path.Combine(root, folderName);
        var projectPath = Path.Combine(webresourcesFolder, projectFileName);

        if (await AlreadyScaffoldedAsync(root, projectPath, solutionFilePath, name, cancellationToken))
            return (int)ExitCode.Success;

        EnsureNoTemplateCollision(webresourcesFolder, projectFileName);

        await projectScaffolder.ScaffoldWebResourcesProjectAsync(webresourcesFolder, projectFileName, solutionFilePath, cancellationToken);

        ReportSolutionFileEntry(projectFileName, solutionFilePath);

        Console.Done("Scaffolded! Use 'push' to send them to Dataverse. ᕦ(ò_óˇ)ᕤ");
        return (int)ExitCode.Success;
    }

    /// <summary>Says whether the project reached the solution file, without needing <c>--verbose</c>.</summary>
    /// <remarks>
    /// Being in the solution file is the difference between a project Flowline can find and a folder it
    /// cannot: every command after this one locates the WebResources project through it. A run that wrote
    /// the template but added nothing looks identical to one that did both unless it says so, and it is the
    /// case the user has to act on.
    ///
    /// Deliberately not worded as "registered" — <c>CONCEPTS.md</c> gives that word to plugin step
    /// registration in Dataverse, and reusing it for a line in a local XML file would put two different acts
    /// under one term. <c>sln add</c> already says "added to"; this matches it.
    /// </remarks>
    void ReportSolutionFileEntry(string projectFileName, string? solutionFilePath)
    {
        if (solutionFilePath is not null)
        {
            Console.Ok($"{projectFileName} added to {Path.GetFileName(solutionFilePath)}");
            return;
        }

        // A skip, not a warning: nothing went wrong, one optional step had no target. Kept short on purpose
        // — 'push --webresources' pushes the built folder without a solution file, so this is not a dead end.
        Console.Skip("No solution file found to add project to — skipped");
    }

    /// <summary>Reports an existing WebResources project and leaves it alone.</summary>
    /// <remarks>
    /// With a solution file, "already there" means <em>recorded anywhere in it</em>, not just sitting at the
    /// default path — a project legitimately moved or renamed still counts, so a second copy is never
    /// scaffolded beside it. <see cref="SolutionFileLayout.WebResourcesProjectPath"/> throws on a genuine
    /// tie between two candidates and that is left to propagate: scaffolding a third project on top of an
    /// unresolved ambiguity would only make it worse.
    ///
    /// Without one there is nothing to consult but the target path itself.
    /// </remarks>
    async Task<bool> AlreadyScaffoldedAsync(string root, string projectPath, string? solutionFilePath, string? name, CancellationToken cancellationToken)
    {
        // --name names one specific project, so that project's own path is what "already there" means. The
        // default WebResources folder is not consulted: the user asked for this one.
        if (name is not null)
        {
            if (File.Exists(projectPath))
            {
                Console.Skip($"{Path.GetFileName(projectPath)} already there — skipping");
                return true;
            }

            if (solutionFilePath is not null)
                EnsureNoSecondWebResourcesProject(root, await LoadLayoutAsync(solutionFilePath, cancellationToken));

            return false;
        }

        if (solutionFilePath is null)
        {
            if (!File.Exists(projectPath)) return false;

            Console.Skip("WebResources project already there — skipping");
            return true;
        }

        var layout = await LoadLayoutAsync(solutionFilePath, cancellationToken);
        if (!ProjectScaffolder.WebResourcesProjectAlreadyRegistered(projectPath, layout)) return false;

        // A generically-named project where the solution-named one would go was scaffolded before this repo
        // had a solution file, and nothing has added it to one. Saying so beats a dim skip line that reads as
        // "handled". Not reported when --name was given: the user named the project, so the solution-derived
        // name it "should" have had is not the name they asked for.
        var solutionName = Path.GetFileNameWithoutExtension(solutionFilePath);
        if (name is null && ProjectScaffolder.IsStandaloneScaffold(Path.GetDirectoryName(projectPath)!, solutionName))
            Console.Warning(ProjectScaffolder.DescribeStandaloneScaffold(solutionName));
        else
            Console.Skip("WebResources project already there — skipping");

        return true;
    }

    /// <summary>Reads the layout of the solution file that was found, wherever it sits.</summary>
    /// <remarks>
    /// Keyed on the solution file's own folder, not the folder the scaffold lands in: the walk means those
    /// two are routinely different, and <see cref="SolutionFileLayout.LoadAsync"/> looks for a solution file
    /// in whatever folder it is handed.
    /// </remarks>
    static Task<SolutionFileLayout> LoadLayoutAsync(string solutionFilePath, CancellationToken cancellationToken) =>
        SolutionFileLayout.LoadAsync(Path.GetDirectoryName(solutionFilePath)!, cancellationToken);

    /// <summary>The folder a run starts from: <c>--output</c>, or the working directory.</summary>
    /// <remarks>
    /// The folder is not created here — the template writer creates the whole chain, so a run that stops on
    /// a validation failure leaves no empty folder behind.
    /// </remarks>
    /// <exception cref="FlowlineException">
    /// <see cref="ExitCode.WriteTargetOccupied"/> when the path names an existing file. Nothing is missing
    /// or malformed there; a file is standing where the scaffold folder would go, and the raw
    /// <c>IOException</c> the writer would otherwise throw carries no exit code an agent can branch on.
    /// </exception>
    internal static string ResolveRoot(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return Directory.GetCurrentDirectory();

        var root = Path.GetFullPath(output);

        if (File.Exists(root))
            throw new FlowlineException(ExitCode.WriteTargetOccupied,
                $"'{output}' is a file, and --output names the folder to scaffold into. Point it at a folder, or at a path that doesn't exist yet.");

        return root;
    }

    /// <summary>Where the project is written, and the solution file it is added to.</summary>
    /// <remarks>
    /// <b>The project is written where you are standing.</b> Not beside the solution file: `scaffold` writes
    /// into the folder you pointed it at, and a command that silently puts files two levels up from the
    /// prompt is the surprise this design exists to avoid. The solution file only supplies the project's
    /// name and takes its entry — it does not move the scaffold.
    ///
    /// <b>The search for it walks up</b>, bounded by <c>.flowline</c> or <c>.git</c> and never past them —
    /// see <see cref="MsBuildSolutionReader.FindSolutionFileUpward"/>, which <c>sln add</c> shares, so both
    /// commands answer "where is my solution file" the same way. <c>.flowline</c> is used as a marker on
    /// disk; nothing inside it is read.
    ///
    /// Static and side-effect free, so every branch is testable without constructing the command.
    /// </remarks>
    internal static ScaffoldTarget ResolveTarget(string startFolder) =>
        new(startFolder, new MsBuildSolutionReader().FindSolutionFileUpward(startFolder, ProjectConfig.s_configFileName));

    /// <summary>Picks the folder and project file name, in that order of preference: <c>--name</c>, the
    /// solution file's own name, the generic fallback.</summary>
    /// <remarks>
    /// Naming the project after the solution <em>file</em> rather than after <c>.flowline</c>'s unique name
    /// is what removes the config read, and it is also closer to right: a repo that reused an existing
    /// <c>.sln</c> (an spkl migration) can have a solution file whose name disagrees with the Dataverse
    /// unique name, and the project sits in that file.
    ///
    /// The prefix is cosmetic either way — see <see cref="ProjectScaffolder.WebResourcesProjectFileName"/> —
    /// so <c>--name</c> overriding it costs nothing. It names the folder too, because a project file and the
    /// folder around it disagreeing is the kind of thing someone has to decode later.
    /// </remarks>
    internal static (string FolderName, string ProjectFileName) ResolveNames(string? name, string? solutionFilePath)
    {
        if (!string.IsNullOrWhiteSpace(name))
            return (name, $"{name}.csproj");

        return (WebResourcesFolderName,
                solutionFilePath is null
                    ? StandaloneProjectFileName
                    : ProjectScaffolder.WebResourcesProjectFileName(Path.GetFileNameWithoutExtension(solutionFilePath)));
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

        // The folder name is read back off the target rather than assumed, so the recovery names the folder
        // --name actually produced instead of the default one.
        var folderName = Path.GetFileName(webresourcesFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        throw new FlowlineException(ExitCode.WriteTargetOccupied,
            $"{Path.Combine(folderName, collision)} is already here and scaffold won't write over it. " +
            $"If an earlier scaffold was interrupted, delete {folderName} and run this again — otherwise move the file aside, or scaffold somewhere else.");
    }

    /// <summary>Refuses to add a second WebResources project to a solution that already has one.</summary>
    /// <remarks>
    /// Only reachable through <c>--name</c>, which is the one way to ask for a project the default path
    /// check would not see. Flowline resolves <b>one</b> WebResources project per solution: two of them in one solution file
    /// candidates either tie, and <see cref="WebResourcesProjectResolver"/> throws rather than picking, or
    /// score differently and the loser is silently never pushed. The second is the worse outcome, because
    /// nothing reports it — so this refuses at the point where nothing has been written yet.
    ///
    /// <see cref="ExitCode.ValidationFailed"/>, matching the other <c>--name</c> refusal: both are "this
    /// name won't work here", decided before anything is read or written. Not
    /// <see cref="ExitCode.ConfigInvalid"/> — nothing on disk is missing or malformed, which is what that
    /// code means, and two different codes for the two <c>--name</c> refusals is what an agent trips on.
    /// </remarks>
    static void EnsureNoSecondWebResourcesProject(string root, SolutionFileLayout layout)
    {
        if (layout.WebResourcesProjectPath is not { } existing) return;

        throw new FlowlineException(ExitCode.ValidationFailed,
            $"{ConsolePath.FormatRelativePath(existing, root, markup: false)} is already this solution's WebResources project, and Flowline resolves one per solution — a second would leave one of them silently never pushed. " +
            "Remove or rename that one first, or scaffold into a different solution with --output.");
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

    /// <summary>Refuses a <c>--name</c> that would produce a project Flowline cannot use.</summary>
    /// <remarks>
    /// The Test/Tests rule is not style. <c>WebResourcesProjectResolver.IsTestProject</c> eliminates any
    /// project whose file name ends that way before scoring, so a project named there would resolve to "no
    /// WebResources project" and every later <c>push</c> would skip web resources with a warning instead of
    /// failing. Rejecting the name at creation is the only point where that is still cheap to fix.
    ///
    /// The character check is what keeps <c>--name</c> a name: a path there would put the project somewhere
    /// <c>--output</c> already covers, with two flags able to disagree about where the scaffold lands.
    /// </remarks>
    internal static void ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        if (name.EndsWith("Test", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase))
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"'{name}' ends in Test, and Flowline reads a project named that way as a test project — push would skip its web resources every run. Pick a name that doesn't.");

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"'{name}' isn't a usable folder name — --name names one folder inside the scaffold target, not a path. Use --output to scaffold somewhere else.");
    }
}
