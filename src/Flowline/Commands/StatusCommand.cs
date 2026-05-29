using System.ComponentModel;
using Flowline.Config;
using Flowline.Utils;
using Flowline.Validation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class StatusCommand(IAnsiConsole console) : AsyncCommand<StatusCommand.Settings>
{
    private readonly IAnsiConsole Console = console;

    public sealed class Settings : FlowlineSettings
    {
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ConsoleHelper.WelcomeScreen(Console);

        try
        {
            var dotNet = await FlowlineValidator.Default.EnsureDotNetAsync(settings, cancellationToken);
            Console.MarkupLine($"[bold].NET SDK[/] version: [green]{dotNet.Version}[/]");

            var pac = await FlowlineValidator.Default.EnsurePacCliAsync(settings, cancellationToken);
            Console.MarkupLine($"[bold]Power Platform CLI[/] version: [green]{pac.Version}[/] ({pac.InstallType})");

            var git = await FlowlineValidator.Default.EnsureGitAsync(settings, cancellationToken);
            Console.MarkupLine($"[bold]Git[/] version: [green]{git.Version}[/]");

            if (settings.Verbose)
            {
                Console.MarkupLine("\n[bold]Environment Information:[/]");
                Console.MarkupLine($"Operating System: [green]{Environment.OSVersion}[/]");
                Console.MarkupLine($".NET Runtime: [green]{Environment.Version}[/]");
                Console.MarkupLine($"64-bit OS: [green]{Environment.Is64BitOperatingSystem}[/]");
            }
        }
        catch
        {
            // PAC CLI and Git checks will exit the application if not found
        }

        // Show the current configuration
        var config = ProjectConfig.Load();
        Console.MarkupLine("\n[bold]Configuration[/]");

        if (config is null)
        {
            Console.MarkupLine("  [yellow]No .flowline config found[/]");
            return 0;
        }

        var envs = new (string Label, string? Url)[]
        {
            ("Production",  config.ProdUrl),
            ("Test",        config.TestUrl),
            ("Development", config.DevUrl),
        };

        var hasUrls = envs.Any(e => !string.IsNullOrEmpty(e.Url));
        var solutions = config.Solutions.ToList();

        (string Label, string? Url, WhoAmIInfo? Who, Dictionary<string, string?> Versions)[] results;

        if (hasUrls)
        {
            results = await Console.Status().FlowlineSpinner().StartAsync(
                "Checking environment connectivity...",
                _ => Task.WhenAll(envs.Select(async e =>
                {
                    var who = !string.IsNullOrEmpty(e.Url)
                        ? await PacUtils.GetEnvWhoAsync(e.Url!, cancellationToken)
                        : null;

                    var versions = new Dictionary<string, string?>();
                    if (who is not null && solutions.Count > 0)
                    {
                        var versionTasks = solutions.Select(async sol =>
                        {
                            string? version = null;
                            try
                            {
                                version = await PacUtils.GetSolutionVersionAsync(sol.Name, e.Url!, cancellationToken: cancellationToken);
                            }
                            catch (FlowlineException)
                            {
                                // solution not deployed or version unreadable
                            }
                            return (sol.Name, version);
                        });
                        foreach (var (name, ver) in await Task.WhenAll(versionTasks))
                            versions[name] = ver;
                    }

                    return (e.Label, e.Url, who, versions);
                })));
        }
        else
        {
            results = envs.Select(e => (e.Label, e.Url, (WhoAmIInfo?)null, new Dictionary<string, string?>())).ToArray();
        }

        foreach (var (label, url, who, versions) in results)
        {
            if (string.IsNullOrEmpty(url))
            {
                Console.MarkupLine($"  {label}: [gray]Not configured[/]");
                continue;
            }

            Console.MarkupLine($"  {label}: [green]{Markup.Escape(url)}[/]");

            if (who is not null)
                Console.MarkupLine($"    [green]✓[/] {Markup.Escape(who.ConnectedAs)}");
            else
                Console.MarkupLine($"    [yellow]✗ Not authenticated[/]");

            foreach (var (solutionName, version) in versions)
            {
                if (version is not null)
                    Console.MarkupLine($"    [dim]{Markup.Escape(solutionName)}[/]  [green]{Markup.Escape(version)}[/]");
                else
                    Console.MarkupLine($"    [dim]{Markup.Escape(solutionName)}  not deployed[/]");
            }
        }

        return 0;
    }
}
