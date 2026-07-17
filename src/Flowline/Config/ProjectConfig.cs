using System.Text.Json;
using Flowline.Core;
using Flowline.Utils;
using Spectre.Console;

namespace Flowline.Config;

public class ProjectConfig
{
    internal static readonly string s_configFileName = ".flowline";
    const int CurrentSchemaVersion = 1;
    const string DocPointer = "see docs/folder-structure.md";

    public int? SchemaVersion { get; set; }
    public string? ProdUrl { get; set; }
    public string? UatUrl { get; set; }
    public string? TestUrl { get; set; }
    public string? DevUrl { get; set; }
    public ProjectSolution? Solution { get; set; }

    public string? GetOrUpdateUatUrl(string? inputUatUrl, FlowlineSettings? settings = null)
    {
        inputUatUrl = inputUatUrl?.Trim();

        if (string.IsNullOrWhiteSpace(UatUrl))
        {
            UatUrl = inputUatUrl;
            return string.IsNullOrWhiteSpace(inputUatUrl) ? null : inputUatUrl;
        }

        if (string.IsNullOrWhiteSpace(inputUatUrl))
        {
            if (settings is { Verbose: true })
            {
                AnsiConsole.MarkupLine($"[dim]UAT: [bold]{UatUrl}[/][/]");
            }

            return UatUrl;
        }

        if (UatUrl != inputUatUrl)
        {
            AnsiConsole.MarkupLine($"[yellow]UAT is already set: [bold]{UatUrl}[/][/]");
            if (!ConsoleHelper.Confirm("[yellow]Overwrite it?[/]", false, settings, "config"))
            {
                AnsiConsole.MarkupLine($"[dim]Keeping UAT as-is: [link]{UatUrl}[/][/]");
                return UatUrl;
            }
            AnsiConsole.MarkupLine("[green]UAT updated[/]");
        }

        UatUrl = inputUatUrl;
        return UatUrl;
    }

    public string? GetOrUpdateTestUrl(string? inputTestUrl, FlowlineSettings? settings = null)
    {
        inputTestUrl = inputTestUrl?.Trim();

        if (string.IsNullOrWhiteSpace(TestUrl))
        {
            TestUrl = inputTestUrl;
            return string.IsNullOrWhiteSpace(inputTestUrl) ? null : inputTestUrl;
        }

        if (string.IsNullOrWhiteSpace(inputTestUrl))
        {
            if (settings is { Verbose: true })
            {
                AnsiConsole.MarkupLine($"[dim]Test: [bold]{TestUrl}[/][/]");
            }

            return TestUrl;
        }

        if (TestUrl != inputTestUrl)
        {
            AnsiConsole.MarkupLine($"[yellow]Test is already set: [bold]{TestUrl}[/][/]");
            if (!ConsoleHelper.Confirm("[yellow]Overwrite it?[/]", false, settings, "config"))
            {
                AnsiConsole.MarkupLine($"[dim]Keeping test as-is: [link]{TestUrl}[/][/]");
                return TestUrl;
            }
            AnsiConsole.MarkupLine("[green]Test updated[/]");
        }

        TestUrl = inputTestUrl;
        return TestUrl;
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
            if (settings is { Verbose: true })
            {
                AnsiConsole.MarkupLine($"[dim]Dev: [bold]{DevUrl}[/][/]");
            }

            return DevUrl;
        }

        if (DevUrl != inputDevUrl)
        {
            AnsiConsole.MarkupLine($"[yellow]Dev is already set: [bold]{DevUrl}[/][/]");
            if (!ConsoleHelper.Confirm("[yellow]Overwrite it?[/]", false, settings, "config"))
            {
                AnsiConsole.MarkupLine($"[dim]Keeping dev as-is: [link]{DevUrl}[/][/]");
                return DevUrl;
            }
            AnsiConsole.MarkupLine("[green]Dev updated[/]");
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
                AnsiConsole.MarkupLine($"[dim]Prod: [bold]{ProdUrl}[/][/]");
            }

            return ProdUrl;
        }

        if (ProdUrl != inputProdUrl)
        {
            AnsiConsole.MarkupLine($"[yellow]Prod is already set: [bold]{ProdUrl}[/][/]");
            if (!ConsoleHelper.Confirm("[yellow]Overwrite it?[/]", false, settings, "config"))
            {
                AnsiConsole.MarkupLine($"[dim]Keeping prod as-is: [link]{ProdUrl}[/][/]");
                return ProdUrl;
            }
            AnsiConsole.MarkupLine("[green]Prod updated[/]");
        }

        ProdUrl = inputProdUrl;
        return ProdUrl;
    }

    public ProjectSolution AddOrUpdateSolution(ProjectSolution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);

        if (string.IsNullOrWhiteSpace(solution.UniqueName))
        {
            throw new ArgumentException("Solution unique name is required.", nameof(solution));
        }

        var normalizedSolution = new ProjectSolution
        {
            UniqueName = solution.UniqueName.Trim(),
            IncludeManaged = solution.IncludeManaged,
            Generate = solution.Generate,
            PluginPackageMode = solution.PluginPackageMode,
        };

        Solution = normalizedSolution;

        return normalizedSolution;
    }

    public ProjectSolution AddOrUpdateSolution(string uniqueName, bool includeManaged = false)
    {
        var existing = Solution;
        return AddOrUpdateSolution(new ProjectSolution
        {
            UniqueName = uniqueName,
            IncludeManaged = includeManaged,
            Generate = existing?.Generate,
            PluginPackageMode = existing?.PluginPackageMode ?? PluginPackageMode.Auto,
        });
    }

    public ProjectSolution? GetOrUpdateSolution(string? uniqueName, bool? includeManaged = null, FlowlineSettings? settings = null)
    {
        uniqueName = uniqueName?.Trim();
        if (string.IsNullOrWhiteSpace(uniqueName))
        {
            if (Solution == null)
            {
                return null;
            }

            uniqueName = Solution.UniqueName;
            if (settings is { Verbose: true })
            {
                AnsiConsole.MarkupLine($"[dim]Solution: [bold]{uniqueName}[/][/]");
            }
        }

        if (Solution == null)
        {
            return AddOrUpdateSolution(uniqueName, includeManaged ?? false);
        }

        if (!string.Equals(uniqueName, Solution.UniqueName, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"'{uniqueName}' doesn't match the configured solution '{Solution.UniqueName}' — pass the correct name, or omit it to use the configured solution.");
        }

        if (includeManaged.HasValue && Solution.IncludeManaged != includeManaged.Value)
        {
            AnsiConsole.MarkupLine($"[yellow]{Solution.UniqueName} is already set to managed: {Solution.IncludeManaged}[/]");

            if (!ConsoleHelper.Confirm("[yellow]Overwrite it?[/]", false, settings, "config"))
            {
                AnsiConsole.MarkupLine("[dim]Keeping solution config as-is[/]");
                return Solution;
            }
            AnsiConsole.MarkupLine("[green]Solution config updated[/]");
            return AddOrUpdateSolution(new ProjectSolution
            {
                UniqueName = uniqueName,
                IncludeManaged = includeManaged.Value,
                Generate = Solution.Generate,
                PluginPackageMode = Solution.PluginPackageMode,
            });
        }

        return Solution;
    }

    public static ProjectConfig? Load(string? rootFolder = null)
    {
        rootFolder ??= Directory.GetCurrentDirectory();
        var configPath = Path.Combine(rootFolder, s_configFileName);

        if (!File.Exists(configPath))
        {
            return null;
        }

        string json;
        try
        {
            json = File.ReadAllText(configPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read configuration: {ex.Message}");
            return null;
        }

        ValidateSchema(json, configPath);

        return JsonSerializer.Deserialize<ProjectConfig>(json);
    }

    // Raw JSON pre-parse check, ahead of strongly-typed deserialization, so legacy/invalid
    // configs fail closed with ConfigInvalid instead of silently deserializing into a
    // half-populated (or empty) ProjectConfig. See R13 in the refactor plan.
    static void ValidateSchema(string json, string configPath)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FlowlineException(ExitCode.ConfigInvalid,
                $"'{configPath}' is not valid JSON — {DocPointer}.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FlowlineException(ExitCode.ConfigInvalid,
                    $"'{configPath}' is not a valid Flowline config (expected a JSON object) — {DocPointer}.");
            }

            if (root.TryGetProperty("Solutions", out _))
            {
                throw new FlowlineException(ExitCode.ConfigInvalid,
                    $"'{configPath}' is on Flowline's old multi-solution format (Solutions array) — Flowline is now single-solution only, a breaking change. Delete '{configPath}' and this project's old solutions/<Name>/ folder, then run 'flowline clone <solution>' to start again.");
            }

            if (!root.TryGetProperty("SchemaVersion", out var schemaVersionElement)
                || schemaVersionElement.ValueKind != JsonValueKind.Number
                || !schemaVersionElement.TryGetInt32(out var schemaVersion)
                || schemaVersion != CurrentSchemaVersion)
            {
                throw new FlowlineException(ExitCode.ConfigInvalid,
                    $"'{configPath}' has a missing or unsupported schema version — likely an old project from before Flowline's single-solution breaking change. Delete '{configPath}' and run 'flowline clone <solution>' to start again.");
            }

            if (root.TryGetProperty("Solution", out var solutionElement) && solutionElement.ValueKind != JsonValueKind.Null)
            {
                if (solutionElement.ValueKind != JsonValueKind.Object)
                {
                    throw new FlowlineException(ExitCode.ConfigInvalid,
                        $"'{configPath}' has a Solution that is not a JSON object — {DocPointer}.");
                }

                var hasUniqueName = solutionElement.TryGetProperty("UniqueName", out var uniqueNameElement)
                    && uniqueNameElement.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(uniqueNameElement.GetString());

                if (!hasUniqueName)
                {
                    throw new FlowlineException(ExitCode.ConfigInvalid,
                        $"'{configPath}' has a Solution with a missing or empty UniqueName — {DocPointer}.");
                }
            }
        }
    }

    public void Save(string? rootFolder = null)
    {
        rootFolder ??= Directory.GetCurrentDirectory();
        var configPath = Path.Combine(rootFolder, s_configFileName);

        SchemaVersion ??= CurrentSchemaVersion;

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
