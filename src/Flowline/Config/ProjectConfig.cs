using System.Text.Json;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Infrastructure;
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
        GetOrUpdateUrl(EnvironmentRole.Uat, inputUatUrl, settings);

    public string? GetOrUpdateTestUrl(string? inputTestUrl, FlowlineSettings? settings = null) =>
        GetOrUpdateUrl(EnvironmentRole.Test, inputTestUrl, settings);

    public string? GetOrUpdateDevUrl(string? inputDevUrl, FlowlineSettings? settings = null) =>
        GetOrUpdateUrl(EnvironmentRole.Dev, inputDevUrl, settings);

    public string? GetOrUpdateProdUrl(string? inputProdUrl, FlowlineSettings? settings = null) =>
        GetOrUpdateUrl(EnvironmentRole.Prod, inputProdUrl, settings);

    // Read-only role-keyed accessor — lets a caller (EnvironmentTargetResolver) look up or compare
    // against a role's URL without switching on EnvironmentRole itself.
    public string? GetUrl(EnvironmentRole role) => role switch
    {
        EnvironmentRole.Prod => ProdUrl,
        EnvironmentRole.Uat  => UatUrl,
        EnvironmentRole.Test => TestUrl,
        EnvironmentRole.Dev  => DevUrl,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    // The one place a role picks its .flowline slot; the four named wrappers above delegate here.
    public string? GetOrUpdateUrl(EnvironmentRole role, string? inputUrl, FlowlineSettings? settings = null, string? saveReason = null) => role switch
    {
        EnvironmentRole.Prod => GetOrUpdateValue(inputUrl, () => ProdUrl, v => ProdUrl = v, "Prod", "ProdUrl", settings, saveReason),
        EnvironmentRole.Uat  => GetOrUpdateValue(inputUrl, () => UatUrl, v => UatUrl = v, "UAT", "UatUrl", settings, saveReason),
        EnvironmentRole.Test => GetOrUpdateValue(inputUrl, () => TestUrl, v => TestUrl = v, "Test", "TestUrl", settings, saveReason),
        EnvironmentRole.Dev  => GetOrUpdateValue(inputUrl, () => DevUrl, v => DevUrl = v, "Dev", "DevUrl", settings, saveReason),
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    // R11: the one write-on-first-use, ask-on-change rule for every persisting flag — originally just
    // the four environment URLs, now shared by GetOrUpdateSolution's managed branch and generate's five
    // flags too (GenerateCommand.ApplyPersistedGenerateSettings). Properties/fields can't be passed by
    // ref, so callers wire their backing value through get/set delegates. key is the .flowline JSON path
    // (e.g. "DevUrl", "Solution.Generate.Namespace") — printed on first save, distinct from label (e.g.
    // "Dev", "Namespace"), which reads better in the overwrite prompt. saveReason, when given, is
    // appended to the first-save line as "(inferred from <reason>)" — EnvironmentTargetResolver's only
    // caller of that parameter; every other call site leaves it null. get()'s value is user-controlled
    // for non-URL callers, so it's escaped before going into markup.
    internal static string? GetOrUpdateValue(
        string? input,
        Func<string?> get,
        Action<string?> set,
        string label,
        string key,
        FlowlineSettings? settings,
        string? saveReason = null)
    {
        input = input?.Trim();

        if (string.IsNullOrWhiteSpace(get()))
        {
            set(input);
            if (!string.IsNullOrWhiteSpace(input))
            {
                var reasonSuffix = string.IsNullOrWhiteSpace(saveReason) ? "" : $" (inferred from {saveReason})";
                AnsiConsole.Console.Ok($"Saved to .flowline: {key}{reasonSuffix}");
            }
            return string.IsNullOrWhiteSpace(input) ? null : input;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            if (settings is { Verbose: true })
            {
                // Verbose(string) escapes its message — markup here would print as literal [bold] tags.
                AnsiConsole.Console.Verbose($"{label}: {get()}");
            }

            return get();
        }

        if (get() != input)
        {
            AnsiConsole.Console.Warning($"{label} is already set: [bold]{Markup.Escape(get()!)}[/]");
            if (!AnsiConsole.Console.Confirm("Overwrite it?", false, settings, "config"))
            {
                AnsiConsole.MarkupLine($"[dim]Keeping {label} as-is: {Markup.Escape(get()!)}[/]");
                return get();
            }
            AnsiConsole.Console.Ok($"{label} updated");
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
                AnsiConsole.Console.Verbose($"Solution: {uniqueName}");
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

        // R11: absent (includeManaged null) leaves Solution untouched — guarded here rather than left
        // to GetOrUpdateValue's own absent branch, since every other caller of GetOrUpdateSolution
        // (drift, configure, push, generate's own solution resolution) passes includeManaged: null and
        // would otherwise pick up an unrequested verbose echo on every run.
        if (includeManaged.HasValue)
        {
            GetOrUpdateValue(
                includeManaged.Value.ToString(),
                () => Solution.IncludeManaged.ToString(),
                v => AddOrUpdateSolution(new ProjectSolution
                {
                    UniqueName = uniqueName,
                    IncludeManaged = bool.Parse(v!),
                    Generate = Solution.Generate,
                    PluginPackageMode = Solution.PluginPackageMode,
                }),
                "Managed", "Solution.IncludeManaged", settings);
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
            throw new FlowlineException(ExitCode.ConfigInvalid,
                $"Failed to read configuration '{configPath}': {ex.Message}", ex);
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
            throw new FlowlineException(ExitCode.ConfigInvalid, 
                $"Failed to save configuration '{configPath}': {ex.Message}", ex);
        }
    }
}
