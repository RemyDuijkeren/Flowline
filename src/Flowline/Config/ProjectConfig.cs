using System.Text.Json;
using Spectre.Console;

namespace Flowline.Config;

public class ProjectConfig
{
    public string? ProductionEnvironment
    {
        get;
        set
        {
            if (!string.IsNullOrEmpty(field))
            {
                if (field == value) return;

                AnsiConsole.MarkupLine($"Production Environment found in config: {field}");
                if (!AnsiConsole.Confirm("[yellow]Do you want to overwrite it?[/]", false))
                {
                    AnsiConsole.MarkupLine($"[green]Alright, we keep as-is! See [link]{field}[/][/]");
                    return;
                }
                AnsiConsole.MarkupLine("[yellow]Overriding existing environment project configuration.[/]");
            }
            field = value;
        }
    }

    public string? StagingEnvironment
    {
        get;
        set
        {
            if (!string.IsNullOrEmpty(field))
            {
                if (field == value) return;

                AnsiConsole.MarkupLine($"Staging Environment found in config: {field}");
                if (!AnsiConsole.Confirm("[yellow]Do you want to overwrite it?[/]", false))
                {
                    AnsiConsole.MarkupLine($"[green]Alright, we keep as-is! See [link]{field}[/][/]");
                    return;
                }
                AnsiConsole.MarkupLine("[yellow]Overriding existing environment project configuration.[/]");
            }
            field = value;
        }
    }

    public string? DevelopmentEnvironment
    {
        get;
        set
        {
            if (!string.IsNullOrEmpty(field))
            {
                if (field == value) return;

                AnsiConsole.MarkupLine($"Development Environment found in config: {field}");
                if (!AnsiConsole.Confirm("[yellow]Do you want to overwrite it?[/]", false))
                {
                    AnsiConsole.MarkupLine($"[green]Alright, we keep as-is! See [link]{field}[/][/]");
                    return;
                }
                AnsiConsole.MarkupLine("[yellow]Overriding existing environment project configuration.[/]");
            }
            field = value;
        }
    }

    public string? SolutionName
    {
        get;
        set
        {
            if (!string.IsNullOrEmpty(field))
            {
                if (field == value) return;

                AnsiConsole.MarkupLine($"SolutionName in config: {field}");
                if (!AnsiConsole.Confirm("[yellow]Do you want to overwrite it?[/]", false))
                {
                    AnsiConsole.MarkupLine($"[green]Alright, we keep as-is! See {field}[/]");
                    return;
                }
                AnsiConsole.MarkupLine("[yellow]Overriding existing SolutionName in config.[/]");
            }
            field = value;
        }
    }

    public bool UseManagedSolution
    {
        get;
        set
        {
            if (field == value) return;

            AnsiConsole.MarkupLine($"UseManagedSolution found in config: {field}");
            if (!AnsiConsole.Confirm("[yellow]Do you want to overwrite it?[/]", false))
            {
                AnsiConsole.MarkupLine($"[green]Alright, we keep as-is! See {field}[/]");
                return;
            }

            AnsiConsole.MarkupLine("[yellow]Overriding UseManagedSolution in config.[/]");
            field = value;
        }
    } = false;

    internal static readonly string ConfigFileName = ".flowline";

    public static ProjectConfig? Load(string? rootFolder = null)
    {
        rootFolder ??= Directory.GetCurrentDirectory();
        var configPath = Path.Combine(rootFolder, ConfigFileName);

        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<ProjectConfig>(json);
            return config;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read configuration: {ex.Message}");
            // If we can't read the config file, return null
            return null;
        }
    }

    public void Save(string? rootFolder = null)
    {
        rootFolder ??= Directory.GetCurrentDirectory();
        var configPath = Path.Combine(rootFolder, ConfigFileName);

        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to save configuration: {ex.Message}");
        }
    }
}
