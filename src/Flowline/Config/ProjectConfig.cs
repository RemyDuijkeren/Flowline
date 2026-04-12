using System.Text.Json;
using Flowline.Utils;
using Spectre.Console;

namespace Flowline.Config;

public class ProjectConfig
{
    internal static readonly string s_configFileName = ".flowline";

    HashSet<ProjectSolution> _solutions = new(ProjectSolution.NameComparer);

    public string? ProdUrl { get; set; }
    public string? StagingUrl { get; set; }
    public string? DevUrl { get; set; }
    public HashSet<ProjectSolution> Solutions
    {
        get => _solutions;
        set => _solutions = value == null
            ? new HashSet<ProjectSolution>(ProjectSolution.NameComparer)
            : new HashSet<ProjectSolution>(value.Where(solution => !string.IsNullOrWhiteSpace(solution.Name)), ProjectSolution.NameComparer);
    }

    public string? GetOrUpdateStagingUrl(string? inputStagingUrl, FlowlineSettings? settings = null)
    {
        inputStagingUrl = inputStagingUrl?.Trim();

        if (string.IsNullOrWhiteSpace(StagingUrl))
        {
            StagingUrl = inputStagingUrl;
            return string.IsNullOrWhiteSpace(inputStagingUrl) ? null : inputStagingUrl;
        }

        if (string.IsNullOrWhiteSpace(inputStagingUrl))
        {
            AnsiConsole.MarkupLine($"[dim]Using configured staging environment: [bold]{StagingUrl}[/][/]");
            return StagingUrl;
        }

        if (StagingUrl != inputStagingUrl)
        {
            AnsiConsole.MarkupLine($"Staging Url found in config: {StagingUrl}");
            if (!ConsoleHelper.Confirm("[yellow]Do you want to overwrite it?[/]", false, settings))
            {
                AnsiConsole.MarkupLine($"[green]Alright, we keep as-is! See [link]{StagingUrl}[/][/]");
                return StagingUrl;
            }
            AnsiConsole.MarkupLine("[yellow]Overriding existing environment project configuration.[/]");
        }
        StagingUrl = inputStagingUrl;
        return StagingUrl;
    }

    public string? GetOrUpdateDevUrl(string? inputDevUrl, FlowlineSettings? settings = null)
    {
        inputDevUrl = inputDevUrl?.Trim();

        if (string.IsNullOrWhiteSpace(DevUrl))
        {
            DevUrl = inputDevUrl;
            return string.IsNullOrWhiteSpace(inputDevUrl) ? null : inputDevUrl;
        }

        if (string.IsNullOrWhiteSpace(inputDevUrl))
        {
            AnsiConsole.MarkupLine($"[dim]Using configured develpment environment: [bold]{DevUrl}[/][/]");
            return DevUrl;
        }

        if (DevUrl != inputDevUrl)
        {
            AnsiConsole.MarkupLine($"Development Url found in config: {DevUrl}");
            if (!ConsoleHelper.Confirm("[yellow]Do you want to overwrite it?[/]", false, settings))
            {
                AnsiConsole.MarkupLine($"[green]Alright, we keep as-is! See [link]{DevUrl}[/][/]");
                return DevUrl;
            }
            AnsiConsole.MarkupLine("[yellow]Overriding existing environment project configuration.[/]");
        }
        DevUrl = inputDevUrl;
        return DevUrl;
    }

    public string? GetOrUpdateProdUrl(string? inputProdUrl, FlowlineSettings? settings = null)
    {
        inputProdUrl = inputProdUrl?.Trim();

        if (string.IsNullOrWhiteSpace(ProdUrl))
        {
            ProdUrl = inputProdUrl;
            return string.IsNullOrWhiteSpace(inputProdUrl) ? null : inputProdUrl;
        }

        if (string.IsNullOrWhiteSpace(inputProdUrl))
        {
            if (settings is { Verbose: true })
            {
                AnsiConsole.MarkupLine($"[dim]Using configured production environment: [bold]{ProdUrl}[/][/]");
            }

            return ProdUrl;
        }

        if (ProdUrl != inputProdUrl)
        {
            AnsiConsole.MarkupLine($"Production Url found in config: {ProdUrl}");
            if (!ConsoleHelper.Confirm("[yellow]Do you want to overwrite it?[/]", false, settings))
            {
                AnsiConsole.MarkupLine($"[green]Alright, we keep as-is! See [link]{ProdUrl}[/][/]");
                return ProdUrl;
            }
            AnsiConsole.MarkupLine("[yellow]Overriding existing environment project configuration.[/]");
        }

        ProdUrl = inputProdUrl;
        return ProdUrl;
    }

    public ProjectSolution AddOrUpdateSolution(ProjectSolution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);

        if (string.IsNullOrWhiteSpace(solution.Name))
        {
            throw new ArgumentException("Solution name is required.", nameof(solution));
        }

        var normalizedSolution = new ProjectSolution
        {
            Name = solution.Name.Trim(),
            IncludeManaged = solution.IncludeManaged
        };

        _solutions.Remove(normalizedSolution);
        _solutions.Add(normalizedSolution);

        return normalizedSolution;
    }

    public ProjectSolution AddOrUpdateSolution(string name, bool includeManaged = false) =>
        AddOrUpdateSolution(new ProjectSolution { Name = name, IncludeManaged = includeManaged });

    public ProjectSolution? GetOrUpdateSolution(string? name, bool includeManaged = false, FlowlineSettings? settings = null)
    {
        name = name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            if (Solutions.Count != 1)
            {
                return null;
            }
            else
            {
                var first = Solutions.Single();
                AnsiConsole.MarkupLine($"[dim]Using configured solution: [bold]{first.Name}[/][/]");
                return first;
            }
        }

        var sln = _solutions.FirstOrDefault(solution => StringComparer.OrdinalIgnoreCase.Equals(solution.Name, name));
        if (sln == null)
        {
            return AddOrUpdateSolution(name, includeManaged);
        }

        if (sln.IncludeManaged != includeManaged)
        {
            AnsiConsole.MarkupLine($"Include Managed mismatch with existing config: {sln.Name} - managed: {sln.IncludeManaged}");

            if (!ConsoleHelper.Confirm("[yellow]Do you want to overwrite it?[/]", false, settings))
            {
                AnsiConsole.MarkupLine($"[green]Alright, we keep as-is! See [link]{ProdUrl}[/][/]");
                return sln;
            }
            AnsiConsole.MarkupLine("[yellow]Overriding existing environment project configuration.[/]");
            return AddOrUpdateSolution(name, includeManaged);
        }

        return sln;
    }

    public static ProjectConfig? Load(string? rootFolder = null)
    {
        rootFolder ??= Directory.GetCurrentDirectory();
        var configPath = Path.Combine(rootFolder, s_configFileName);

        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize<ProjectConfig>(json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read configuration: {ex.Message}");
            return null;
        }
    }

    public void Save(string? rootFolder = null)
    {
        rootFolder ??= Directory.GetCurrentDirectory();
        var configPath = Path.Combine(rootFolder, s_configFileName);

        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]Failed to save configuration.[/]");
            AnsiConsole.WriteException(ex);
        }
    }
}

public class ProjectSolution
{
    public static IEqualityComparer<ProjectSolution> NameComparer { get; } = new ProjectSolutionNameComparer();

    public string Name { get; init; } = null!;
    public bool IncludeManaged { get; set; } = false;

    private sealed class ProjectSolutionNameComparer : IEqualityComparer<ProjectSolution>
    {
        public bool Equals(ProjectSolution? x, ProjectSolution? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null)
            {
                return false;
            }

            return StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);
        }

        public int GetHashCode(ProjectSolution obj)
        {
            return StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name);
        }
    }
}
