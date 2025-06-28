using System.Text.Json;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;

namespace FlowLineCli;

public static class PacUtils
{
    public static async Task AssertPacCliInstalledAsync()
    {
        try
        {
            await Cli.Wrap("pac")
                .ExecuteBufferedAsync();
        }
        catch (Exception)
        {
            Console.Error.WriteLine("The Power Platform CLI (pac) is not installed or not in PATH. Please install it: https://aka.ms/pac");
            Environment.Exit(1);
        }
    }

    public static async Task AssertGitInstalledAsync()
    {
        try
        {
            await Cli.Wrap("git")
                .WithArguments("--version")
                .ExecuteBufferedAsync();
        }
        catch (Exception)
        {
            Console.Error.WriteLine("🦑 GIT NOT FOUND! Your commit powers are weak, young padawan. Please install Git from https://git-scm.com/ before you can join the commit side.");
            Environment.Exit(1);
        }
    }

    public static async Task<List<EnvironmentInfo>> GetEnvironmentsAsync()
    {
        var result = await Cli.Wrap("pac")
            .WithArguments("admin list --json")
            .ExecuteBufferedAsync();

        return JsonSerializer.Deserialize<List<EnvironmentInfo>>(result.StandardOutput) ?? new List<EnvironmentInfo>();
    }

    public static EnvironmentParts GetPartsFromEnvUrl(string envUrl)
    {
        var regex = new Regex(@"^https://([^.]+)\.([^.]+\.[^.]+\.[a-z]+)(?:/|$)");
        var match = regex.Match(envUrl);

        if (!match.Success)
        {
            Console.Error.WriteLine($"Could not extract environment name and region domain from URL: {envUrl}");
            Environment.Exit(1);
        }

        var envDomain = match.Groups[1].Value;
        var regionDomain = match.Groups[2].Value;

        var regionDomainToRegion = new Dictionary<string, string>
        {
            { "crm.dynamics.com", "unitedstates" },
            { "crm3.dynamics.com", "canada" },
            { "crm2.dynamics.com", "southamerica" },
            { "crm4.dynamics.com", "europe" },
            { "crm12.dynamics.com", "france" },
            { "crm.microsoftdynamics.de", "germany" },
            { "crm21.dynamics.com", "switzerland" },
            { "crm11.dynamics.com", "unitedkingdom" },
            { "crm22.dynamics.com", "norway" },
            { "crm5.dynamics.com", "asia" },
            { "crm6.dynamics.com", "japan" },
            { "crm8.dynamics.com", "australia" },
            { "crm9.dynamics.com", "india" },
            { "crm20.dynamics.com", "uae" },
            { "crm19.dynamics.com", "korea" },
            { "crm.dynamics.cn", "china" },
            { "crm.appsplatform.us", "usgovhigh" }
        };

        if (!regionDomainToRegion.TryGetValue(regionDomain, out var region))
        {
            Console.Error.WriteLine($"Unknown region/domain: {regionDomain}");
            Environment.Exit(1);
        }

        return new EnvironmentParts
        {
            EnvDomain = envDomain,
            RegionDomain = regionDomain,
            Region = region
        };
    }
}

public class EnvironmentInfo
{
    public string? EnvironmentUrl { get; set; }
    public string? Type { get; set; }
    public string? DisplayName { get; set; }
    // Add other properties as needed
}

public class EnvironmentParts
{
    public string EnvDomain { get; set; } = null!;
    public string RegionDomain { get; set; } = null!;
    public string Region { get; set; } = null!;
}
