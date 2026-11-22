using System.ComponentModel;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Solutions;
using Flowline.Services;
using Flowline.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

/// <summary>
/// <c>flowline pack</c> — packs the committed solution into the same versioned zip <c>deploy</c> would import,
/// without a target environment, so CI can build the artifact once and hand it to a customer or to
/// <c>deploy --path</c>.
/// </summary>
public class PackCommand(CommandServices services) : FlowlineCommand<PackCommand.Settings>(services)
{
    public sealed class Settings : DataverseSettings
    {
        [CommandOption("-o|--output <dir>")]
        [Description("Folder to write the zip to (default: artifacts/ next to the solution)")]
        public string? Output { get; set; }

        [CommandOption("--skip-solution-check")]
        [Description("Skip the solution checker gate")]
        [DefaultValue(false)]
        public bool SkipSolutionCheck { get; set; } = false;
    }

    // "drift" is the one hazard pack gates: a committed binary that differs from the local build output.
    internal static readonly string[] ValidSpecifiers = ["drift", "all"];
    protected override string[] ValidForceSpecifiers => ValidSpecifiers;

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var sln = Config.Solution ?? throw new FlowlineException(ExitCode.ConfigInvalid, "No solution configured — run 'clone' first.");
        var layout = await SolutionFileLayout.LoadAsync(RootFolder, cancellationToken);
        var dataverseSolutionFolder = layout.DataverseSolutionFolder;

        await SolutionPackService.ValidateGitCleanAsync(SolutionPackService.GetPackInputPaths(layout, dataverseSolutionFolder), RootFolder, _capture, "pack", cancellationToken);

        if (FindMappingFile(dataverseSolutionFolder) is { } mappingFile)
            Console.Info($"Found {mappingFile} — Flowline doesn't read mapping files. Pack packs what's committed; --binaries-from-build packs fresh plugin and web resource builds instead.");

        await SolutionPackService.ValidateDriftAsync(Console, RuntimeOptions.HasForce("drift"), sln.UniqueName, layout, dataverseSolutionFolder, "pack", cancellationToken);

        var version = DeployCommand.ReadLocalSolutionVersion(dataverseSolutionFolder);
        var zipPath = SolutionPackService.ResolveZipPath(RootFolder, settings.Output, sln.UniqueName, version, sln.IncludeManaged);

        var staged = await SolutionPackService.StageTrackedSolutionAsync(dataverseSolutionFolder, RootFolder, _capture, cancellationToken);
        try
        {
            SolutionPackService.EnsureSlotsHaveBinaries(staged);
            await SolutionPackService.PackAsync(Console, _capture, sln.UniqueName, staged, zipPath, sln.IncludeManaged, cancellationToken);
        }
        finally
        {
            SolutionPackService.DeleteStagedFolder(staged, Logger);
        }
        Console.Ok($"Packed [bold]{sln.UniqueName}[/] {version} ({(sln.IncludeManaged ? "managed" : "unmanaged")})");

        if (settings.SkipSolutionCheck)
            Console.Skip("Solution checker — skipping (--skip-solution-check)");
        else
            await RunSolutionCheckAsync(zipPath, cancellationToken);

        SolutionPackService.PublishArtifactForCi(Logger, zipPath, sln.UniqueName, version);

        Console.Done($"Packed to {ConsolePath.FormatRelativePath(zipPath, RootFolder)}. Hand it over, or import it with 'deploy <env> --path'.");
        return 0;
    }

    async Task RunSolutionCheckAsync(string zipPath, CancellationToken ct)
    {
        try
        {
            // No environment: pac checks with the active auth profile, so pack needs a PAC login but no target.
            await SolutionCheckService.RunAsync(Console, _capture, zipPath, environmentUrl: null, ct);
        }
        catch (FlowlineException ex) when (ex.ExitCode == ExitCode.ConnectionFailed)
        {
            throw new FlowlineException(ExitCode.ConnectionFailed, BuildCheckerUnavailableMessage(ex.Message), ex);
        }
    }

    internal static string BuildCheckerUnavailableMessage(string checkerMessage) =>
        $"{checkerMessage.TrimEnd('.')}, or pass --skip-solution-check to pack without it.";

    /// <summary>A mapping file beside the cdsproj, matched case-insensitively; the two names issue 24 lists.</summary>
    internal static string? FindMappingFile(string dataverseSolutionFolder) =>
        Directory.Exists(dataverseSolutionFolder)
            ? Directory.EnumerateFiles(dataverseSolutionFolder)
                .Select(Path.GetFileName)
                .FirstOrDefault(name => name is not null
                    && (name.Equals("Mapping.xml", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("solution-mapping.xml", StringComparison.OrdinalIgnoreCase)))
            : null;
}
