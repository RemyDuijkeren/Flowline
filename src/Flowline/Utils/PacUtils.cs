using System.Text.Json;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;

namespace Flowline;

public static class PacUtils
{
    public static async Task<string> AssertPacCliInstalledAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Cli.Wrap("pac")
                         .ExecuteBufferedAsync(cancellationToken);

            // Extract version from the output
            var versionLine = result.StandardOutput.Split('\n')
                .FirstOrDefault(line => line.Trim().StartsWith("Version:"));

            if (versionLine != null)
            {
                return versionLine.Trim().Replace("Version: ", string.Empty);
            }

            return "Unknown";
        }
        catch (Exception)
        {
            Console.Error.WriteLine("The Power Platform CLI (pac) is not installed or not in PATH. Please install it: https://aka.ms/pac");
            Environment.Exit(1);
            return string.Empty; // This line will never be reached due to Environment.Exit
        }
    }

    public static async Task<List<EnvironmentInfo>> GetEnvironmentsAsync(CancellationToken cancellationToken = default)
    {
        var result = await Cli.Wrap("pac")
            .WithArguments("admin list --json")
            .ExecuteBufferedAsync(cancellationToken);

        return JsonSerializer.Deserialize<List<EnvironmentInfo>>(result.StandardOutput) ?? new List<EnvironmentInfo>();
    }

    public static async Task<EnvironmentInfo?> GetEnvironmentInfoByUrlAsync(string environmentUrl, CancellationToken cancellationToken = default)
    {
        var environments = await GetEnvironmentsAsync(cancellationToken);
        return environments.FirstOrDefault(e => e.EnvironmentUrl?.Equals(environmentUrl, StringComparison.OrdinalIgnoreCase) == true);
    }

    public static EnvironmentUrlParts GetPartsFromEnvUrl(string envUrl)
    {
        var regex = new Regex(@"^https://([^.]+)\.([^.]+\.[^.]+\.[a-z]+)(?:/|$)");
        var match = regex.Match(envUrl);

        if (!match.Success)
        {
            Console.Error.WriteLine($"Could not extract environment name and region domain from URL: {envUrl}");
            Environment.Exit(1);
        }

        var organization = match.Groups[1].Value;
        var host = match.Groups[2].Value;

        var hostToRegion = new Dictionary<string, string>
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

        if (!hostToRegion.TryGetValue(host, out var region))
        {
            Console.Error.WriteLine($"Unknown region/domain: {host}");
            Environment.Exit(1);
        }

        return new EnvironmentUrlParts
        {
            Organization = organization,
            Host = host,
            Region = region
        };
    }
}

public class EnvironmentInfo
{
    public Guid EnvironmentId { get; set; }
    public string? EnvironmentUrl { get; set; }
    public Guid OrganizationId { get; set; }
    public string? DisplayName { get; set; }
    public string? Type { get; set; }
    public string? DomainName { get; set; }
    public string? Version { get; set; }
}

public class EnvironmentUrlParts
{
    public string Organization { get; set; } = null!;
    public string Host { get; set; } = null!;
    public string Region { get; set; } = null!;
}
