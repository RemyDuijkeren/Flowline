using System.ComponentModel;
using System.Xml.Linq;
using Flowline.Core;
using Flowline.Core.Configure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

/// <summary>
/// Options every <c>settings</c> operation that touches an environment carries.
/// </summary>
/// <remarks>
/// The environment positional is deliberately not here (KTD1). <c>push</c> and the five component kinds
/// require it, <c>pull</c> makes it optional for the all-roles sweep, and one
/// <see cref="CommandArgumentAttribute"/> cannot be both — so each leaf declares its own at position 0 and
/// only the shared options live on the base.
/// </remarks>
public abstract class SettingsSettings : DataverseSettings
{
    [CommandOption("--solution-name <name>")]
    [Description("Solution unique name — required outside a Flowline project, rejected inside one")]
    public string? SolutionName { get; set; }

    [CommandOption("--dry-run")]
    [Description("Report what would change and write nothing")]
    [DefaultValue(false)]
    public bool DryRun { get; set; } = false;
}

/// <summary>Pure helpers shared by the <c>settings</c> operations, testable without a checkout.</summary>
public static class SettingsSupport
{
    /// <summary>
    /// Stand-alone is a flag naming the solution plus no project to read one from (KTD3).
    /// </summary>
    /// <remarks>
    /// Same shape as <see cref="DeployCommand.ResolveStandalone"/> — a flag plus the absence of a project —
    /// rather than push's flag-only-then-throw form, so the two precedents do not diverge further. Pure so
    /// the rule is testable without a checkout.
    ///
    /// Either flag qualifies: <c>--solution-name</c> states the name outright, and <c>--from &lt;zip|folder&gt;</c>
    /// names an artifact that carries it. Without this, a stand-alone capture with only the artifact would be
    /// judged project mode and stop at "No Flowline project found" — the artifact it was handed ignored.
    /// </remarks>
    public static bool ResolveStandalone(string? solutionName, string? artifactPath, string startDir) =>
        (!string.IsNullOrWhiteSpace(solutionName) || !string.IsNullOrWhiteSpace(artifactPath))
        && FlowlineCommand<FlowlineSettings>.FindFlowlineProjectRoot(startDir) is null;

    /// <summary>Rejects the one flag pair that cannot mean anything, naming both flags.</summary>
    /// <remarks>
    /// The <c>--pull</c> with <c>--settings-file</c> check the old single leaf carried is gone: the grammar
    /// makes that pair unreachable rather than invalid, and <c>--settings-file</c> now names the destination
    /// on a capture (KTD16). What remains is a flag that only means something outside a project.
    /// </remarks>
    public static string? ValidateFlags(bool standalone, string? solutionName, bool projectFound)
    {
        if (!standalone && !string.IsNullOrWhiteSpace(solutionName) && projectFound)
            return "--solution-name only applies outside a Flowline project. Inside one the solution comes from the project — remove the flag.";

        return null;
    }

    /// <summary>The message a role keyword earns when there is no project to resolve it from.</summary>
    public static string BuildStandaloneRoleError(string target) =>
        $"'{target}' is a role name, and roles resolve from .flowline — there's no Flowline project here. " +
        "Pass the environment URL instead.";

    /// <summary>Names the file a run resolved, before anything is written.</summary>
    /// <remarks>
    /// An input verdict in the same shape as the environment and solution lines above it, not an
    /// announcement of the work — the tone guide's one-line-per-resolved-input act. With no confirmation
    /// prompt, this line plus the environment line is what an operator has to catch a run pointed at the
    /// wrong file or the wrong environment before it writes.
    /// </remarks>
    public static string BuildResolutionNote(SettingsFileLocation location) =>
        $"Settings: [bold]{Markup.Escape(Path.GetFileName(location.Path))}[/] ({SourceLabel(location.Source)})";

    static string SourceLabel(SettingsFileSource source) => source switch
    {
        SettingsFileSource.Explicit => "--settings-file",
        SettingsFileSource.RoleConvention => "role convention",
        _ => "shared fallback",
    };

    /// <summary>Dry-run completion wording, in the statement form the other commands use.</summary>
    public static string BuildDryRunCompleteMessage(string environment) =>
        $"Dry run complete — {Markup.Escape(environment)} is untouched. Run without --dry-run to apply.";

    /// <summary>The finish line for a real apply.</summary>
    public static string BuildAppliedMessage(string environment) =>
        $"{Markup.Escape(environment)} is configured. Re-run any time — the same file changes nothing twice.";

    /// <summary>The finish line for a run that was interrupted partway through the file.</summary>
    /// <remarks>
    /// It has to say two things an operator acts on: the components listed above are the ones that were
    /// written, and the rest of the file was never reached. Re-running is the fix, and a re-run is safe.
    /// </remarks>
    public static string BuildCancelledMessage(string environment) =>
        $"Stopped early — the components above were applied to {Markup.Escape(environment)}, the rest weren't. " +
        "Re-run to finish.";

    /// <summary>How many solution components this file says nothing about.</summary>
    public static string BuildUndeclaredWarning(int count) =>
        count == 1
            ? "1 component in this solution isn't in the file — run with --verbose to see it."
            : $"{count} components in this solution aren't in the file — run with --verbose to list them.";

    /// <summary>A capture's dry run leaves a file unwritten, not an environment untouched.</summary>
    public static string BuildPullDryRunMessage(string path) =>
        $"Dry run complete — {Markup.Escape(path)} wasn't written. Run without --dry-run to write it.";

    /// <summary>The error a capture earns when it cannot name the file it would write.</summary>
    /// <remarks>
    /// KTD17: falling back to the shared, un-suffixed file is the one thing this must not do. That file is a
    /// live fallback for every environment, so a capture written there would apply one environment's
    /// connection identifiers everywhere.
    /// </remarks>
    public static string BuildUninferrableRoleError(string target) =>
        $"Couldn't tell which environment '{target}' is, so there's no role to name the file after. " +
        "Pass --settings-file with the path to write.";

    /// <summary>The error a destination flag earns when the capture is a whole-project sweep.</summary>
    public static string BuildSweepDestinationError() =>
        "--settings-file names one file, and capturing every configured environment writes one per role. " +
        "Name an environment, or drop --settings-file.";

    /// <summary>Decides whether a capture's artifact argument names a packed zip or an unpacked folder.</summary>
    public static (string Path, bool IsZip) ResolveSolutionInput(string path)
    {
        var full = Path.GetFullPath(path);

        if (Directory.Exists(full)) return (full, false);
        if (File.Exists(full)) return (full, true);

        throw new FlowlineException(ExitCode.NotFound,
            $"No solution zip or folder at '{path}'.");
    }

    /// <summary>Reads a solution's unique name from an unpacked folder's manifest.</summary>
    /// <remarks>
    /// <see cref="DeployCommand.ReadArtifactSolutionManifest"/> is zip-only and throws on a directory, so an
    /// unpacked folder reads <c>Other/Solution.xml</c> and hands it to the same parser that helper uses.
    /// </remarks>
    public static string? ReadFolderSolutionUniqueName(string folder)
    {
        var manifest = Path.Combine(folder, "Other", "Solution.xml");
        if (!File.Exists(manifest))
            throw new FlowlineException(ExitCode.NotFound,
                $"No solution manifest at '{manifest}' — is '{folder}' an unpacked solution folder?");

        return DeployCommand.ParseSolutionManifest(XDocument.Load(manifest)).UniqueName;
    }
}
