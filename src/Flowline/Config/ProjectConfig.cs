using System.Text.Json;

namespace Flowline.Config;

public class ProjectConfig
{
    // Development environment URL
    public string SandboxEnvironment { get; set; } = string.Empty;

    // Production environment URL
    public string ProductionEnvironment { get; set; } = string.Empty;

    // The current branch environment (defaults to development if available)
    public string BranchEnvironment { get; set; } = string.Empty;

    public string SolutionName { get; set; } = string.Empty;
    public bool UseManagedSolution { get; set; } = false;

    internal static readonly string ConfigFileName = ".flowline";

    // Get the current environment to use (active, or fall back to dev, then prod)
    public string GetCurrentEnvironment()
    {
        if (!string.IsNullOrEmpty(BranchEnvironment))
            return BranchEnvironment;

        if (!string.IsNullOrEmpty(SandboxEnvironment))
            return SandboxEnvironment;

        return ProductionEnvironment;
    }

    // Set an environment as both the corresponding type and make it active
    public void SetEnvironment(string url, bool isProd)
    {
        if (isProd)
        {
            ProductionEnvironment = url;
        }
        else
        {
            SandboxEnvironment = url;
        }

        // Set as an active environment too
        BranchEnvironment = url;
    }

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
