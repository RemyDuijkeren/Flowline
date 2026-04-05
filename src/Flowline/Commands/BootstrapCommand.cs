using System.ComponentModel;
using CliWrap;
using Flowline.Config;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class BootstrapCommand : AsyncCommand<BootstrapCommand.Settings>
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "[git-remote-url]")]
        [Description("Git repository URL")]
        public string GitRemoteUrl { get; set; } = null!; // = "https://github.com/AutomateValue/Dataverse01.git";

        [CommandOption("-e|--environment <URL>")]
        [Description("The environment to run the command against")]
        public string? Environment { get; set; }

        [CommandOption("-s|--solution")]
        [Description("The solution name to initialize")]
        [DefaultValue("Cr07982")]
        public string? SolutionName { get; set; } = "Cr07982";

        [CommandOption("--managed")]
        [Description("Use managed solution instead of unmanaged")]
        public bool Managed { get; set; } = false;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        await GitUtils.AssertGitInstalledAsync(cancellationToken);
        await PacUtils.AssertPacCliInstalledAsync(cancellationToken);

        // Clone Git repo if not already a Git repo
        var rootFolder = Directory.GetCurrentDirectory();
        if (!Directory.Exists(Path.Combine(rootFolder, ".git")))
        {
            AnsiConsole.MarkupLine("No Git repository found. Cloning...");

            var result = await Cli.Wrap("git")
                                  .WithArguments(args => args
                                                         .Add("clone")
                                                         .Add(settings.GitRemoteUrl)
                                                         .Add(rootFolder))
                                  .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]GIT: {s}[/]")))
                                  .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                                  .ExecuteAsync(cancellationToken);

            if (result.ExitCode != 0)
            {
                AnsiConsole.MarkupLine("[red]Failed to clone the repository. Please check the Git URL and your network connection.[/]");
                return 1;
            }
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]Git repository already initialized.[/]");

            (string? remoteName, string? remoteUrl) = await GitUtils.GetRemoteUrlAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(remoteUrl))
            {
                AnsiConsole.MarkupLineInterpolated($"  Remote URL: [link]{remoteUrl}[/] ({remoteName})");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]No remote configured for the current branch.[/]");
            }
        }

        // Execute Clone command

        return 0;
    }
}
