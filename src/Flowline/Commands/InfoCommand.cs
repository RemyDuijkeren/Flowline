using System.ComponentModel;
using System.Reflection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class InfoCommandSettings : BaseCommandSettings
{
}

public class InfoCommand : AsyncCommand<InfoCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, InfoCommandSettings settings)
    {
        AnsiConsole.MarkupLine($"[bold]Flowline[/] version: [green]{Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version}[/]");

        try
        {
            var pacVersion = await PacUtils.AssertPacCliInstalledAsync();
            AnsiConsole.MarkupLine($"[bold]Power Platform CLI[/] version: [green]{pacVersion}[/]");

            var gitVersion = await PacUtils.AssertGitInstalledAsync();
            AnsiConsole.MarkupLine($"[bold]Git[/] version: [green]{gitVersion}[/]");

            if (settings.Verbose)
            {
                AnsiConsole.MarkupLine("\n[bold]Environment Information:[/]");
                AnsiConsole.MarkupLine($"Operating System: [green]{Environment.OSVersion}[/]");
                AnsiConsole.MarkupLine($".NET Runtime: [green]{Environment.Version}[/]");
                AnsiConsole.MarkupLine($"64-bit OS: [green]{Environment.Is64BitOperatingSystem}[/]");
            }
        }
        catch
        {
            // PAC CLI and Git checks will exit the application if not found
        }

        return 0;
    }
}
