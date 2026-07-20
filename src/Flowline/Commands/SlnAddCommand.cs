using System.ComponentModel;
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
/// <c>flowline sln add &lt;path&gt;</c> — wires a <c>.cdsproj</c> into the project's solution file.
/// </summary>
/// <remarks>
/// Exists because <c>dotnet sln add</c> refuses a <c>.cdsproj</c> and exits 0 while doing so
/// (https://github.com/dotnet/sdk/issues/47638). Everything it touches is a local file, so it runs
/// standalone — a repo migrating off spkl with nothing but a <c>Package/Package.cdsproj</c> is exactly
/// the case it exists for, and that repo has no <c>.flowline</c>, no git history worth probing, and
/// possibly no pac install.
/// </remarks>
public class SlnAddCommand(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService, ILoggerFactory loggerFactory, SubprocessCapture capture)
    : FlowlineCommand<SlnAddCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture)
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "<path>")]
        [Description("Path to the .cdsproj to add")]
        public string ProjectPath { get; set; } = null!;
    }

    readonly MsBuildSolutionReader _reader = new();
    readonly MsBuildSolutionWriter _writer = new();

    /// <summary>What the command did, once the work is done.</summary>
    internal enum Outcome
    {
        /// <summary>The entry was written.</summary>
        Added,

        /// <summary>The solution file already referenced the project; nothing was written.</summary>
        AlreadyPresent
    }

    protected override bool RequiresProject => false;

    // A leftover .sln next to a .slnx is what `dotnet sln migrate` leaves behind, and the welcome
    // screen would push the one line that tells the user to delete it further from the work it
    // describes. Nothing here needs an introduction either — it edits one local file.
    protected override bool ShowWelcome => false;

    /// <summary>Skips the standard git/dotnet/pac probe entirely.</summary>
    /// <remarks>
    /// The base implementation shells out to four tools before the command body runs. This command
    /// reads and writes one local XML file and must work in a repo that uses no other Flowline feature,
    /// so paying that probe would be both slow and a false prerequisite — a missing pac install would
    /// fail a command that never calls pac.
    /// </remarks>
    protected override Task CheckSetupAsync(Settings settings, CancellationToken cancellationToken) => Task.CompletedTask;

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var projectFullPath = Path.GetFullPath(settings.ProjectPath);
        ValidateExtension(settings.ProjectPath);
        ValidateProjectExists(projectFullPath, settings.ProjectPath);

        var solutionFolder = RootFolder;
        var result = await AddAsync(_reader, _writer, projectFullPath, solutionFolder, cancellationToken);

        var project = ConsolePath.FormatRelativePath(projectFullPath, solutionFolder);
        var solution = ConsolePath.FormatRelativePath(result.SolutionFilePath, solutionFolder);
        var outcome = result.Outcome;

        // Announced after the write, not before it — the file only exists once the writer succeeded.
        if (result.CreatedSolutionFile)
            Console.Ok($"Solution file {solution} created");

        if (outcome == Outcome.Added)
            Console.Ok($"{project} added to {solution}");
        else
            Console.Skip($"{project} is already in {solution} — skipping");

        WarnAboutLeftoverSln(solutionFolder, result.SolutionFilePath);

        if (outcome == Outcome.Added)
            Console.Done("Wired up! Run 'dotnet build' to check it.");

        return SelectExitCode(outcome);
    }

    /// <summary>What <see cref="AddAsync"/> did.</summary>
    /// <param name="SolutionFilePath">The solution file that was written to, existing or newly created.</param>
    /// <param name="CreatedSolutionFile"><c>true</c> when there was no solution file and one was made.</param>
    /// <param name="Outcome">Whether an entry was written or the project was already referenced.</param>
    internal readonly record struct AddResult(string SolutionFilePath, bool CreatedSolutionFile, Outcome Outcome);

    /// <summary>
    /// Locates (or names) the solution file and writes the entry. All of the command's decisions, none
    /// of its output.
    /// </summary>
    /// <remarks>
    /// Static and dependency-free apart from the two services it is handed, so the whole resolve-and-write
    /// path is exercisable against a real temp directory. Splitting it out is what keeps the tests off a
    /// re-implementation of the same composition, which is exactly where a wiring bug would hide.
    /// </remarks>
    internal static async Task<AddResult> AddAsync(
        MsBuildSolutionReader reader,
        MsBuildSolutionWriter writer,
        string projectFullPath,
        string solutionFolder,
        CancellationToken cancellationToken = default)
    {
        var existing = reader.FindSolutionFile(solutionFolder);
        var solutionFilePath = existing ?? Path.Combine(solutionFolder, DefaultSolutionFileName(solutionFolder));

        var entryPath = ToSolutionRelativePath(projectFullPath, solutionFolder);
        var written = await writer.AddProjectAsync(solutionFilePath, entryPath, cancellationToken);

        return new AddResult(solutionFilePath, existing is null, written ? Outcome.Added : Outcome.AlreadyPresent);
    }

    /// <summary>Refuses anything that is not a <c>.cdsproj</c>.</summary>
    /// <remarks>
    /// A <c>.csproj</c> gets its own message because <c>dotnet sln add</c> handles that case perfectly
    /// well — Flowline covers what the SDK cannot and stays out of the way otherwise, so pointing at
    /// the first-party tool is the whole answer rather than a consolation.
    /// </remarks>
    internal static void ValidateExtension(string projectPath)
    {
        var extension = Path.GetExtension(projectPath);

        if (string.Equals(extension, ".cdsproj", StringComparison.OrdinalIgnoreCase)) return;

        if (string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase))
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"'{Path.GetFileName(projectPath)}' is a C# project — use 'dotnet sln add' for those. 'sln add' is for .cdsproj, which the SDK won't take.");

        throw new FlowlineException(ExitCode.ValidationFailed,
            $"'{Path.GetFileName(projectPath)}' isn't a .cdsproj — pass the path to your Dataverse package project.");
    }

    /// <summary>Fails before anything is written when the project file isn't on disk.</summary>
    internal static void ValidateProjectExists(string projectFullPath, string projectPathAsGiven)
    {
        if (File.Exists(projectFullPath)) return;

        throw new FlowlineException(ExitCode.NotFound,
            $"No project at '{projectPathAsGiven}' — check the path.");
    }

    /// <summary>The name given to a solution file this command has to create itself.</summary>
    /// <remarks>
    /// The folder name, not the Dataverse solution's unique name: this command runs where there may be
    /// no <c>.flowline</c> to read that from, and <c>dotnet new sln</c> defaults to the folder name for
    /// the same reason. <c>.slnx</c> is the .NET 10 default and holds a <c>.cdsproj</c> fine — a fresh
    /// file has no downstream consumer pinned to an older SDK, so nothing argues for <c>.sln</c> here.
    /// </remarks>
    internal static string DefaultSolutionFileName(string solutionFolder) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(solutionFolder)) + ".slnx";

    /// <summary>Rewrites the user's path into one relative to the folder holding the solution file.</summary>
    /// <remarks>
    /// The argument is relative to wherever the user is standing; the solution entry has to be relative
    /// to the solution file. Running <c>flowline sln add Package.cdsproj</c> from inside <c>Package/</c>
    /// must record <c>Package\Package.cdsproj</c>, not <c>Package.cdsproj</c>.
    ///
    /// Separator normalization is left to the writer, which does it on every path it is handed —
    /// duplicating it here would just be a second place to keep in step.
    /// </remarks>
    internal static string ToSolutionRelativePath(string projectFullPath, string solutionFolder) =>
        Path.GetRelativePath(solutionFolder, projectFullPath);

    /// <summary>Tells the user to delete the <c>.sln</c> that <c>dotnet sln migrate</c> left behind.</summary>
    /// <remarks>
    /// Worth saying out loud even though the command handled it: a bare <c>dotnet build</c> in a folder
    /// holding both files fails with MSB1011, so the user hits this the moment they build — after this
    /// command has already reported success.
    /// </remarks>
    void WarnAboutLeftoverSln(string solutionFolder, string solutionFilePath)
    {
        if (!_reader.HasCoexistingSolutionFiles(solutionFolder)) return;

        var leftover = Path.ChangeExtension(solutionFilePath, ".sln");
        Console.Warning($"{Path.GetFileName(leftover)} is still here next to the .slnx — delete it, or 'dotnet build' won't know which one you mean.");
    }

    /// <summary>
    /// Maps the outcome to a process exit code.
    /// </summary>
    /// <remarks>
    /// Both outcomes succeed, and that is the point rather than an oversight. Re-adding a project the
    /// solution already has is the desired end state reached without work, and the leftover-<c>.sln</c>
    /// case is what a supported <c>dotnet sln migrate</c> produces — neither is a failure a script
    /// should branch on. Failures leave through <see cref="FlowlineException"/> instead.
    /// </remarks>
    internal static int SelectExitCode(Outcome outcome) => outcome switch
    {
        Outcome.Added          => (int)ExitCode.Success,
        Outcome.AlreadyPresent => (int)ExitCode.Success,
        _                      => (int)ExitCode.GeneralError
    };
}
