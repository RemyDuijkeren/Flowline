using System.Text.Json;

namespace Flowline.Config;

public class ProjectConfig
{
    // Development environment URL
    public string DevEnvironment { get; set; } = string.Empty;

    // Production environment URL
    public string ProdEnvironment { get; set; } = string.Empty;

    // The current active environment (defaults to development if available)
    public string ActiveEnvironment { get; set; } = string.Empty;

    public string SolutionName { get; set; } = string.Empty;
    public bool UseManagedSolution { get; set; } = false;

    private static readonly string ConfigFileName = ".flowline";

    // Get the current environment to use (active, or fall back to dev, then prod)
    public string GetCurrentEnvironment()
    {
        if (!string.IsNullOrEmpty(ActiveEnvironment))
            return ActiveEnvironment;

        if (!string.IsNullOrEmpty(DevEnvironment))
            return DevEnvironment;

        return ProdEnvironment;
    }

    // Set an environment as both the corresponding type and make it active
    public void SetEnvironment(string url, bool isProd)
    {
        if (isProd)
        {
            ProdEnvironment = url;
        }
        else
        {
            DevEnvironment = url;
        }

        // Set as an active environment too
        ActiveEnvironment = url;
    }

    public static ProjectConfig Load(string? rootFolder = null)
    {
        rootFolder ??= Directory.GetCurrentDirectory();
        var configPath = Path.Combine(rootFolder, ConfigFileName);

        if (!File.Exists(configPath))
        {
            return new ProjectConfig();
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<ProjectConfig>(json) ?? new ProjectConfig();
            return config;
        }
        catch (Exception)
        {
            // If we can't read the config file, return a default config
            return new ProjectConfig();
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
