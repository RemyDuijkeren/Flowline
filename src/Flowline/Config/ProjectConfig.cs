using System.Text.Json;
using Flowline.Core;
using Flowline.Utils;
using Spectre.Console;

namespace Flowline.Config;

public class ProjectConfig
{
    internal static readonly string s_configFileName = ".flowline";
    const int CurrentSchemaVersion = 1;

    public int? SchemaVersion { get; set; }
    public string? ProdUrl { get; set; }
    public string? UatUrl { get; set; }
    public string? TestUrl { get; set; }
    public string? DevUrl { get; set; }
    public ProjectSolution? Solution { get; set; }

    public string? GetOrUpdateUatUrl(string? inputUatUrl, FlowlineSettings? settings = null) =>
        GetOrUpdateUrl(inputUatUrl, () => UatUrl, v => UatUrl = v, "UAT", settings);

    public string? GetOrUpdateTestUrl(string? inputTestUrl, FlowlineSettings? settings = null) =>
        GetOrUpdateUrl(inputTestUrl, () => TestUrl, v => TestUrl = v, "Test", settings);

    public string? GetOrUpdateDevUrl(string? inputDevUrl, FlowlineSettings? settings = null) =>
        GetOrUpdateUrl(inputDevUrl, () => DevUrl, v => DevUrl = v, "Dev", settings);

    public string? GetOrUpdateProdUrl(string? inputProdUrl, FlowlineSettings? settings = null) =>
        GetOrUpdateUrl(inputProdUrl, () => ProdUrl, v => ProdUrl = v, "Prod", settings);

    // Properties can't be passed by ref, so the four environment URLs share this via get/set delegates.
    static string? GetOrUpdateUrl(
        string? input,
        Func<string?> get,
        Action<string?> set,
        string label,
        FlowlineSettings? settings)
    {
        input = input?.Trim();

        if (string.IsNullOrWhiteSpace(get()))
        {
            set(input);
            return string.IsNullOrWhiteSpace(input) ? null : input;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            if (settings is { Verbose: true })
            {
                AnsiConsole.MarkupLine($"[dim]{label}: [bold]{get()}[/][/]");
            }

            return get();
        }

        if (get() != input)
        {
            AnsiConsole.MarkupLine($"[yellow]{label} is already set: [bold]{get()}[/][/]");
            if (!ConsoleHelper.Confirm("[yellow]Overwrite it?[/]", false, settings, "config"))
            {
                AnsiConsole.MarkupLine($"[dim]Keeping {label} as-is: [link]{get()}[/][/]");
                return get();
            }
            AnsiConsole.MarkupLine($"[green]{label} updated[/]");
        }

        set(input);
        return get();
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

    /// <summary>
    /// Raw JSON pre-parse check, ahead of strongly typed deserialization, so legacy or invalid
    /// configs fail closed with <see cref="ExitCode.ConfigInvalid"/> instead of silently
    /// deserializing into a half-populated (or empty) <see cref="ProjectConfig"/>.
    /// </summary>
    /// <param name="json">The raw, unparsed contents of the <c>.flowline</c> file.</param>
    /// <param name="configPath">The config file's path, used in thrown exception messages.</param>
    /// <exception cref="FlowlineException">
    /// Thrown with <see cref="ExitCode.ConfigInvalid"/> when the JSON is malformed, its root
    /// isn't an object, it uses the legacy multi-solution <c>Solutions</c> array, its
    /// <c>SchemaVersion</c> is missing or unsupported, or a non-null <c>Solution</c> is not a
    /// JSON object or has a missing/empty <c>UniqueName</c>.
    /// </exception>
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
                $"'{configPath}' is not valid JSON.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FlowlineException(ExitCode.ConfigInvalid,
                    $"'{configPath}' is not a valid Flowline config (expected a JSON object).");
            }

            // <= 0.12.0 was multi-solution folder structure
            if (root.TryGetProperty("Solutions", out _))
            {
                throw new FlowlineException(ExitCode.ConfigInvalid,
                    $"'{configPath}' is Flowline's old multi-solution format (Solutions array) — Flowline is now single-solution tool only, a breaking change. Delete '{configPath}' and this project's old solutions/<Name>/ folder, then run 'flowline clone <solution>' to start again.");
            }

            if (!root.TryGetProperty("SchemaVersion", out var schemaVersionElement)
                || schemaVersionElement.ValueKind != JsonValueKind.Number
                || !schemaVersionElement.TryGetInt32(out var schemaVersion)
                || schemaVersion != CurrentSchemaVersion)
            {
                throw new FlowlineException(ExitCode.ConfigInvalid,
                    $"'{configPath}' has a missing or unsupported schema version. Delete '{configPath}' and run 'flowline clone <solution>' to start again.");
            }

            if (root.TryGetProperty("Solution", out var solutionElement) && solutionElement.ValueKind != JsonValueKind.Null)
            {
                if (solutionElement.ValueKind != JsonValueKind.Object)
                {
                    throw new FlowlineException(ExitCode.ConfigInvalid,
                        $"'{configPath}' has a Solution that is not a JSON object.");
                }

                var hasUniqueName = solutionElement.TryGetProperty("UniqueName", out var uniqueNameElement)
                    && uniqueNameElement.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(uniqueNameElement.GetString());

                if (!hasUniqueName)
                {
                    throw new FlowlineException(ExitCode.ConfigInvalid,
                        $"'{configPath}' has a Solution with a missing or empty UniqueName.");
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
